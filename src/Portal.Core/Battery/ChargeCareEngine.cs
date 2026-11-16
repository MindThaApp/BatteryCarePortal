namespace Portal.Core.Battery;

public enum ChargeCareMode
{
    NoBattery,
    /// <summary>Charge care is off; only the low-battery reminder can fire.</summary>
    Disabled,
    /// <summary>No usable hardware control (or Lite): reminders only.</summary>
    Software,
    /// <summary>A backend applies the limit, start threshold and pause.</summary>
    Hardware,
}

public enum ChargeCareStatus
{
    Idle,
    Charging,
    HoldingAtLimit,
    Sailing,
    Discharging,
    TopUp,
    HeatPaused,
    ManuallyPaused,
    Calibrating,
}

public enum ChargeCareReminderKind
{
    UnplugNow,
    PlugIn,
    LimitReached,
    TooHot,
    TopUpFinished,
    CalibrationStepChanged,
    CalibrationCompleted,
    CalibrationDue,
}

public sealed record ChargeCareReminder(ChargeCareReminderKind Kind, int? Percent, CalibrationStep? Step = null, double? TemperatureC = null);

public sealed record ChargeCareEvaluation
{
    public ChargeCareMode Mode { get; init; }
    public ChargeCareStatus Status { get; init; }
    /// <summary>The level charging stops at right now (100 during top-up and calibration).</summary>
    public int EffectiveLimit { get; init; } = 100;
    /// <summary>The full hardware target, or null in software mode.</summary>
    public ChargeLimitRequest? DesiredHardware { get; init; }
    /// <summary>A hardware change that still has to be applied, or null when the device already matches.</summary>
    public ChargeLimitRequest? PendingHardware { get; init; }
    public bool HeatPauseActive { get; init; }
    public TimeSpan? TimeToLimit { get; init; }
    public TimeSpan? TimeToFull { get; init; }
    public IReadOnlyList<ChargeCareReminder> Reminders { get; init; } = Array.Empty<ChargeCareReminder>();
}

/// <summary>
/// Turns battery snapshots and <see cref="BatterySettings"/> into hardware targets and reminders.
/// Not thread-safe: call it from one thread (for example the snapshot callback, marshalled to the UI thread).
/// Hardware writes happen automatically only when the backend needs no administrator prompt; otherwise
/// <see cref="HardwareChangeRequired"/> asks the UI to confirm and call <see cref="ApplyNowAsync"/>.
/// </summary>
public sealed class ChargeCareEngine
{
    public const double HeatHysteresisC = 3;
    public const int LowReminderRearmGap = 5;
    public static readonly TimeSpan DefaultTopUpDuration = TimeSpan.FromHours(12);
    public static readonly TimeSpan ScheduledTopUpGrace = TimeSpan.FromHours(2);

    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private BatterySettings _settings;
    private ChargeLimitRequest? _lastApplied;
    private ChargeLimitRequest? _lastRequired;
    private bool _unplugArmed = true;
    private bool _plugInArmed = true;
    private bool _limitReachedArmed = true;
    private bool _hot;
    private bool _hotReminderSent;
    private bool _sailingPaused;
    private bool _manualPause;
    private bool _forceReapply;

    public ChargeCareEngine(IChargeLimitBackend? backend, BatterySettings settings, TimeProvider? time = null,
        bool hardwareFeaturesAllowed = true)
    {
        Backend = backend;
        _settings = settings.Clone().Normalize();
        _time = time ?? TimeProvider.System;
        HardwareFeaturesAllowed = hardwareFeaturesAllowed;
    }

    public IChargeLimitBackend? Backend { get; }

    /// <summary>False in Lite: hardware limit, sailing, top-up, heat protection and calibration stay read-only.</summary>
    public bool HardwareFeaturesAllowed { get; }

    public BatterySettings Settings => _settings.Clone();
    public BatterySnapshot LastSnapshot { get; private set; } = BatterySnapshot.NotPresent;
    public ChargeCareEvaluation Last { get; private set; } = new() { Mode = ChargeCareMode.NoBattery };
    public bool IsManuallyPaused => _manualPause;
    public bool CanPauseCharging => Mode == ChargeCareMode.Hardware && Backend!.Capabilities.CanPauseCharging;
    public bool IsTopUpActive => _settings.TopUpUntil is DateTimeOffset until && until > _time.GetUtcNow();

