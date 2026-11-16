using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Portal.Core.Battery;

public sealed record BatteryHistoryEntry
{
    [JsonPropertyName("t")] public long UnixSeconds { get; init; }
    [JsonPropertyName("p")] public int? Percent { get; init; }
    [JsonPropertyName("c")] public bool Charging { get; init; }
    [JsonPropertyName("ac")] public bool OnAc { get; init; }
    /// <summary>Battery watts, positive charging, negative discharging; null when not measured.</summary>
    [JsonPropertyName("w")] public double? BatteryW { get; init; }
    [JsonPropertyName("sw")] public double? SystemW { get; init; }
    [JsonPropertyName("tc")] public double? TemperatureC { get; init; }

    [JsonIgnore] public DateTimeOffset Time => DateTimeOffset.FromUnixTimeSeconds(UnixSeconds);
}

public sealed record BatteryHealthPoint(DateOnly Date, double HealthPercent, int? CycleCount);

/// <summary>
/// Records one sample per minute as JSON lines, one file per UTC day under <c>battery-history</c>,
/// keeps the last 30 days, and keeps one health value per day for the health trend. Never throws.
/// </summary>
public sealed class BatteryHistoryLogger
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);
    public const int DefaultRetentionDays = 30;
    public const int MaxHealthPoints = 1100;
    private const string HealthFileName = "health-trend.json";

    private readonly string _folder;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly int _retentionDays;
    private readonly object _gate = new();
    private DateTimeOffset? _lastRecord;
    private DateOnly? _lastPrunedDay;
    private BatteryHealthPoint? _lastHealthWritten;

    public BatteryHistoryLogger(string localStateFolder, TimeProvider? time = null, TimeSpan? interval = null,
        int retentionDays = DefaultRetentionDays)
    {
        _folder = Path.Combine(localStateFolder, "battery-history");
        _time = time ?? TimeProvider.System;
        _interval = interval ?? DefaultInterval;
        _retentionDays = Math.Max(1, retentionDays);
    }

    public string Folder => _folder;

    /// <summary>Appends a sample when at least the interval has passed since the last one. Returns true when written.</summary>
    public bool Record(BatterySnapshot snapshot, PowerFlowSnapshot? flow = null)
    {
        if (!snapshot.Present) return false;
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_lastRecord is DateTimeOffset last && now - last < _interval) return false;
            try
            {
                Directory.CreateDirectory(_folder);
                var entry = new BatteryHistoryEntry
                {
                    UnixSeconds = now.ToUnixTimeSeconds(),
                    Percent = snapshot.Percent,
                    Charging = snapshot.IsCharging,
                    OnAc = snapshot.IsOnAc,
                    BatteryW = flow?.BatteryW.HasValue == true ? flow.BatteryW.Watts : null,
                    SystemW = flow?.SystemW.HasValue == true ? flow.SystemW.Watts : null,
                    TemperatureC = snapshot.TemperatureC,
                };
                File.AppendAllText(DayFile(DateOnly.FromDateTime(now.UtcDateTime)), JsonSerializer.Serialize(entry) + "\n");
                _lastRecord = now;
                if (snapshot.HealthPercent is double health) RecordHealth(DateOnly.FromDateTime(now.UtcDateTime), health, snapshot.CycleCount);
                PruneIfNewDay(DateOnly.FromDateTime(now.UtcDateTime));
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>Samples between two times, oldest first. Corrupt lines are skipped.</summary>
    public IReadOnlyList<BatteryHistoryEntry> Read(DateTimeOffset from, DateTimeOffset to)
    {
        var entries = new List<BatteryHistoryEntry>();
        lock (_gate)
        {
            for (DateOnly day = DateOnly.FromDateTime(from.UtcDateTime); day <= DateOnly.FromDateTime(to.UtcDateTime); day = day.AddDays(1))
            {
                string file = DayFile(day);
                if (!File.Exists(file)) continue;
                try
                {
                    foreach (string line in File.ReadLines(file))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        BatteryHistoryEntry? entry = null;
                        try { entry = JsonSerializer.Deserialize<BatteryHistoryEntry>(line); }
                        catch (JsonException) { }
                        if (entry is not null && entry.Time >= from && entry.Time <= to) entries.Add(entry);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return entries;
    }

    public IReadOnlyList<BatteryHealthPoint> ReadHealthTrend()
    {
        lock (_gate) { return LoadHealth(); }
    }

    public void Clear()
    {
        lock (_gate)
        {
            // Only the files this logger writes; the folder itself and anything else in it are left alone.
            try
            {
                if (Directory.Exists(_folder))
                {
                    foreach (string file in Directory.EnumerateFiles(_folder, "????-??-??.jsonl")) File.Delete(file);
                    foreach (string name in new[] { HealthFileName, HealthFileName + ".tmp" })
                    {
                        string path = Path.Combine(_folder, name);
                        if (File.Exists(path)) File.Delete(path);
                    }
                    if (!Directory.EnumerateFileSystemEntries(_folder).Any()) Directory.Delete(_folder);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _lastRecord = null;
            _lastHealthWritten = null;
        }
    }

    private void RecordHealth(DateOnly day, double health, int? cycles)
    {
        var latest = new BatteryHealthPoint(day, health, cycles);
        if (_lastHealthWritten == latest) return;
        List<BatteryHealthPoint> points = LoadHealth().Where(p => p.Date != day).ToList();
        _lastHealthWritten = latest;
        points.Add(new BatteryHealthPoint(day, health, cycles));
        points = points.OrderBy(p => p.Date).TakeLast(MaxHealthPoints).ToList();
        string path = Path.Combine(_folder, HealthFileName);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(points));
        File.Move(temp, path, overwrite: true);
    }

    private List<BatteryHealthPoint> LoadHealth()
    {
        string path = Path.Combine(_folder, HealthFileName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<BatteryHealthPoint>>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    private void PruneIfNewDay(DateOnly today)
    {
        if (_lastPrunedDay == today) return;
        _lastPrunedDay = today;
        DateOnly oldestKept = today.AddDays(-(_retentionDays - 1));
        foreach (string file in Directory.EnumerateFiles(_folder, "????-??-??.jsonl"))
        {
            if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateOnly day) && day < oldestKept)
            {
                try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private string DayFile(DateOnly day) =>
        Path.Combine(_folder, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
}
