namespace Portal.Core.Power;

public enum EightyPlusLevel
{
    None,
    White,
    Bronze,
    Silver,
    Gold,
    Platinum,
    Titanium,
}

/// <summary>Per-PC correction learned from a wall meter at low load. Identity until learned.</summary>
public sealed class PowerCalibration
{
    /// <summary>Watts added to the motherboard baseline estimate.</summary>
    public double BaselineOffsetW { get; set; }
    /// <summary>Factor applied to every estimated node (after the baseline offset).</summary>
    public double Scale { get; set; } = 1.0;
    /// <summary>Number of accepted learning samples; 0 means not calibrated.</summary>
    public int Samples { get; set; }
    public DateTimeOffset? LearnedUtc { get; set; }
    public string? SourceName { get; set; }

    public bool IsLearned => Samples > 0;

    public PowerCalibration Clone() => new()
    {
        BaselineOffsetW = BaselineOffsetW,
        Scale = Scale,
        Samples = Samples,
        LearnedUtc = LearnedUtc,
        SourceName = SourceName,
    };

    public PowerCalibration Normalize()
    {
        if (!double.IsFinite(Scale) || Scale <= 0) Scale = 1.0;
        Scale = Math.Clamp(Scale, PowerCalibrator.MinScale, PowerCalibrator.MaxScale);
        if (!double.IsFinite(BaselineOffsetW)) BaselineOffsetW = 0;
        BaselineOffsetW = Math.Clamp(BaselineOffsetW, -40, 60);
        if (Samples < 0) Samples = 0;
        return this;
    }
}

/// <summary>Persisted power model settings (one JSON value in LocalSettings).</summary>
public sealed class PowerModelSettings
{
    public const int MinPsuWatts = 150;
    public const int MaxPsuWatts = 3000;

    /// <summary>Rated (DC) output of the power supply.</summary>
    public int PsuRatedWatts { get; set; } = 750;
    public EightyPlusLevel PsuLevel { get; set; } = EightyPlusLevel.Gold;
    /// <summary>True on 220–240 V mains, which makes a PSU about 1.5 points more efficient.</summary>
    public bool HighLineMains { get; set; }
    /// <summary>Learn the calibration automatically while a wall meter is fresh and the PC is near idle.</summary>
    public bool AutoCalibrate { get; set; } = true;
    public PowerCalibration Calibration { get; set; } = new();
    /// <summary>The river card is folded down to its row of live values.</summary>
    public bool RiverCollapsed { get; set; }

    public PowerModelSettings Normalize()
    {
        PsuRatedWatts = Math.Clamp(PsuRatedWatts, MinPsuWatts, MaxPsuWatts);
        if (!Enum.IsDefined(PsuLevel)) PsuLevel = EightyPlusLevel.Gold;
        Calibration = (Calibration ?? new()).Normalize();
        return this;
    }

    public PowerModelSettings Clone() => new()
    {
        PsuRatedWatts = PsuRatedWatts,
        PsuLevel = PsuLevel,
        HighLineMains = HighLineMains,
        AutoCalibrate = AutoCalibrate,
        Calibration = Calibration.Clone(),
        RiverCollapsed = RiverCollapsed,
    };
}

/// <summary>Load and save of <see cref="PowerModelSettings"/>; the app backs it with LocalSettings.</summary>
public interface IPowerModelSettingsStore
{
    PowerModelSettings Load();
    void Save(PowerModelSettings settings);
}

/// <summary>Keeps the settings in memory only (tests, or before the app's store is ready).</summary>
public sealed class InMemoryPowerModelSettingsStore : IPowerModelSettingsStore
{
    private PowerModelSettings _settings;
    public InMemoryPowerModelSettingsStore(PowerModelSettings? settings = null) => _settings = (settings ?? new()).Normalize();
    public int SaveCount { get; private set; }
    public PowerModelSettings Load() => _settings.Clone();
    public void Save(PowerModelSettings settings) { _settings = settings.Clone().Normalize(); SaveCount++; }
}

/// <summary>Store backed by delegates, so the app can pass its settings service property.</summary>
public sealed class DelegatePowerModelSettingsStore(Func<PowerModelSettings> load, Action<PowerModelSettings> save) : IPowerModelSettingsStore
{
    public PowerModelSettings Load()
    {
        try { return (load() ?? new()).Normalize(); }
        catch (Exception) { return new PowerModelSettings().Normalize(); }
    }

    public void Save(PowerModelSettings settings)
    {
        try { save(settings.Clone().Normalize()); }
        catch (Exception) { }
    }
}
