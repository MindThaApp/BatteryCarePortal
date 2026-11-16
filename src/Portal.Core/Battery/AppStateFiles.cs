using System.Text.Json;

namespace Portal.Core.Battery;

/// <summary>
/// Reads and writes the app's own small JSON state files. A write goes to a uniquely named temp file in the same
/// folder and then replaces the target in one move, so a crash never leaves a half-written file.
/// </summary>
public static class AtomicJsonFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public enum ReadStatus { Missing, Ok, Unreadable }

    public static ReadStatus TryRead<T>(string path, out T? value) where T : class
    {
        value = null;
        try
        {
            if (!File.Exists(path)) return ReadStatus.Missing;
            value = JsonSerializer.Deserialize<T>(File.ReadAllText(path));
            return value is null ? ReadStatus.Unreadable : ReadStatus.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            value = null;
            return ReadStatus.Unreadable;
        }
    }

    /// <summary>Replaces <paramref name="path"/> (an app-owned state file) atomically. Throws on I/O failure.</summary>
    public static void Write<T>(string path, T value)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        string temp = Path.Combine(folder, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            // Only the uniquely named temp file this call created, when the move did not happen.
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Loads and saves <see cref="BatterySettings"/> in the app's own settings file.</summary>
public sealed class BatterySettingsStore
{
    public const string FileName = "battery-settings.json";
    private readonly object _gate = new();

    public BatterySettingsStore(string folder) => Path = System.IO.Path.Combine(folder, FileName);

    public string Path { get; }

    /// <summary>Never throws. A missing or unreadable file gives the defaults (beta opt-in off).</summary>
    public BatterySettings Load()
    {
        lock (_gate)
        {
            return AtomicJsonFile.TryRead(Path, out BatterySettings? settings) == AtomicJsonFile.ReadStatus.Ok
                ? settings!.Normalize()
                : new BatterySettings().Normalize();
        }
    }

    public void Save(BatterySettings settings)
    {
        lock (_gate) AtomicJsonFile.Write(Path, settings.Clone().Normalize());
    }

    public BatterySettings Update(Action<BatterySettings> change)
    {
        lock (_gate)
        {
            BatterySettings settings = Load();
            change(settings);
            Save(settings);
            return settings.Clone().Normalize();
        }
    }
}
