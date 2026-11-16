using System.Collections.Concurrent;
using System.Text.Json;

namespace Portal.Core.Battery;

/// <summary>One lock object per state-file path for the whole process, so every store instance on a file serializes with the others.</summary>
public static class AppStateFileLock
{
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);

    public static object For(string path) => Locks.GetOrAdd(System.IO.Path.GetFullPath(path), _ => new object());
}

/// <summary>
/// The one lock every firmware write in the process takes. Every <see cref="ChargeLimitController"/> uses
/// <see cref="Process"/> unless a test passes its own, so two controllers can never write at the same time.
/// </summary>
public sealed class ChargeWriteLock
{
    public static ChargeWriteLock Process { get; } = new();

    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public bool IsHeld => _semaphore.CurrentCount == 0;

    public bool TryEnter() => _semaphore.Wait(0);

    public Task EnterAsync(CancellationToken cancellationToken = default) => _semaphore.WaitAsync(cancellationToken);

    public void Exit() => _semaphore.Release();
}

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

public enum OriginalCaptureResult
{
    /// <summary>The captured values were saved now (the first write on this device).</summary>
    Saved,
    /// <summary>Originals for this backend already existed and were kept as they are.</summary>
    AlreadyCaptured,
    /// <summary>The originals file exists but cannot be read. It is left untouched and writes are refused.</summary>
    FileUnreadable,
    /// <summary>The captured values are incomplete (for example the firmware could not be read).</summary>
    Incomplete,
    /// <summary>The file could not be written.</summary>
    WriteFailed,
}

/// <summary>
/// The firmware values found before the app's first write on this device, per backend, in an app-owned file.
/// An entry is written once and never overwritten; "Restore laptop defaults" puts these values back.
/// </summary>
public sealed class OriginalStateStore
{
    public const string FileName = "charge-originals.json";
    private readonly object _gate;

    public OriginalStateStore(string folder)
    {
        Path = System.IO.Path.Combine(folder, FileName);
        _gate = AppStateFileLock.For(Path);
    }

    public string Path { get; }

    public sealed class OriginalsFile
    {
        public int Version { get; set; } = 1;
        public List<BackendOriginalState> Originals { get; set; } = new();
    }

    /// <summary>Returns the saved originals for a backend, or null. <paramref name="unreadable"/> is true when the file exists but cannot be read.</summary>
    public BackendOriginalState? Get(string backendId, out bool unreadable)
    {
        lock (_gate)
        {
            AtomicJsonFile.ReadStatus status = AtomicJsonFile.TryRead(Path, out OriginalsFile? file);
            unreadable = status == AtomicJsonFile.ReadStatus.Unreadable;
            return status == AtomicJsonFile.ReadStatus.Ok
                ? file!.Originals?.FirstOrDefault(o => o.BackendId == backendId)
                : null;
        }
    }

    /// <summary>Saves <paramref name="captured"/> only when nothing is stored yet for its backend.</summary>
    /// <param name="isComplete">Whether the captured values are enough to restore from.</param>
    public OriginalCaptureResult SaveIfAbsent(BackendOriginalState captured, Func<BackendOriginalState, bool> isComplete)
    {
        lock (_gate)
        {
            AtomicJsonFile.ReadStatus status = AtomicJsonFile.TryRead(Path, out OriginalsFile? file);
            if (status == AtomicJsonFile.ReadStatus.Unreadable) return OriginalCaptureResult.FileUnreadable;
            file ??= new OriginalsFile();
            file.Originals ??= new();
            if (file.Originals.Any(o => o.BackendId == captured.BackendId)) return OriginalCaptureResult.AlreadyCaptured;
            if (string.IsNullOrEmpty(captured.BackendId) || captured.Values.Count == 0 || !isComplete(captured))
                return OriginalCaptureResult.Incomplete;

            file.Originals.Add(captured);
            try { AtomicJsonFile.Write(Path, file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return OriginalCaptureResult.WriteFailed; }
            return OriginalCaptureResult.Saved;
        }
    }
}

/// <summary>
/// What the app means the charge limit to be. Saved before every change, so a quit, crash or sleep can be followed by
/// putting the steady-state limit back.
/// </summary>
public sealed record ChargeIntent
{
    public int Version { get; init; } = 1;
    public string BackendId { get; init; } = string.Empty;
    /// <summary>The app set the limit and owns it. False after Restore laptop defaults.</summary>
    public bool Managed { get; init; }
    /// <summary>The steady-state limit is on.</summary>
    public bool LimitOn { get; init; }
    /// <summary>The steady-state limit value (100 when off).</summary>
    public int Limit { get; init; } = 100;
    /// <summary>A temporary top-up has the limit off on purpose; the steady-state limit must come back after it.</summary>
    public bool TopUpActive { get; init; }
    public DateTimeOffset? TopUpUntil { get; init; }
    /// <summary>An unattended re-apply could not run (elevation needed, timed out, refused). The next start or the user fixes it.</summary>
    public bool NeedsReapply { get; init; }
    /// <summary>A re-apply was attempted and failed. It is never retried automatically; the user puts the limit back.</summary>
    public bool ReapplyFailed { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
}

public sealed class ChargeIntentStore
{
    public const string FileName = "charge-intent.json";
    private readonly object _gate;

    public ChargeIntentStore(string folder)
    {
        Path = System.IO.Path.Combine(folder, FileName);
        _gate = AppStateFileLock.For(Path);
    }

    public string Path { get; }

    public AtomicJsonFile.ReadStatus Load(out ChargeIntent? intent)
    {
        lock (_gate) return AtomicJsonFile.TryRead(Path, out intent);
    }

    /// <summary>False when the file could not be written.</summary>
    public bool TrySave(ChargeIntent intent)
    {
        lock (_gate)
        {
            try { AtomicJsonFile.Write(Path, intent); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>
    /// Renames the intent file to a unique "&lt;name&gt;.corrupt-&lt;timestamp&gt;" next to it when it cannot be read or
    /// <paramref name="isUsable"/> rejects it. Never deletes and never overwrites: an existing name gets " (2)", " (3)" and so on.
    /// Returns the new path, or null when the file is missing or fine. Throws on I/O failure.
    /// </summary>
    public string? QuarantineIfUnusable(Func<ChargeIntent, bool> isUsable, DateTimeOffset now)
    {
        lock (_gate)
        {
            AtomicJsonFile.ReadStatus status = AtomicJsonFile.TryRead(Path, out ChargeIntent? intent);
            if (status == AtomicJsonFile.ReadStatus.Missing) return null;
            if (status == AtomicJsonFile.ReadStatus.Ok && isUsable(intent!)) return null;

            string stem = $"{Path}.corrupt-{now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture)}";
            string target = stem;
            for (int n = 2; File.Exists(target) || Directory.Exists(target); n++) target = $"{stem} ({n})";
            // Same-folder rename of the app's own file; overwrite: false so a name that appeared meanwhile is never replaced.
            File.Move(Path, target, overwrite: false);
            return target;
        }
    }
}

/// <summary>Loads and saves <see cref="BatterySettings"/> in the app's own settings file.</summary>
public sealed class BatterySettingsStore
{
    public const string FileName = "battery-settings.json";
    private readonly object _gate;

    public BatterySettingsStore(string folder)
    {
        Path = System.IO.Path.Combine(folder, FileName);
        _gate = AppStateFileLock.For(Path);
    }

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
