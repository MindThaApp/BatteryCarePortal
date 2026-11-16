namespace Portal.Core.Power;

/// <summary>One reading of the whole PC's power at the wall socket, taken by an external meter.</summary>
/// <param name="Watts">Real power drawn from the socket.</param>
/// <param name="At">When the meter took the reading.</param>
/// <param name="SourceName">Display name of the meter.</param>
public sealed record WallReading(double Watts, DateTimeOffset At, string SourceName);

/// <summary>Anything that can provide the latest wall reading (a smart plug, a test fake).</summary>
public interface IWallReadingSource
{
    /// <summary>The newest reading, or null when the meter never reported one.</summary>
    WallReading? Latest { get; }

    /// <summary>Raised after <see cref="Latest"/> changes. May be raised on any thread.</summary>
    event EventHandler? Updated;
}