    public ChargeCareMode Mode =>
        !LastSnapshot.Present ? ChargeCareMode.NoBattery
        : !_settings.Enabled ? ChargeCareMode.Disabled
        : Backend is not null && HardwareFeaturesAllowed && Backend.Capabilities is { } c && (c.SupportsLimit || c.CanPauseCharging)
            ? ChargeCareMode.Hardware
            : ChargeCareMode.Software;

    public event EventHandler<ChargeCareEvaluation>? StateChanged;
    public event EventHandler<ChargeCareReminder>? ReminderRaised;
    /// <summary>The engine changed settings itself (top-up ended, calibration step, original values captured); persist them.</summary>
    public event EventHandler<BatterySettings>? SettingsChanged;
    /// <summary>A hardware change needs an administrator prompt; ask the user, then call <see cref="ApplyNowAsync"/>.</summary>
    public event EventHandler<ChargeLimitRequest>? HardwareChangeRequired;
    public event EventHandler<BackendWriteResult>? HardwareApplied;

    public void UpdateSettings(BatterySettings settings)
    {
        _settings = settings.Clone().Normalize();
        if (!_settings.Enabled) _manualPause = false;
    }

    public void StartTopUp(TimeSpan? duration = null)
    {
        if (!HardwareFeaturesAllowed) return;
        _settings.TopUpUntil = _time.GetUtcNow() + (duration ?? DefaultTopUpDuration);
        RaiseSettingsChanged();
    }

    public void CancelTopUp()
    {
        if (_settings.TopUpUntil is null) return;
        _settings.TopUpUntil = null;
        RaiseSettingsChanged();
    }

    public void StartCalibration()
    {
        if (!HardwareFeaturesAllowed) return;
        _settings.Calibration.Step = CalibrationStep.ChargeToFull;
        _settings.Calibration.StartedUtc = _time.GetUtcNow();
        RaiseSettingsChanged();
        Raise(new ChargeCareReminder(ChargeCareReminderKind.CalibrationStepChanged, LastSnapshot.Percent, CalibrationStep.ChargeToFull));
    }

    public void CancelCalibration()
    {
        if (_settings.Calibration.Step == CalibrationStep.NotRunning) return;
        _settings.Calibration.Step = CalibrationStep.NotRunning;
        _settings.Calibration.StartedUtc = null;
        RaiseSettingsChanged();
    }

    /// <summary>"Stop charging now" / "Resume". Only when the backend can pause.</summary>
    public bool SetManualPause(bool paused)
    {
        if (!CanPauseCharging) return false;
        _manualPause = paused;
        return true;
    }

    /// <summary>Evaluates a snapshot and applies hardware changes that need no administrator prompt.</summary>
    public async Task<ChargeCareEvaluation> ProcessAsync(BatterySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ChargeCareEvaluation evaluation = Evaluate(snapshot);
        if (evaluation.PendingHardware is { } pending)
        {
            if (!Backend!.RequiresElevation)
            {
                BackendWriteResult result = await ApplyCoreAsync(pending, allowPrompt: false, cancellationToken).ConfigureAwait(false);
                if (result.NeedsElevation) RequireHardwareChange(pending);
            }
            else
            {
                RequireHardwareChange(pending);
            }
        }
        return evaluation;
    }

