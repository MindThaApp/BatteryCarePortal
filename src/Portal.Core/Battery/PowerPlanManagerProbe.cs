using System.Diagnostics;
using System.Text.Json;

namespace Portal.Core.Battery;

public sealed record PowerPlanManagerStatus(bool Installed, bool Running, bool ChargeControlOn, bool SettingsFound)
{
    public static PowerPlanManagerStatus None { get; } = new(false, false, false, false);

    /// <summary>Power Plan Manager is installed with its battery care turned on, so it may write the limit.</summary>
    public bool OwnsCharging => Installed && ChargeControlOn;
}

/// <summary>
/// Read-only check for the one-owner rule. Power Plan Manager (Pro and Lite) keeps its battery settings as a JSON
/// string under "BatterySettings" in its package LocalSettings, and mirrors all LocalSettings to
/// %LOCALAPPDATA%\PowerPlanManagerBackup\&lt;family name&gt;\settings-backup.json (inside its package LocalCache when
/// the write was virtualized). Its charge-care engine writes the limit only while that JSON has "Enabled": true.
/// Nothing here writes, and Power Plan Manager is never changed.
/// </summary>
public sealed class PowerPlanManagerProbe
{
    public const string DisplayName = "Power Plan Manager";
    public const string PackageNamePrefix = "34458MindtheApp.PowerPlanManager";
    public const string ProcessName = "PowerPlanTray";
    private const string BackupFolder = "PowerPlanManagerBackup";
    private const string BackupFile = "settings-backup.json";

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
        try
        {
            string packages = Path.Combine(_localAppData, "Packages");
            string[] families = Directory.Exists(packages)
                ? Directory.GetDirectories(packages, PackageNamePrefix + "*").Select(Path.GetFileName).OfType<string>().ToArray()
                : Array.Empty<string>();
            if (families.Length == 0) return PowerPlanManagerStatus.None;

            bool found = false, on = false;
            foreach (string family in families)
            {
                foreach (string path in new[]
                {
                    Path.Combine(_localAppData, BackupFolder, family, BackupFile),
                    Path.Combine(packages, family, "LocalCache", "Local", BackupFolder, family, BackupFile),
                })
                {
                    if (ReadEnabled(path) is not bool enabled) continue;
                    found = true;
                    on |= enabled;
                }
            }
            return new PowerPlanManagerStatus(true, SafeRunning(), on, found);
        }
        catch (Exception)
        {
            return PowerPlanManagerStatus.None;
        }
    }

    /// <summary>The owner name for <see cref="WriteGateContext.OtherOwner"/>, or null.</summary>
    public string? OtherOwner() => Check().OwnsCharging ? DisplayName : null;

    internal static bool? ReadEnabled(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using JsonDocument backup = JsonDocument.Parse(File.ReadAllText(path));
            if (!backup.RootElement.TryGetProperty("LocalSettings", out JsonElement settings) ||
                !settings.TryGetProperty("BatterySettings", out JsonElement battery) ||
                battery.ValueKind != JsonValueKind.String)
                return false;
            using JsonDocument batteryJson = JsonDocument.Parse(battery.GetString()!);
            return batteryJson.RootElement.TryGetProperty("Enabled", out JsonElement enabled) && enabled.ValueKind == JsonValueKind.True;
        }
        catch (Exception)
        {
            return null;
        }
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
