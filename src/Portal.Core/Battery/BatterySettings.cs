namespace Portal.Core.Battery;

/// <summary>Persisted battery-care settings (the app's own settings file, see <see cref="BatterySettingsStore"/>).</summary>
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

    /// <summary>Notify once each time the battery reaches the limit (in Monitor mode: the unplug reminder at <see cref="Limit"/>).</summary>
    public bool NotifyWhenLimitReached { get; set; } = true;

    /// <summary>Notify on battery at <see cref="LowReminderPercent"/>.</summary>
    public bool NotifyPlugIn { get; set; } = true;

    /// <summary>Notify when the app ends a top-up and the limit is back on.</summary>
    public bool NotifyTopUpFinished { get; set; } = true;

    /// <summary>When the user finished or skipped the welcome flow. Null means it is still to be shown.</summary>
    public DateTimeOffset? FirstRunCompletedUtc { get; set; }

    /// <summary>"Send anonymous crash reports". Off until the user turns it on (welcome flow or Settings); nothing is sent without it.</summary>
    public bool SendCrashReports { get; set; }

    public CalibrationProgress Calibration { get; set; } = new();

    public Portal.Core.Theming.AppearanceSettings Appearance { get; set; } = new();

    public ScheduledTopUpSettings ScheduledTopUp { get; set; } = new();

    /// <summary>Order and visibility of the tray pop-up tiles (see <see cref="Portal.Core.Tray.TrayTileLayout"/>); empty = default.</summary>
    public string TrayTiles { get; set; } = "";

    /// <summary>Remind to calibrate when the last calibration is older than this many days (0 = off).</summary>
    public int CalibrationReminderDays { get; set; }

    public DateTimeOffset? LastCalibrationReminderUtc { get; set; }

    /// <summary>
    /// "Help test charge limit on other brands". Off by default. It counts only together with
    /// <see cref="BetaOptInConfirmedUtc"/>, which is set when the user accepts the warning; <see cref="Normalize"/> turns it
    /// off otherwise. <see cref="WriteGate"/> reads it; it never changes the verified-models list.
    /// </summary>
    public bool BetaOtherBrandsOptIn { get; set; }

    /// <summary>When the user confirmed the beta warning. Null means never confirmed.</summary>
    public DateTimeOffset? BetaOptInConfirmedUtc { get; set; }

    /// <summary>Turns the beta opt-in on. Call only after the user accepted the warning.</summary>
    public void ConfirmBetaOptIn(DateTimeOffset nowUtc)
    {
        BetaOtherBrandsOptIn = true;
        BetaOptInConfirmedUtc = nowUtc;
    }

    public void TurnOffBetaOptIn()
    {
        BetaOtherBrandsOptIn = false;
        BetaOptInConfirmedUtc = null;
    }

    /// <summary>
    /// Not used: the original firmware values live only in <see cref="OriginalStateStore"/>, written by
    /// <see cref="ChargeLimitController"/>. Kept so a settings file from an earlier build loses nothing when saved.
    /// </summary>
    public BackendOriginalState? OriginalHardwareState { get; set; }

    /// <summary>Clamps every value into its valid range.</summary>
    public BatterySettings Normalize()
    {
        if (BetaOptInConfirmedUtc is null) BetaOtherBrandsOptIn = false;
        if (!BetaOtherBrandsOptIn) BetaOptInConfirmedUtc = null;
        Limit = Math.Clamp(Limit, MinLimit, MaxLimit);
        SailingRange = Math.Clamp(SailingRange, SailingMath.MinRange, SailingMath.MaxRange);
        LowReminderPercent = Math.Clamp(LowReminderPercent, 5, 50);
        HeatProtection ??= new HeatProtectionSettings();
        HeatProtection.MaxTempC = Math.Clamp(HeatProtection.MaxTempC, 30, 60);
        Calibration ??= new CalibrationProgress();
        Appearance = (Appearance ?? new Portal.Core.Theming.AppearanceSettings()).Normalize();
        TrayTiles ??= "";
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

    /// <summary>End a running top-up early while the battery is above <see cref="MaxTempC"/>. The only automatic heat action.</summary>
    public bool EndTopUpWhenHot { get; set; } = true;
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

