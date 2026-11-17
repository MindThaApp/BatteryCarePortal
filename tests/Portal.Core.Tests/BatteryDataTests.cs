using System.Text;
using Portal.Core.Battery;
using Portal.Core.Battery.Backends;
using Xunit;

namespace Portal.Core.Tests;

public class SnapshotMappingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_system_battery_flag_means_not_present()
    {
        BatterySnapshot s = BatterySnapshotMapper.Map(new BatteryRawReadings { BatteryFlag = 128, AcLineStatus = 1 }, Now);
        Assert.False(s.Present);
        Assert.Null(s.Percent);
    }

    [Fact]
    public void Charging_readings_are_merged_and_unknowns_stay_null()
    {
        BatterySnapshot s = BatterySnapshotMapper.Map(new BatteryRawReadings
        {
            AcLineStatus = 1, BatteryFlag = 8, LifePercent = 64,
            ReportStatus = "Charging", ReportChargeRateMw = 15000, ReportDesignMwh = 57000, ReportFullMwh = 51300, ReportRemainingMwh = 32832,
            WmiVoltageMv = 0xFFFFFFFF, WmiCycleCount = 0, WmiTemperatureDeciKelvin = 3082,
            ChemistryCode = 0x4E4F494C, Manufacturer = " SMP ",
        }, Now);

        Assert.True(s.Present);
        Assert.Equal(64, s.Percent);
        Assert.True(s.IsOnAc);
        Assert.True(s.IsCharging);
        Assert.Equal(15000, s.ChargeRateMw);
        Assert.Null(s.DischargeRateMw);
        Assert.Equal(90.0, s.HealthPercent);
        Assert.Null(s.VoltageMv);
        Assert.Null(s.CycleCount);
        Assert.InRange(s.TemperatureC!.Value, 35.0, 35.1);
        Assert.Equal("LION", s.Chemistry);
        Assert.Equal("SMP", s.Manufacturer);
        Assert.Null(s.TimeRemaining);
        Assert.Equal(TimeSpan.FromHours((51300 - 32832) / 15000.0), s.TimeToFull);
    }

    [Fact]
    public void Discharging_uses_negative_rate_and_life_time()
    {
        BatterySnapshot s = BatterySnapshotMapper.Map(new BatteryRawReadings
        {
            AcLineStatus = 0, BatteryFlag = 0, LifePercent = 50, LifeTimeSeconds = 7200,
            ReportStatus = "Discharging", ReportChargeRateMw = -9000, ReportFullMwh = 50000, ReportRemainingMwh = 25000,
        }, Now);
        Assert.False(s.IsOnAc);
        Assert.False(s.IsCharging);
        Assert.Equal(9000, s.DischargeRateMw);
        Assert.Null(s.ChargeRateMw);
        Assert.Equal(TimeSpan.FromHours(2), s.TimeRemaining);
    }

    [Fact]
    public void Percent_falls_back_to_capacity_and_wmi_values()
    {
        BatterySnapshot s = BatterySnapshotMapper.Map(new BatteryRawReadings
        {
            AcLineStatus = 255, BatteryFlag = 255, LifePercent = 255,
            WmiFullMwh = 40000, WmiRemainingMwh = 10000, WmiDesignMwh = 50000, WmiPowerOnline = true, WmiCycleCount = 321,
        }, Now);
        Assert.True(s.Present);
        Assert.Equal(25, s.Percent);
        Assert.True(s.IsOnAc);
        Assert.Equal(80.0, s.HealthPercent);
        Assert.Equal(321, s.CycleCount);
    }

    [Fact]
    public void Idle_rate_zero_is_reported_as_zero_watts_not_unknown()
    {
        BatterySnapshot s = BatterySnapshotMapper.Map(new BatteryRawReadings
        {
            AcLineStatus = 1, BatteryFlag = 1, LifePercent = 80, ReportStatus = "Idle", ReportChargeRateMw = 0,
        }, Now);
        PowerFlowSnapshot flow = PowerFlowCalculator.Compute(s, adapterRatedW: null, meter: null, cpuPercent: 12);
        Assert.Equal(new PowerReading(0, PowerDataQuality.Measured), flow.BatteryW);
        Assert.Equal(PowerDataQuality.Unavailable, flow.SystemW.Quality);
        Assert.Equal(PowerDataQuality.Unavailable, flow.AdapterRatedW.Quality);
        Assert.True(flow.AdapterConnected);
    }

    [Fact]
    public void Power_flow_on_battery_measures_system_from_discharge()
    {
        var s = new BatterySnapshot { Present = true, Percent = 50, IsOnAc = false, DischargeRateMw = 9800 };
        PowerFlowSnapshot flow = PowerFlowCalculator.Compute(s, null, null, null);
        Assert.Equal(-9.8, flow.BatteryW.Watts);
        Assert.Equal(9.8, flow.SystemW.Watts);
        Assert.Equal(PowerDataQuality.Measured, flow.SystemW.Quality);
    }

    [Fact]
    public void Power_flow_uses_only_a_system_energy_meter_channel_on_ac()
    {
        var s = new BatterySnapshot { Present = true, Percent = 80, IsOnAc = true };
        var cpuOnly = new EnergyMeterSample(new[] { new EmiChannelPower("p", "RAPL_Package0_PKG", 6.5) }, SystemW: null);
        Assert.Equal(PowerDataQuality.Unavailable, PowerFlowCalculator.Compute(s, null, cpuOnly, null).SystemW.Quality);
        var system = new EnergyMeterSample(new[] { new EmiChannelPower("p", "RAPL_Package0_PSYS", 14.2) }, SystemW: 14.2);
        Assert.Equal(14.2, PowerFlowCalculator.Compute(s, null, system, null).SystemW.Watts);
        Assert.Null(PowerFlowCalculator.Compute(BatterySnapshot.NotPresent, null, null, null).AdapterConnected);
    }

    [Fact]
    public void Energy_meter_math_and_channel_names()
    {
        Assert.Equal(1.0, EnergyMeterReader.ToWatts(1_000_000_000_000UL, 36_000_000_000UL), 6);
        Assert.True(EnergyMeterReader.IsSystemChannel("RAPL_Package0_PSYS"));
        Assert.True(EnergyMeterReader.IsSystemChannel("sys"));
        Assert.False(EnergyMeterReader.IsSystemChannel("RAPL_Package0_PKG"));
    }

    [Fact]
    public void Estimates_need_charging_rate_and_capacity()
    {
        var charging = Snap.At(50, onAc: true, charging: true);
        Assert.Equal(TimeSpan.FromHours(1), BatteryEstimates.TimeToPercent(charging, 100));
        Assert.Equal(TimeSpan.FromHours(0.6), BatteryEstimates.TimeToPercent(charging, 80));
        Assert.Null(BatteryEstimates.TimeToPercent(charging, 40));
        Assert.Null(BatteryEstimates.TimeToPercent(Snap.At(50, onAc: true), 100));
        Assert.Null(BatteryEstimates.TimeToPercent(charging with { ChargeRateMw = null }, 100));
        Assert.Null(BatteryEstimates.TimeToPercent(charging with { FullChargeCapacityMwh = null }, 100));
    }

    [Fact]
    public void Capabilities_snap_and_validate()
    {
        var fixedCaps = new ChargeLimitCapabilities { Kind = ChargeLimitKind.FixedValues, FixedValues = new[] { 60, 80, 100 } };
        Assert.Equal(80, fixedCaps.Snap(75));
        Assert.False(fixedCaps.IsValidLimit(75));
        var conservation = new ChargeLimitCapabilities { Kind = ChargeLimitKind.OnOffConservation, ConservationPercent = 60 };
        Assert.Equal(60, conservation.Snap(80));
        Assert.Equal(100, conservation.Snap(100));
        Assert.Equal(new[] { 60, 100 }, conservation.DiscreteValues);
        var range = new ChargeLimitCapabilities { Kind = ChargeLimitKind.ContinuousRange, MinLimit = 50, MaxLimit = 100, SupportsStartThreshold = true };
        Assert.Equal(50, range.Snap(20));
        Assert.NotNull(ChargeLimitRequestValidator.Validate(new ChargeLimitRequest { Limit = 80, StartThreshold = 78 }, range, 100));
        Assert.Null(ChargeLimitRequestValidator.Validate(new ChargeLimitRequest { Limit = 80, StartThreshold = 75 }, range, 100));
        Assert.NotNull(ChargeLimitRequestValidator.Validate(new ChargeLimitRequest { ChargingPaused = true }, range, 100));
    }

    [Fact]
    public void Settings_normalize_clamps_ranges()
    {
        var s = new BatterySettings { Limit = 30, SailingLower = 90, LowReminderPercent = 1 }.Normalize();
        Assert.Equal(50, s.Limit);
        Assert.Equal(49, s.SailingLower);
        Assert.Equal(5, s.LowReminderPercent);
        var t = new BatterySettings { Limit = 80, SailingLower = 60 }.Normalize();
        Assert.Equal(70, t.SailingLower);
    }

    [Fact]
    public void Write_plan_travels_as_base64_data_not_code()
    {
        var plan = new BatteryWritePlan { BackendId = "x", Steps = { new BatteryWriteStep { Label = "Set'; Format-Volume", Kind = BatteryWriteStepKind.WmiMethod } } };
        string script = BatteryWritePlanScript.Build(plan, "pipe'; Format-Volume");
        Assert.DoesNotContain("Format-Volume", script);
        Assert.DoesNotContain("{PLAN}", script);
        Assert.DoesNotContain("{PIPE}", script);
        string encoded = BatteryWritePlanScript.EncodeForCommandLine(script);
        Assert.Equal(script, Encoding.Unicode.GetString(Convert.FromBase64String(encoded)));
    }
}

