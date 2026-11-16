namespace Portal.Core.Battery;

/// <summary>Persisted battery-care settings (one JSON value in LocalSettings).</summary>
public sealed class BatterySettings
{
    public const int MinLimit = 50;
    public const int MaxLimit = 100;
    public const int MaxSailingRange = 10;

    /// <summary>Charge care (hardware limit or software reminders at the limit) is on.</summary>
    public bool Enabled { get; set; }

    /// <summary>Stop charging at this level (50–100).</summary>
    public int Limit { get; set; } = 80;

    /// <summary>
    /// Charging resumes only below this level. It is always <see cref="Limit"/> minus <see cref="SailingRange"/>, so it
    /// follows the limit. Older saved settings hold only this absolute value; setting it converts it to a range.
    /// </summary>
    public int SailingLower
    {
        get => SailingMath.Lower(Limit, SailingRange);
        set => SailingRange = SailingMath.RangeFor(Limit, value);
    }

    /// <summary>How far the level may fall below the limit before charging resumes, in percentage points (1-10).</summary>
    public int SailingRange { get; set; } = 5;

    /// <summary>While set and in the future, charge to 100 % once.</summary>
    public DateTimeOffset? TopUpUntil { get; set; }

    public HeatProtectionSettings HeatProtection { get; set; } = new();

    /// <summary>"Unplug now" / "Plug in" notifications (Lite and Pro).</summary>
    public bool RemindersEnabled { get; set; } = true;

    /// <summary>"Plug in" reminder level on battery.</summary>
    public int LowReminderPercent { get; set; } = 20;

    /// <summary>Notify once each time the battery reaches the limit.</summary>
    public bool NotifyWhenLimitReached { get; set; } = true;

    public CalibrationProgress Calibration { get; set; } = new();

    public ScheduledTopUpSettings ScheduledTopUp { get; set; } = new();

    /// <summary>Remind to calibrate when the last calibration is older than this many days (0 = off).</summary>
    public int CalibrationReminderDays { get; set; }

    public DateTimeOffset? LastCalibrationReminderUtc { get; set; }

    /// <summary>
    /// Stub for "Help test charge limit on other brands". Always off: <see cref="Normalize"/> resets it, and no write path reads
    /// it. The verified-models list is the only thing that allows a write.
    /// </summary>
    public bool BetaOtherBrandsOptIn { get; set; }

    /// <summary>Firmware values found before the first write, for "Restore defaults".</summary>
    public BackendOriginalState? OriginalHardwareState { get; set; }

    /// <summary>Clamps every value into its valid range.</summary>
    public BatterySettings Normalize()
    {
        BetaOtherBrandsOptIn = false;
        Limit = Math.Clamp(Limit, MinLimit, MaxLimit);
        SailingRange = Math.Clamp(SailingRange, SailingMath.MinRange, SailingMath.MaxRange);
        LowReminderPercent = Math.Clamp(LowReminderPercent, 5, 50);
        HeatProtection ??= new HeatProtectionSettings();
        HeatProtection.MaxTempC = Math.Clamp(HeatProtection.MaxTempC, 30, 60);
        Calibration ??= new CalibrationProgress();
        ScheduledTopUp ??= new ScheduledTopUpSettings();
        ScheduledTopUp.LeadHours = Math.Clamp(ScheduledTopUp.LeadHours, 1, 12);
        CalibrationReminderDays = CalibrationReminderDays <= 0 ? 0 : Math.Clamp(CalibrationReminderDays, 7, 180);
        return this;
    }

    public BatterySettings Clone() =>
        System.Text.Json.JsonSerializer.Deserialize<BatterySettings>(System.Text.Json.JsonSerializer.Serialize(this))!;
}

public sealed class HeatProtectionSettings
{
    public bool Enabled { get; set; }
    public double MaxTempC { get; set; } = 40;
}

public enum CalibrationStep
{
    NotRunning,
    ChargeToFull,
    DischargeToLow,
    RechargeToFull,
}

public sealed class CalibrationProgress
{
    public const int LowTarget = 15;
    public CalibrationStep Step { get; set; }
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? LastCompletedUtc { get; set; }
}

/// <summary>Top up to 100 % before a weekly time, for example every Friday 07:00 before travel.</summary>
public sealed class ScheduledTopUpSettings
{
    public bool Enabled { get; set; }
    public List<DayOfWeek> Days { get; set; } = new() { DayOfWeek.Friday };
    /// <summary>Local time of day the battery should be full by.</summary>
    public TimeSpan ReadyBy { get; set; } = new(7, 0, 0);
    /// <summary>How long before <see cref="ReadyBy"/> charging to 100 % starts.</summary>
    public int LeadHours { get; set; } = 3;
    /// <summary>When the schedule last started a top-up, so a cancelled top-up is not restarted in the same window.</summary>
    public DateTimeOffset? LastTriggeredUtc { get; set; }
}

