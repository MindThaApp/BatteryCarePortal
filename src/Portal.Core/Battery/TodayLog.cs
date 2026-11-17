namespace Portal.Core.Battery;

public enum TodayEventKind { Unplugged, PluggedIn, ChargingStarted, ChargingStopped }

/// <param name="Time">Local time of the sample where the change was first seen.</param>
/// <param name="Percent">Battery level at that sample, when known.</param>
public sealed record TodayEvent(DateTimeOffset Time, TodayEventKind Kind, int? Percent);

/// <summary>Turns the day's recorded samples into a short list of changes. Pure; the samples are only read.</summary>
public static class TodayLog
{
    public static IReadOnlyList<TodayEvent> Build(IReadOnlyList<BatteryHistoryEntry> samples, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var events = new List<TodayEvent>();
        BatteryHistoryEntry? prev = null;
        foreach (BatteryHistoryEntry s in samples.OrderBy(e => e.UnixSeconds))
        {
            if (prev is not null)
            {
                DateTimeOffset local = TimeZoneInfo.ConvertTime(s.Time, zone);
                if (prev.OnAc && !s.OnAc) events.Add(new TodayEvent(local, TodayEventKind.Unplugged, s.Percent));
                else if (!prev.OnAc && s.OnAc) events.Add(new TodayEvent(local, TodayEventKind.PluggedIn, s.Percent));
                if (s.OnAc && !prev.Charging && s.Charging) events.Add(new TodayEvent(local, TodayEventKind.ChargingStarted, s.Percent));
                else if (s.OnAc && prev.Charging && !s.Charging) events.Add(new TodayEvent(local, TodayEventKind.ChargingStopped, s.Percent));
            }
            prev = s;
        }
        return events;
    }

    /// <summary>Start of the local day containing <paramref name="now"/>, as an absolute time.</summary>
    public static DateTimeOffset DayStart(DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        DateTimeOffset local = TimeZoneInfo.ConvertTime(now, zone);
        var midnight = new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
    }
}
