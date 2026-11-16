namespace Portal.Core.Battery;

/// <summary>Sailing is stored as a difference: charging stops at the limit and resumes below limit minus range.</summary>
public static class SailingMath
{
    public const int MinRange = 1;
    public const int MaxRange = 10;

    public static int Lower(int limit, int range) => limit - Math.Clamp(range, MinRange, MaxRange);

    /// <summary>Converts an absolute resume level (older settings) into a range for the given limit.</summary>
    public static int RangeFor(int limit, int lower) => Math.Clamp(limit - lower, MinRange, MaxRange);

    /// <summary>
    /// The range values a control may offer. The hardware floor (lowest start threshold the firmware accepts)
    /// and the smallest start/stop gap narrow the usual 1-10.
    /// </summary>
    public static (int Min, int Max) Bounds(int limit, int minGap = MinRange, int minStartThreshold = 1)
    {
        int min = Math.Clamp(minGap, MinRange, MaxRange);
        int max = Math.Min(MaxRange, Math.Max(min, limit - Math.Max(1, minStartThreshold)));
        return (min, max);
    }

    public static int ClampRange(int range, int limit, int minGap = MinRange, int minStartThreshold = 1)
    {
        (int min, int max) = Bounds(limit, minGap, minStartThreshold);
        return Math.Clamp(range, min, max);
    }

    /// <summary>Values for the computed line: where charging pauses and where it resumes.</summary>
    public static (int PauseAt, int ResumeBelow) Describe(int limit, int range) => (limit, Lower(limit, range));

    /// <summary>True when the limit is under the 80 % that most batteries are comfortable at.</summary>
    public static bool IsLowLimit(int limit) => limit < 80;
}