public class BackendTests
{
    private static FakeWmiReader LenovoWmi()
    {
        var wmi = new FakeWmiReader();
        wmi.Add("Win32_ComputerSystem", new() { ["Manufacturer"] = "LENOVO", ["SystemFamily"] = "IdeaPad 5 15ITL05", ["Model"] = "82FG" });
        return wmi;
    }

    [Fact]
    public async Task Lenovo_conservation_detects_writes_and_reads_back()
    {
        var driver = new FakeEnergyDriver();
        var executor = new RecordingExecutor();
        var backend = new LenovoConservationBackend(LenovoWmi(), executor, driver);

        Assert.True(backend.Detect());
        Assert.Equal(ChargeLimitKind.OnOffConservation, backend.Capabilities.Kind);
        Assert.Equal(100, backend.GetState().Limit);
        Assert.Equal("Off", backend.CaptureOriginal().Values["Conservation"]);

        BackendWriteResult on = await backend.SetLimitAsync(60);
        Assert.True(on.Success, on.Message);
        Assert.Equal(60, on.ReadBack!.Limit);
        Assert.Contains(LenovoConservationBackend.ConservationOnCode, driver.Sent);
        Assert.Equal(LenovoConservationBackend.QueryCode, driver.Sent[^1]);
        Assert.Empty(executor.Plans);

        int sentBefore = driver.Sent.Count;
        BackendWriteResult again = await backend.SetLimitAsync(60);
        Assert.True(again.Success);
        Assert.DoesNotContain(LenovoConservationBackend.ConservationOnCode, driver.Sent.Skip(sentBefore));

        Assert.False((await backend.SetLimitAsync(80)).Success);
        Assert.False((await backend.SetChargingPausedAsync(true)).Success);
    }

