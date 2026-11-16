using Portal.Core.Battery;
using Xunit;

namespace Portal.Core.Tests;

public class ChargeCareEngineTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static readonly ChargeLimitCapabilities ThresholdCaps = new()
    {
        Kind = ChargeLimitKind.ContinuousRange, MinLimit = 50, MaxLimit = 100,
        SupportsStartThreshold = true, MinStartThreshold = 40, MaxStartThreshold = 95, MinStartStopGap = 5,
    };

    private static readonly ChargeLimitCapabilities PauseCaps = new()
    {
        Kind = ChargeLimitKind.ContinuousRange, MinLimit = 50, MaxLimit = 100, CanPauseCharging = true,
    };

    private static BatterySettings Enabled(Action<BatterySettings>? configure = null)
    {
        var s = new BatterySettings { Enabled = true, Limit = 80, SailingLower = 75 };
        configure?.Invoke(s);
        return s;
    }

    /// <param name="applyRequests">Plays the app's part: applies each requested change to the fake backend, as the controller will.</param>
    private static (ChargeCareEngine Engine, ManualTimeProvider Time, List<ChargeCareReminder> Reminders) Create(
        IChargeLimitBackend? backend, BatterySettings settings, bool pro = true, bool applyRequests = true)
    {
        var time = new ManualTimeProvider(Start);
        var engine = new ChargeCareEngine(backend, settings, time, pro);
        var reminders = new List<ChargeCareReminder>();
        engine.ReminderRaised += (_, r) => reminders.Add(r);
        if (applyRequests && backend is not null) engine.HardwareChangeRequired += (_, r) => backend.ApplyAsync(r);
        return (engine, time, reminders);
    }

    [Fact]
    public async Task Hardware_mode_applies_limit_and_sailing_start_threshold_once()
    {
        var backend = new TestBackend(ThresholdCaps);
        var (engine, _, _) = Create(backend, Enabled());

        ChargeCareEvaluation first = await engine.ProcessAsync(Snap.At(60, onAc: true, charging: true));
        await engine.ProcessAsync(Snap.At(61, onAc: true, charging: true));

        Assert.Equal(ChargeCareMode.Hardware, first.Mode);
        Assert.Single(backend.Applied);
        Assert.Equal(80, backend.Limit);
        Assert.Equal(75, backend.Start);
        Assert.Null(engine.Settings.OriginalHardwareState);
    }

    [Fact]
    public async Task Engine_never_writes_firmware_or_touches_saved_originals()
    {
        var backend = new TestBackend(PauseCaps);
        var original = new BackendOriginalState { BackendId = backend.Id, Values = new() { ["Limit"] = "100" } };
        var (engine, time, _) = Create(backend, Enabled(s => s.OriginalHardwareState = original), applyRequests: false);
        var asked = new List<ChargeLimitRequest>();
        engine.HardwareChangeRequired += (_, r) => asked.Add(r);

        await engine.ProcessAsync(Snap.At(60, onAc: true, charging: true));
        engine.StartTopUp(TimeSpan.FromHours(1));
        await engine.ProcessAsync(Snap.At(61, onAc: true, charging: true));
        engine.SetManualPause(true);
        await engine.ProcessAsync(Snap.At(62, onAc: true));
        time.Advance(TimeSpan.FromHours(2));
        await engine.ReapplyAfterResumeAsync();

        Assert.Empty(backend.Applied);
        Assert.NotEmpty(asked);
        BackendOriginalState kept = engine.Settings.OriginalHardwareState!;
        Assert.Equal(original.BackendId, kept.BackendId);
        Assert.Equal(original.Values, kept.Values);
        Assert.DoesNotContain(typeof(ChargeCareEngine).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public),
            f => typeof(IChargeLimitBackend).IsAssignableFrom(f.FieldType));
        Assert.DoesNotContain(typeof(ChargeCareEngine).GetMethods(), m => m.ReturnType == typeof(Task<BackendWriteResult>));
    }

    [Fact]
    public async Task Sailing_by_pause_uses_hysteresis()
    {
        var backend = new TestBackend(PauseCaps);
        var (engine, _, _) = Create(backend, Enabled());

        await engine.ProcessAsync(Snap.At(80, onAc: true, charging: true));
        Assert.True(backend.Paused);
        await engine.ProcessAsync(Snap.At(77, onAc: true));
        Assert.True(backend.Paused);
        await engine.ProcessAsync(Snap.At(74, onAc: true));
        Assert.False(backend.Paused);
        await engine.ProcessAsync(Snap.At(78, onAc: true, charging: true));
        Assert.False(backend.Paused);
    }

    [Fact]
    public async Task Top_up_charges_to_full_then_expires_and_restores_limit()
    {
        var backend = new TestBackend(ThresholdCaps);
        var (engine, time, reminders) = Create(backend, Enabled());
        BatterySettings? persisted = null;
        engine.SettingsChanged += (_, s) => persisted = s;

        engine.StartTopUp(TimeSpan.FromHours(2));
        ChargeCareEvaluation during = await engine.ProcessAsync(Snap.At(85, onAc: true, charging: true));
        Assert.Equal(ChargeCareStatus.TopUp, during.Status);
        Assert.Equal(100, backend.Limit);
        Assert.Null(backend.Start);

        time.Advance(TimeSpan.FromHours(3));
        ChargeCareEvaluation after = await engine.ProcessAsync(Snap.At(95, onAc: true));
        Assert.Contains(reminders, r => r.Kind == ChargeCareReminderKind.TopUpFinished);
        Assert.Equal(80, after.EffectiveLimit);
        Assert.Equal(80, backend.Limit);
        Assert.Null(persisted!.TopUpUntil);
    }

    [Fact]
    public async Task Top_up_ends_when_battery_is_full()
    {
        var (engine, _, reminders) = Create(new TestBackend(ThresholdCaps), Enabled());
        engine.StartTopUp();
        await engine.ProcessAsync(Snap.At(100, onAc: true));
        Assert.False(engine.IsTopUpActive);
        Assert.Contains(reminders, r => r.Kind == ChargeCareReminderKind.TopUpFinished);
    }

    [Fact]
    public async Task Heat_protection_pauses_and_resumes_with_hysteresis()
    {
        var backend = new TestBackend(PauseCaps);
        var (engine, _, _) = Create(backend, Enabled(s => s.HeatProtection = new HeatProtectionSettings { Enabled = true, MaxTempC = 40 }));

        ChargeCareEvaluation hot = await engine.ProcessAsync(Snap.At(60, onAc: true, charging: true, tempC: 41));
        Assert.True(hot.HeatPauseActive);
        Assert.Equal(ChargeCareStatus.HeatPaused, hot.Status);
        Assert.True(backend.Paused);

        await engine.ProcessAsync(Snap.At(60, onAc: true, tempC: 38.5));
        Assert.True(backend.Paused);

        ChargeCareEvaluation cool = await engine.ProcessAsync(Snap.At(60, onAc: true, tempC: 37));
        Assert.False(cool.HeatPauseActive);
        Assert.False(backend.Paused);
    }

    [Fact]
    public void Heat_protection_without_pause_notifies_once_per_episode()
    {
        var (engine, _, reminders) = Create(null, Enabled(s => s.HeatProtection = new HeatProtectionSettings { Enabled = true, MaxTempC = 40 }));
        engine.Evaluate(Snap.At(60, onAc: true, charging: true, tempC: 42));
        engine.Evaluate(Snap.At(61, onAc: true, charging: true, tempC: 43));
        engine.Evaluate(Snap.At(62, onAc: true, charging: true, tempC: 36));
        engine.Evaluate(Snap.At(63, onAc: true, charging: true, tempC: 41));
        Assert.Equal(2, reminders.Count(r => r.Kind == ChargeCareReminderKind.TooHot));
    }

    [Fact]
    public async Task Calibration_walks_through_all_steps_and_restores_limit()
    {
        var backend = new TestBackend(ThresholdCaps);
        var (engine, _, reminders) = Create(backend, Enabled());

        engine.StartCalibration();
        Assert.Equal(CalibrationStep.ChargeToFull, engine.Settings.Calibration.Step);
        ChargeCareEvaluation charging = await engine.ProcessAsync(Snap.At(90, onAc: true, charging: true));
        Assert.Equal(ChargeCareStatus.Calibrating, charging.Status);
        Assert.Equal(100, backend.Limit);

        await engine.ProcessAsync(Snap.At(100, onAc: true));
        Assert.Equal(CalibrationStep.DischargeToLow, engine.Settings.Calibration.Step);
        await engine.ProcessAsync(Snap.At(40, onAc: false));
        Assert.Equal(CalibrationStep.DischargeToLow, engine.Settings.Calibration.Step);
        await engine.ProcessAsync(Snap.At(15, onAc: false));
        Assert.Equal(CalibrationStep.RechargeToFull, engine.Settings.Calibration.Step);
        ChargeCareEvaluation done = await engine.ProcessAsync(Snap.At(100, onAc: true));

        Assert.Equal(CalibrationStep.NotRunning, engine.Settings.Calibration.Step);
        Assert.NotNull(engine.Settings.Calibration.LastCompletedUtc);
        Assert.Equal(80, done.EffectiveLimit);
        Assert.Equal(80, backend.Limit);
        Assert.Equal(3, reminders.Count(r => r.Kind == ChargeCareReminderKind.CalibrationStepChanged));
        Assert.Single(reminders, r => r.Kind == ChargeCareReminderKind.CalibrationCompleted);
    }

    [Fact]
    public void Software_mode_reminds_to_unplug_and_plug_in_once_per_crossing()
    {
        var (engine, _, reminders) = Create(null, Enabled());

        Assert.Equal(ChargeCareMode.Software, engine.Evaluate(Snap.At(80, onAc: true, charging: true)).Mode);
        engine.Evaluate(Snap.At(81, onAc: true, charging: true));
        Assert.Single(reminders, r => r.Kind == ChargeCareReminderKind.UnplugNow);

        engine.Evaluate(Snap.At(70, onAc: false));
        engine.Evaluate(Snap.At(80, onAc: true));
        Assert.Equal(2, reminders.Count(r => r.Kind == ChargeCareReminderKind.UnplugNow));

        engine.Evaluate(Snap.At(20, onAc: false));
        engine.Evaluate(Snap.At(19, onAc: false));
        Assert.Single(reminders, r => r.Kind == ChargeCareReminderKind.PlugIn);
        engine.Evaluate(Snap.At(26, onAc: false));
        engine.Evaluate(Snap.At(20, onAc: false));
        Assert.Equal(2, reminders.Count(r => r.Kind == ChargeCareReminderKind.PlugIn));
    }

    [Fact]
    public async Task Engine_asks_once_per_target()
    {
        var backend = new TestBackend(ThresholdCaps, requiresElevation: true);
        var (engine, _, _) = Create(backend, Enabled(), applyRequests: false);
        int asked = 0;
        engine.HardwareChangeRequired += (_, _) => asked++;

        await engine.ProcessAsync(Snap.At(60, onAc: true, charging: true));
        await engine.ProcessAsync(Snap.At(61, onAc: true, charging: true));
        Assert.Equal(1, asked);
        Assert.Empty(backend.Applied);
    }

    [Fact]
    public void Lite_keeps_hardware_features_read_only()
    {
        var (engine, _, _) = Create(new TestBackend(ThresholdCaps), Enabled(), pro: false);
        Assert.Equal(ChargeCareMode.Software, engine.Evaluate(Snap.At(50, onAc: true)).Mode);
        engine.StartTopUp();
        Assert.False(engine.IsTopUpActive);
        engine.StartCalibration();
        Assert.Equal(CalibrationStep.NotRunning, engine.Settings.Calibration.Step);
    }

    [Fact]
    public void Scheduled_top_up_starts_in_window_and_is_not_restarted_after_cancel()
    {
        var friday = new DateTimeOffset(2026, 10, 23, 5, 0, 0, TimeSpan.Zero);
        Assert.Equal(DayOfWeek.Friday, friday.DayOfWeek);
        var (engine, time, _) = Create(new TestBackend(ThresholdCaps), Enabled(s =>
            s.ScheduledTopUp = new ScheduledTopUpSettings { Enabled = true, Days = { DayOfWeek.Friday }, ReadyBy = new TimeSpan(7, 0, 0), LeadHours = 3 }));

        time.Set(friday.AddHours(-2)); // 03:00, before the window
        engine.Evaluate(Snap.At(70, onAc: true));
        Assert.False(engine.IsTopUpActive);

        time.Set(friday);
        engine.Evaluate(Snap.At(70, onAc: true));
        Assert.True(engine.IsTopUpActive);
        Assert.Equal(friday.AddHours(4), engine.Settings.TopUpUntil); // 07:00 + 2 h grace

        engine.CancelTopUp();
        time.Set(friday.AddMinutes(30));
        engine.Evaluate(Snap.At(72, onAc: true));
        Assert.False(engine.IsTopUpActive);
    }

    [Fact]
    public void Calibration_reminder_waits_for_the_interval()
    {
        var (engine, time, reminders) = Create(null, Enabled(s => s.CalibrationReminderDays = 30));
        engine.Evaluate(Snap.At(60, onAc: true));
        Assert.DoesNotContain(reminders, r => r.Kind == ChargeCareReminderKind.CalibrationDue);

        time.Advance(TimeSpan.FromDays(31));
        engine.Evaluate(Snap.At(60, onAc: true));
        engine.Evaluate(Snap.At(60, onAc: true));
        Assert.Single(reminders, r => r.Kind == ChargeCareReminderKind.CalibrationDue);
    }

    [Fact]
    public async Task Limit_reached_is_raised_once_until_rearmed()
    {
        var (engine, _, reminders) = Create(new TestBackend(ThresholdCaps), Enabled());
        await engine.ProcessAsync(Snap.At(80, onAc: true));
        await engine.ProcessAsync(Snap.At(80, onAc: true));
        Assert.Single(reminders, r => r.Kind == ChargeCareReminderKind.LimitReached);
        Assert.DoesNotContain(reminders, r => r.Kind == ChargeCareReminderKind.UnplugNow);
    }

    [Fact]
    public async Task Manual_pause_and_resume_when_backend_supports_pause()
    {
        var backend = new TestBackend(PauseCaps);
        var (engine, _, _) = Create(backend, Enabled());
        await engine.ProcessAsync(Snap.At(50, onAc: true, charging: true));
        Assert.True(engine.SetManualPause(true));
        ChargeCareEvaluation paused = await engine.ProcessAsync(Snap.At(50, onAc: true));
        Assert.Equal(ChargeCareStatus.ManuallyPaused, paused.Status);
        Assert.True(backend.Paused);
        engine.SetManualPause(false);
        await engine.ProcessAsync(Snap.At(50, onAc: true));
        Assert.False(backend.Paused);

        var (noPause, _, _) = Create(new TestBackend(ThresholdCaps), Enabled());
        noPause.Evaluate(Snap.At(50, onAc: true));
        Assert.False(noPause.SetManualPause(true));
    }

    [Fact]
    public void No_battery_reports_no_battery_mode()
    {
        var (engine, _, reminders) = Create(null, Enabled());
        Assert.Equal(ChargeCareMode.NoBattery, engine.Evaluate(BatterySnapshot.NotPresent).Mode);
        Assert.Empty(reminders);
    }

    [Fact]
    public void Estimates_time_to_limit_and_full()
    {
        var (engine, _, _) = Create(null, Enabled());
        ChargeCareEvaluation e = engine.Evaluate(Snap.At(50, onAc: true, charging: true));
        // 50 000 mWh pack, 25 000 mWh at 50 %, charging at 25 W.
        Assert.Equal(TimeSpan.FromHours(0.6), e.TimeToLimit);
        Assert.Equal(TimeSpan.FromHours(1), e.TimeToFull);
    }
}