    /// <summary>
    /// After the PC wakes: writes the hardware target again through the backend (without an administrator prompt;
    /// a backend that needs one asks through <see cref="HardwareChangeRequired"/>). Does nothing outside hardware mode.
    /// </summary>
    public async Task<ChargeCareEvaluation> ReapplyAfterResumeAsync(CancellationToken cancellationToken = default)
    {
        if (Mode != ChargeCareMode.Hardware || !LastSnapshot.Present) return Last;
        _forceReapply = true;
        _lastApplied = null;
        _lastRequired = null;
        return await ProcessAsync(LastSnapshot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies the current hardware target in one operation (at most one administrator prompt). User-initiated.</summary>
    public async Task<BackendWriteResult> ApplyNowAsync(CancellationToken cancellationToken = default)
    {
        if (Mode != ChargeCareMode.Hardware) return BackendWriteResult.Fail("No hardware charge control is available.");
        ChargeLimitRequest desired = Last.DesiredHardware ?? ComputeDesired(LastSnapshot, _time.GetUtcNow(), out _);
        return await ApplyCoreAsync(desired, allowPrompt: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Puts the firmware values captured before the first write back and turns charge care off.</summary>
    public async Task<BackendWriteResult> RestoreDefaultsAsync(CancellationToken cancellationToken = default)
    {
        if (Backend is null) return BackendWriteResult.Fail("No hardware charge control is available.");
        BackendOriginalState? original = _settings.OriginalHardwareState;
        if (original is null || original.BackendId != Backend.Id)
            return BackendWriteResult.Ok("Nothing was changed on this device, so there is nothing to restore.");

        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BackendWriteResult result = await Backend.RestoreAsync(original, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                _settings.Enabled = false;
                _settings.TopUpUntil = null;
                _settings.Calibration.Step = CalibrationStep.NotRunning;
                _settings.OriginalHardwareState = null;
                _lastApplied = null;
                _lastRequired = null;
                _manualPause = false;
                RaiseSettingsChanged();
            }
            HardwareApplied?.Invoke(this, result);
            return result;
        }
        finally
        {
            _applyGate.Release();
        }
    }

    /// <summary>Pure state update for one snapshot: advances top-up, calibration, heat and reminder state.</summary>
    public ChargeCareEvaluation Evaluate(BatterySnapshot snapshot)
    {
        LastSnapshot = snapshot;
        DateTimeOffset now = _time.GetUtcNow();
        var reminders = new List<ChargeCareReminder>();

        if (!snapshot.Present)
        {
            Last = new ChargeCareEvaluation { Mode = ChargeCareMode.NoBattery, Status = ChargeCareStatus.Idle };
            StateChanged?.Invoke(this, Last);
            return Last;
        }

        int? percent = snapshot.Percent;
        bool proActive = HardwareFeaturesAllowed && _settings.Enabled;

        if (proActive) AdvanceScheduledTopUp(now);
        if (proActive) AdvanceTopUp(now, percent, reminders);
        if (proActive) AdvanceCalibration(now, percent, reminders);
        if (HardwareFeaturesAllowed) AdvanceCalibrationReminder(now, reminders);
        bool heatPause = proActive && AdvanceHeat(snapshot, reminders);

        ChargeCareMode mode = Mode;
        int effectiveLimit = ComputeEffectiveLimit(now);
        ChargeLimitRequest? desired = mode == ChargeCareMode.Hardware ? ComputeDesired(snapshot, now, out effectiveLimit) : null;

        AdvanceReminders(mode, snapshot, effectiveLimit, reminders);

        ChargeLimitRequest? pending = null;
        if (desired is not null && _forceReapply)
        {
            // After sleep the firmware may have lost the values, whatever it reads back: write them again.
            _forceReapply = false;
            pending = desired;
        }
        else if (desired is not null && !SameTarget(desired, _lastApplied))
        {
            ChargeLimitState state = Backend!.GetState();
            if (IsSatisfiedBy(desired, state)) _lastApplied = desired;
            else pending = desired;
        }

        Last = new ChargeCareEvaluation
        {
            Mode = mode,
            Status = ComputeStatus(snapshot, mode, effectiveLimit, heatPause),
            EffectiveLimit = effectiveLimit,
            DesiredHardware = desired,
            PendingHardware = pending,
            HeatPauseActive = heatPause,
            TimeToLimit = effectiveLimit < 100 ? BatteryEstimates.TimeToPercent(snapshot, effectiveLimit) : null,
            TimeToFull = BatteryEstimates.TimeToPercent(snapshot, 100),
            Reminders = reminders,
        };

        foreach (ChargeCareReminder reminder in reminders) Raise(reminder);
        StateChanged?.Invoke(this, Last);
        return Last;
    }

    private int ComputeEffectiveLimit(DateTimeOffset now)
    {
        if (!_settings.Enabled) return 100;
        if (HardwareFeaturesAllowed && (_settings.Calibration.Step != CalibrationStep.NotRunning || IsTopUpActive)) return 100;
        return _settings.Limit;
    }

    private ChargeLimitRequest ComputeDesired(BatterySnapshot snapshot, DateTimeOffset now, out int effectiveLimit)
    {
        ChargeLimitCapabilities caps = Backend!.Capabilities;
        int target = ComputeEffectiveLimit(now);
        bool normal = target == _settings.Limit && target < 100;
        effectiveLimit = caps.SupportsLimit ? caps.Snap(target) ?? target : target;

        int? start = null;
        bool resetStart = false;
        if (caps.SupportsStartThreshold && caps.SupportsLimit)
        {
            if (normal)
            {
                int upper = Math.Min(caps.MaxStartThreshold, effectiveLimit - caps.MinStartStopGap);
                start = Math.Clamp(_settings.SailingLower, caps.MinStartThreshold, Math.Max(caps.MinStartThreshold, upper));
            }
            else
            {
                resetStart = true;
            }
        }

        bool? paused = null;
        if (caps.CanPauseCharging)
        {
            // Without a start threshold, sailing is done by pausing at the limit and resuming below the lower bound.
            if (normal && !caps.SupportsStartThreshold && snapshot.Percent is int p)
            {
                if (p >= target) _sailingPaused = true;
                else if (p < _settings.SailingLower) _sailingPaused = false;
            }
            else
            {
                _sailingPaused = false;
            }
            paused = _manualPause || (_hot && _settings.HeatProtection.Enabled) || _sailingPaused;
        }

        return new ChargeLimitRequest
        {
            Limit = caps.SupportsLimit ? effectiveLimit : null,
            StartThreshold = start,
            ResetStartThreshold = resetStart,
            ChargingPaused = paused,
        };
    }

    private void AdvanceScheduledTopUp(DateTimeOffset now)
    {
        ScheduledTopUpSettings schedule = _settings.ScheduledTopUp;
        if (!schedule.Enabled || schedule.Days.Count == 0) return;
        DateTimeOffset local = TimeZoneInfo.ConvertTime(now, _time.LocalTimeZone);
        foreach (int dayOffset in new[] { 0, 1 })
        {
            DateTime day = local.Date.AddDays(dayOffset);
            if (!schedule.Days.Contains(day.DayOfWeek)) continue;
            var readyLocal = new DateTimeOffset(day + schedule.ReadyBy, _time.LocalTimeZone.GetUtcOffset(day + schedule.ReadyBy));
            DateTimeOffset readyUtc = readyLocal.ToUniversalTime();
            DateTimeOffset startUtc = readyUtc - TimeSpan.FromHours(schedule.LeadHours);
            if (now < startUtc || now >= readyUtc) continue;
            if (schedule.LastTriggeredUtc is DateTimeOffset last && last >= startUtc) return;
            schedule.LastTriggeredUtc = now;
            _settings.TopUpUntil = readyUtc + ScheduledTopUpGrace;
            RaiseSettingsChanged();
            return;
        }
    }

    private void AdvanceTopUp(DateTimeOffset now, int? percent, List<ChargeCareReminder> reminders)
    {
        if (_settings.TopUpUntil is not DateTimeOffset until) return;
        if (now >= until || percent >= 100)
        {
            _settings.TopUpUntil = null;
            RaiseSettingsChanged();
            reminders.Add(new ChargeCareReminder(ChargeCareReminderKind.TopUpFinished, percent));
        }
    }

    private void AdvanceCalibration(DateTimeOffset now, int? percent, List<ChargeCareReminder> reminders)
    {
        CalibrationProgress calibration = _settings.Calibration;
        if (percent is not int p) return;
        CalibrationStep before = calibration.Step;
        switch (calibration.Step)
        {
            case CalibrationStep.ChargeToFull when p >= 100:
                calibration.Step = CalibrationStep.DischargeToLow;
                break;
            case CalibrationStep.DischargeToLow when p <= CalibrationProgress.LowTarget:
                calibration.Step = CalibrationStep.RechargeToFull;
                break;
            case CalibrationStep.RechargeToFull when p >= 100:
                calibration.Step = CalibrationStep.NotRunning;
                calibration.StartedUtc = null;
                calibration.LastCompletedUtc = now;
                _settings.LastCalibrationReminderUtc = now;
                break;
        }
        if (calibration.Step == before) return;
        RaiseSettingsChanged();
        reminders.Add(calibration.Step == CalibrationStep.NotRunning
            ? new ChargeCareReminder(ChargeCareReminderKind.CalibrationCompleted, p)
            : new ChargeCareReminder(ChargeCareReminderKind.CalibrationStepChanged, p, calibration.Step));
    }

    private void AdvanceCalibrationReminder(DateTimeOffset now, List<ChargeCareReminder> reminders)
    {
        if (_settings.CalibrationReminderDays <= 0 || _settings.Calibration.Step != CalibrationStep.NotRunning) return;
        DateTimeOffset? baseline = Max(_settings.Calibration.LastCompletedUtc, _settings.LastCalibrationReminderUtc);
        if (baseline is null)
        {
            // Start the clock on first use instead of nagging immediately.
            _settings.LastCalibrationReminderUtc = now;
            RaiseSettingsChanged();
            return;
        }
        if (now - baseline.Value < TimeSpan.FromDays(_settings.CalibrationReminderDays)) return;
        _settings.LastCalibrationReminderUtc = now;
        RaiseSettingsChanged();
        reminders.Add(new ChargeCareReminder(ChargeCareReminderKind.CalibrationDue, LastSnapshot.Percent));
    }

    private bool AdvanceHeat(BatterySnapshot snapshot, List<ChargeCareReminder> reminders)
    {
        HeatProtectionSettings heat = _settings.HeatProtection;
        if (!heat.Enabled || snapshot.TemperatureC is not double t)
        {
            _hot = false;
            _hotReminderSent = false;
            return false;
        }
        if (t >= heat.MaxTempC) _hot = true;
        else if (t <= heat.MaxTempC - HeatHysteresisC) { _hot = false; _hotReminderSent = false; }

        bool canPause = Mode == ChargeCareMode.Hardware && Backend!.Capabilities.CanPauseCharging;
        if (_hot && !canPause && snapshot.IsOnAc && !_hotReminderSent)
        {
            _hotReminderSent = true;
            reminders.Add(new ChargeCareReminder(ChargeCareReminderKind.TooHot, snapshot.Percent, TemperatureC: t));
        }
        return _hot && canPause;
    }

    private void AdvanceReminders(ChargeCareMode mode, BatterySnapshot snapshot, int effectiveLimit, List<ChargeCareReminder> reminders)
    {
        if (snapshot.Percent is not int p) return;
        bool specialPhase = effectiveLimit >= 100;

        if (!snapshot.IsOnAc || p < _settings.SailingLower) { _unplugArmed = true; _limitReachedArmed = true; }

        if (_settings.RemindersEnabled && mode == ChargeCareMode.Software && !specialPhase &&
            snapshot.IsOnAc && p >= effectiveLimit && _unplugArmed)
        {
            _unplugArmed = false;
            reminders.Add(new ChargeCareReminder(ChargeCareReminderKind.UnplugNow, p));
        }

        if (_settings.NotifyWhenLimitReached && mode == ChargeCareMode.Hardware && !specialPhase &&
            snapshot.IsOnAc && p >= effectiveLimit && _limitReachedArmed)
        {
            _limitReachedArmed = false;
            reminders.Add(new ChargeCareReminder(ChargeCareReminderKind.LimitReached, p));
        }

        if (snapshot.IsOnAc || p > _settings.LowReminderPercent + LowReminderRearmGap) _plugInArmed = true;
        if (_settings.RemindersEnabled && !snapshot.IsOnAc && p <= _settings.LowReminderPercent && _plugInArmed)
        {
            _plugInArmed = false;
            reminders.Add(new ChargeCareReminder(ChargeCareReminderKind.PlugIn, p));
        }
    }

    private ChargeCareStatus ComputeStatus(BatterySnapshot snapshot, ChargeCareMode mode, int effectiveLimit, bool heatPause)
    {
        if (HardwareFeaturesAllowed && _settings.Enabled && _settings.Calibration.Step != CalibrationStep.NotRunning) return ChargeCareStatus.Calibrating;
        if (heatPause) return ChargeCareStatus.HeatPaused;
        if (_manualPause && mode == ChargeCareMode.Hardware) return ChargeCareStatus.ManuallyPaused;
        if (HardwareFeaturesAllowed && _settings.Enabled && IsTopUpActive) return ChargeCareStatus.TopUp;
        if (snapshot.IsCharging) return ChargeCareStatus.Charging;
        if (!snapshot.IsOnAc) return ChargeCareStatus.Discharging;
        if (snapshot.Percent is int p && mode is ChargeCareMode.Hardware or ChargeCareMode.Software)
        {
            if (p >= effectiveLimit - 1) return ChargeCareStatus.HoldingAtLimit;
            if (p >= _settings.SailingLower) return ChargeCareStatus.Sailing;
        }
        return ChargeCareStatus.Idle;
    }

    private async Task<BackendWriteResult> ApplyCoreAsync(ChargeLimitRequest request, bool allowPrompt, CancellationToken cancellationToken)
    {
        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_settings.OriginalHardwareState is null || _settings.OriginalHardwareState.BackendId != Backend!.Id)
            {
                BackendOriginalState original = Backend!.CaptureOriginal();
                if (original.Values.Count > 0)
                {
                    _settings.OriginalHardwareState = original;
                    RaiseSettingsChanged();
                }
            }

            BackendWriteResult result = await Backend!.ApplyAsync(request with { AllowElevationPrompt = allowPrompt }, cancellationToken)
                .ConfigureAwait(false);
            if (result.Success)
            {
                _lastApplied = request;
                _lastRequired = null;
            }
            if (!result.NeedsElevation) HardwareApplied?.Invoke(this, result);
            return result;
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private void RequireHardwareChange(ChargeLimitRequest pending)
    {
        if (SameTarget(pending, _lastRequired)) return;
        _lastRequired = pending;
        HardwareChangeRequired?.Invoke(this, pending);
    }

    internal static bool SameTarget(ChargeLimitRequest? a, ChargeLimitRequest? b) =>
        a is not null && b is not null && a with { AllowElevationPrompt = true } == b with { AllowElevationPrompt = true };

    internal static bool IsSatisfiedBy(ChargeLimitRequest desired, ChargeLimitState state)
    {
        if (desired.Limit is int limit && state.Limit != limit) return false;
        if (desired.StartThreshold is int start && state.StartThreshold != start) return false;
        if (desired.ChargingPaused is bool paused && (state.ChargingPaused ?? false) != paused) return false;
        // A reset start threshold has no single firmware value to compare with; only skip it if nothing else differs
        // and the firmware reports no threshold.
        if (desired.ResetStartThreshold && state.StartThreshold is not null) return false;
        return true;
    }

    private void Raise(ChargeCareReminder reminder)
    {
        try { ReminderRaised?.Invoke(this, reminder); }
        catch (Exception) { }
    }

    private void RaiseSettingsChanged()
    {
        try { SettingsChanged?.Invoke(this, _settings.Clone()); }
        catch (Exception) { }
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}

public static class BatteryEstimates
{
    /// <summary>
    /// Linear estimate of the time to reach <paramref name="targetPercent"/> at the current charge rate.
    /// Null when not charging, already there, or the rate or capacity is unknown. Real charging slows near
    /// full, so the UI should present this as an estimate.
    /// </summary>
    public static TimeSpan? TimeToPercent(BatterySnapshot snapshot, int targetPercent)
    {
        if (!snapshot.Present || !snapshot.IsCharging) return null;
        if (snapshot.ChargeRateMw is not int rate || rate <= 0) return null;
        if (snapshot.FullChargeCapacityMwh is not int full || full <= 0) return null;
        double remaining = snapshot.RemainingCapacityMwh ?? (snapshot.Percent is int p ? full * p / 100.0 : double.NaN);
        if (double.IsNaN(remaining)) return null;
        double target = full * Math.Clamp(targetPercent, 0, 100) / 100.0;
        if (target <= remaining) return null;
        return TimeSpan.FromHours((target - remaining) / rate);
    }
}