    [Fact]
    public async Task Lenovo_conservation_reports_failure_when_read_back_does_not_change()
    {
        var driver = new FakeEnergyDriver { IgnoreWrites = true };
        var backend = new LenovoConservationBackend(LenovoWmi(), new RecordingExecutor(), driver);
        Assert.True(backend.Detect());
        BackendWriteResult result = await backend.SetLimitAsync(60);
        Assert.False(result.Success);
        Assert.Equal(1, driver.Sent.Count(c => c == LenovoConservationBackend.ConservationOnCode));
    }

    [Fact]
    public async Task Lenovo_conservation_uses_elevated_helper_only_on_access_denied_and_when_allowed()
    {
        var driver = new FakeEnergyDriver { ModeWord = 1u << LenovoConservationBackend.ConservationBit, FailWritesWith = DeviceIoResult.ErrorAccessDenied };
        var executor = new RecordingExecutor();
        var backend = new LenovoConservationBackend(LenovoWmi(), executor, driver);
        Assert.True(backend.Detect());
        Assert.Equal(60, backend.GetState().Limit);

        BackendWriteResult unattended = await backend.ApplyAsync(new ChargeLimitRequest { Limit = 100, AllowElevationPrompt = false });
        Assert.True(unattended.NeedsElevation);
        Assert.Empty(executor.Plans);

        await backend.SetLimitAsync(100);
        BatteryWritePlan plan = Assert.Single(executor.Plans);
        Assert.Equal(BatteryWriteStepKind.DeviceIoControl, plan.Steps[0].Kind);
        Assert.Equal(LenovoConservationBackend.ConservationOffCode, plan.Steps[0].InputValue);
        Assert.Equal(BatteryWriteStepKind.VerifyDeviceIoControlBit, plan.Steps[1].Kind);
        Assert.False(plan.Steps[1].ExpectedBitValue);
    }

    [Fact]
    public void Lenovo_conservation_not_detected_without_driver_or_on_other_brands()
    {
        Assert.False(new LenovoConservationBackend(LenovoWmi(), new RecordingExecutor(), new FakeEnergyDriver { FailAllWith = 2 }).Detect());
        var other = new FakeWmiReader();
        other.Add("Win32_ComputerSystem", new() { ["Manufacturer"] = "Contoso" });
        Assert.False(new LenovoConservationBackend(other, new RecordingExecutor(), new FakeEnergyDriver()).Detect());
    }

