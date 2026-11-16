using System.Diagnostics;
using System.Text.Json;

namespace Portal.Core.Battery;

/// <summary>What Power Plan Manager's settings mirror says about its charge control.</summary>
public enum PowerPlanManagerChargeControl
{
    /// <summary>No settings mirror was found.</summary>
    Absent,
    /// <summary>A mirror exists but could not be read or understood (locked, mid-write, unknown format).</summary>
    Unreadable,
    /// <summary>The mirror says charge control is off ("Enabled": false).</summary>
    Disabled,
    /// <summary>The mirror says charge control is on.</summary>
    Enabled,
}

public sealed record PowerPlanManagerStatus(bool Installed, bool Running, PowerPlanManagerChargeControl ChargeControl)
{
    public static PowerPlanManagerStatus None { get; } = new(false, false, PowerPlanManagerChargeControl.Absent);

    /// <summary>Power Plan Manager is installed with its battery care turned on, so it may write the limit.</summary>
    public bool OwnsCharging => Installed && ChargeControl == PowerPlanManagerChargeControl.Enabled;

    /// <summary>True only when Power Plan Manager is not installed or its charge control is confirmed off.</summary>
    public bool ConfirmedOff => !Installed || ChargeControl == PowerPlanManagerChargeControl.Disabled;
}

/// <summary>
/// Read-only check for the one-owner rule. Power Plan Manager (Pro and Lite) keeps its battery settings as a JSON
/// string under "BatterySettings" in its package LocalSettings, and mirrors all LocalSettings to
/// %LOCALAPPDATA%\PowerPlanManagerBackup\&lt;family name&gt;\settings-backup.json (inside its package LocalCache when
/// the write was virtualized). Its charge-care engine writes the limit only while that JSON has "Enabled": true.
/// Fails closed: when Power Plan Manager is installed, only an explicit "Enabled": false lets this app write.
/// Nothing here writes, and Power Plan Manager is never changed.
/// </summary>
public sealed class PowerPlanManagerProbe
{
    public const string DisplayName = "Power Plan Manager";
    public const string PackageNamePrefix = "34458MindtheApp.PowerPlanManager";
    public const string ProcessName = "PowerPlanTray";
    private const string BackupFolder = "PowerPlanManagerBackup";
    private const string BackupFile = "settings-backup.json";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(150);

    private readonly string _localAppData;
    private readonly Func<bool> _isRunning;

    public PowerPlanManagerProbe(string? localAppData = null, Func<bool>? isRunning = null)
    {
        _localAppData = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _isRunning = isRunning ?? IsProcessRunning;
    }

    /// <summary>Never throws.</summary>
    public PowerPlanManagerStatus Check()
    {
        string packages = Path.Combine(_localAppData, "Packages");
        string[] families;
        try
        {
            families = Directory.Exists(packages)
                ? Directory.GetDirectories(packages, PackageNamePrefix + "*").Select(Path.GetFileName).OfType<string>().ToArray()
                : Array.Empty<string>();
        }
        catch (Exception)
        {
            // Whether it is installed cannot be told, so it cannot be confirmed off.
            return new PowerPlanManagerStatus(true, SafeRunning(), PowerPlanManagerChargeControl.Unreadable);
        }
        if (families.Length == 0) return PowerPlanManagerStatus.None;

        var seen = new List<PowerPlanManagerChargeControl>();
        foreach (string family in families)
        {
            seen.Add(ReadChargeControl(Path.Combine(_localAppData, BackupFolder, family, BackupFile)));
            seen.Add(ReadChargeControl(Path.Combine(packages, family, "LocalCache", "Local", BackupFolder, family, BackupFile)));
        }
        return new PowerPlanManagerStatus(true, SafeRunning(), Combine(seen));
    }

    /// <summary>
    /// For <see cref="WriteGateContext.OtherOwner"/>: null only when Power Plan Manager is absent or its charge control
    /// is confirmed off; otherwise a claim that refuses writes.
    /// </summary>
    public OtherOwnerClaim? OtherOwner()
    {
        PowerPlanManagerStatus status = Check();
        if (status.ConfirmedOff) return null;
        return new OtherOwnerClaim(DisplayName, Confirmed: status.OwnsCharging);
    }

    /// <summary>On means on anywhere; otherwise any doubt wins over a confirmed off.</summary>
    private static PowerPlanManagerChargeControl Combine(IEnumerable<PowerPlanManagerChargeControl> values)
    {
        var all = values.ToList();
        if (all.Contains(PowerPlanManagerChargeControl.Enabled)) return PowerPlanManagerChargeControl.Enabled;
        if (all.Contains(PowerPlanManagerChargeControl.Unreadable)) return PowerPlanManagerChargeControl.Unreadable;
        if (all.Contains(PowerPlanManagerChargeControl.Disabled)) return PowerPlanManagerChargeControl.Disabled;
        return PowerPlanManagerChargeControl.Absent;
    }

    /// <summary>Reads one mirror. A locked or half-written file is read once more after a short pause.</summary>
    public static PowerPlanManagerChargeControl ReadChargeControl(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return PowerPlanManagerChargeControl.Absent;
                return Parse(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is IOException or JsonException && attempt == 0)
            {
                Thread.Sleep(RetryDelay);
            }
            catch (Exception)
            {
                return PowerPlanManagerChargeControl.Unreadable;
            }
        }
    }

    private static PowerPlanManagerChargeControl Parse(string text)
    {
        using JsonDocument backup = JsonDocument.Parse(text);
        if (backup.RootElement.ValueKind != JsonValueKind.Object ||
            !backup.RootElement.TryGetProperty("LocalSettings", out JsonElement settings) ||
            settings.ValueKind != JsonValueKind.Object)
            return PowerPlanManagerChargeControl.Unreadable;
        // Power Plan Manager's BatterySettings.Enabled defaults to false, so a readable mirror with no battery settings means off.
        if (!settings.TryGetProperty("BatterySettings", out JsonElement battery))
            return PowerPlanManagerChargeControl.Disabled;
        if (battery.ValueKind != JsonValueKind.String)
            return PowerPlanManagerChargeControl.Unreadable;

        using JsonDocument batteryJson = JsonDocument.Parse(battery.GetString()!);
        if (batteryJson.RootElement.ValueKind != JsonValueKind.Object)
            return PowerPlanManagerChargeControl.Unreadable;
        if (!batteryJson.RootElement.TryGetProperty("Enabled", out JsonElement enabled))
            return PowerPlanManagerChargeControl.Disabled;
        return enabled.ValueKind switch
        {
            JsonValueKind.True => PowerPlanManagerChargeControl.Enabled,
            JsonValueKind.False => PowerPlanManagerChargeControl.Disabled,
            _ => PowerPlanManagerChargeControl.Unreadable,
        };
    }

    private bool SafeRunning()
    {
        try { return _isRunning(); }
        catch (Exception) { return false; }
    }

    private static bool IsProcessRunning()
    {
        Process[] processes = Process.GetProcessesByName(ProcessName);
        try { return processes.Length > 0; }
        finally { foreach (Process p in processes) p.Dispose(); }
    }
}