    [Fact]
    public void Unverified_backends_stay_off_by_default()
    {
        var wmi = new FakeWmiReader();
        Assert.False(new DellChargeLimitBackend(wmi, new RecordingExecutor()).Detect());
        Assert.False(new HpChargeLimitBackend(wmi, new RecordingExecutor()).Detect());
        Assert.False(new AsusChargeLimitBackend(wmi, new RecordingExecutor()).Detect());
        Assert.False(new LenovoThinkPadThresholdBackend(wmi, new RecordingExecutor()).Detect());
    }

    [Fact]
    public async Task Dell_reads_state_but_refuses_every_write_while_unverified()
    {
        var wmi = new FakeWmiReader();
        wmi.Add("AttributeName='PrimaryBattChargeCfg'", new() { ["CurrentValue"] = new[] { "Standard" }, ["PossibleValue"] = new[] { "Standard", "Express", "Custom" } });
        wmi.Add("AttributeName='CustomChargeStart'", new() { ["CurrentValue"] = 50u, ["LowerBound"] = 50u, ["UpperBound"] = 95u });
        wmi.Add("AttributeName='CustomChargeStop'", new() { ["CurrentValue"] = 100u, ["LowerBound"] = 55u, ["UpperBound"] = 100u });
        var executor = new RecordingExecutor();
        var backend = new DellChargeLimitBackend(wmi, executor);

        Assert.True(backend.ProbeForDiagnostics());
        Assert.Equal(100, backend.GetState().Limit);
        Assert.Equal("Standard", backend.CaptureOriginal().Values["PrimaryBattChargeCfg"]);

        // Dell is compiled but has no verified-models rule, so it must refuse every write.
        BackendWriteResult result = await backend.ApplyAsync(new ChargeLimitRequest { Limit = 80, StartThreshold = 75 });
        Assert.False(result.Success);
        Assert.Empty(executor.Plans);
    }

    [Fact]
    public async Task Fake_battery_follows_limit_and_pause()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var battery = new FakeBattery(time, percent: 70) { IsOnAc = true };
        await battery.Backend.SetLimitAsync(80);
        time.Advance(TimeSpan.FromMinutes(5));
        BatterySnapshot full = battery.SnapshotSource.Read();
        Assert.Equal(80, full.Percent);
        Assert.False(full.IsCharging);
        Assert.True(full.IsSimulated);

        await battery.Backend.SetLimitAsync(100);
        await battery.Backend.SetChargingPausedAsync(true);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(80, battery.SnapshotSource.Read().Percent);
    }
}

public class HistoryLoggerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ppm-history-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    [Fact]
    public void Logger_gates_by_interval_rolls_over_daily_and_prunes_after_retention()
    {
        var day1 = new DateTimeOffset(2026, 10, 1, 23, 59, 30, TimeSpan.Zero);
        var time = new ManualTimeProvider(day1);
        var logger = new BatteryHistoryLogger(_folder, time);
        BatterySnapshot snapshot = Snap.At(60, onAc: true, charging: true) with { HealthPercent = 90.5, TemperatureC = 31 };

        Assert.True(logger.Record(snapshot));
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(logger.Record(snapshot));
        time.Advance(TimeSpan.FromSeconds(40));
        Assert.True(logger.Record(snapshot with { HealthPercent = 90.4 }));

        Assert.Equal(2, Directory.GetFiles(logger.Folder, "*.jsonl").Length);
        IReadOnlyList<BatteryHistoryEntry> entries = logger.Read(day1.AddHours(-1), day1.AddHours(1));
        Assert.Equal(2, entries.Count);
        Assert.Equal(60, entries[0].Percent);
        Assert.Equal(31, entries[0].TemperatureC);

        IReadOnlyList<BatteryHealthPoint> trend = logger.ReadHealthTrend();
        Assert.Equal(new[] { 90.5, 90.4 }, trend.Select(p => p.HealthPercent));

        // The first day after a long gap prunes nothing (it may be a clock jump); the next day does.
        time.Advance(TimeSpan.FromDays(40));
        Assert.True(logger.Record(snapshot));
        Assert.Equal(3, Directory.GetFiles(logger.Folder, "*.jsonl").Length);
        time.Advance(TimeSpan.FromDays(1));
        Assert.True(logger.Record(snapshot));
        Assert.Equal(2, Directory.GetFiles(logger.Folder, "*.jsonl").Length);
        Assert.Equal(4, logger.ReadHealthTrend().Count);
    }

    [Fact]
    public void Forward_clock_jump_never_empties_the_history()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));
        var logger = new BatteryHistoryLogger(_folder, time, retentionDays: 30);
        BatterySnapshot snapshot = Snap.At(60, onAc: true);
        for (int day = 0; day < 10; day++)
        {
            Assert.True(logger.Record(snapshot));
            time.Advance(TimeSpan.FromDays(1));
        }
        Assert.Equal(10, Directory.GetFiles(logger.Folder, "*.jsonl").Length);

        // A new logger (an app start) with the clock a year ahead, then put right again.
        time.Advance(TimeSpan.FromDays(365));
        Assert.True(new BatteryHistoryLogger(_folder, time, retentionDays: 30).Record(snapshot));
        Assert.Equal(11, Directory.GetFiles(logger.Folder, "*.jsonl").Length);
        time.Advance(TimeSpan.FromDays(-365));
        Assert.True(new BatteryHistoryLogger(_folder, time, retentionDays: 30).Record(snapshot));
        Assert.Equal(12, Directory.GetFiles(logger.Folder, "*.jsonl").Length);
    }

    [Fact]
    public void Day_after_a_short_gap_keeps_the_window_counted_from_the_newest_recorded_day()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));
        var logger = new BatteryHistoryLogger(_folder, time, retentionDays: 3);
        BatterySnapshot snapshot = Snap.At(60, onAc: true);
        for (int day = 0; day < 3; day++)
        {
            Assert.True(logger.Record(snapshot));
            time.Advance(TimeSpan.FromDays(1));
        }
        // Two days without a sample: Oct 3 is the newest earlier day, so Oct 1-3 stay.
        time.Advance(TimeSpan.FromDays(1));
        Assert.True(logger.Record(snapshot));
        Assert.Equal(4, Directory.GetFiles(logger.Folder, "*.jsonl").Length);
    }

    [Fact]
    public void Long_gap_still_prunes_but_never_within_retention_of_the_newest_recorded_day()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.Zero));
        var logger = new BatteryHistoryLogger(_folder, time, retentionDays: 3);
        Directory.CreateDirectory(logger.Folder);
        for (int day = 1; day <= 10; day++)
            File.WriteAllText(Path.Combine(logger.Folder, $"2026-09-{day:00}.jsonl"), "");

        Assert.True(logger.Record(Snap.At(60, onAc: true)));

        string[] names = Directory.GetFiles(logger.Folder, "*.jsonl").Select(Path.GetFileName).OrderBy(n => n).ToArray()!;
        Assert.Equal(new[] { "2026-09-08.jsonl", "2026-09-09.jsonl", "2026-09-10.jsonl", "2026-09-20.jsonl" }, names);
    }

    [Fact]
    public void Locked_health_file_is_skipped_and_a_damaged_one_kept_aside()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));
        var logger = new BatteryHistoryLogger(_folder, time);
        BatterySnapshot snapshot = Snap.At(60, onAc: true) with { HealthPercent = 92 };
        Assert.True(logger.Record(snapshot));
        time.Advance(TimeSpan.FromDays(1));
        Assert.True(logger.Record(snapshot with { HealthPercent = 91.9 }));
        string health = Path.Combine(logger.Folder, "health-trend.json");

        time.Advance(TimeSpan.FromDays(1));
        using (new FileStream(health, FileMode.Open, FileAccess.Read, FileShare.None))
            logger.Record(snapshot with { HealthPercent = 91.8 });
        Assert.Equal(2, logger.ReadHealthTrend().Count);

        File.WriteAllText(health, "{ damaged");
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(logger.Record(snapshot with { HealthPercent = 91.7 }));
        Assert.Equal("{ damaged", File.ReadAllText(Assert.Single(Directory.GetFiles(logger.Folder, "health-trend.json.corrupt-*"))));
        Assert.Single(logger.ReadHealthTrend());
    }

    [Fact]
    public void Logger_skips_corrupt_lines_and_ignores_missing_battery()
    {
        var now = new DateTimeOffset(2026, 10, 2, 8, 0, 0, TimeSpan.Zero);
        var logger = new BatteryHistoryLogger(_folder, new ManualTimeProvider(now));
        Assert.False(logger.Record(BatterySnapshot.NotPresent));
        Assert.True(logger.Record(Snap.At(55, onAc: false)));
        File.AppendAllText(Directory.GetFiles(logger.Folder, "*.jsonl").Single(), "not json\n");
        Assert.Single(logger.Read(now.AddMinutes(-1), now.AddMinutes(1)));
        logger.Clear();
        Assert.False(Directory.Exists(logger.Folder));
    }
}
