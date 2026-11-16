using System.Globalization;
using System.Text.RegularExpressions;

namespace Portal.Core.Battery.Backends;

/// <summary>
/// Dell client BIOS WMI (root\dcim\sysman\biosattributes): PrimaryBattChargeCfg = Custom with
/// CustomChargeStart / CustomChargeStop, written through BIOSAttributeInterface.SetAttribute.
/// Unverified: needs on-device confirmation of argument types before it is enabled by default.
/// </summary>
public sealed class DellChargeLimitBackend : ChargeLimitBackendBase
{
    private const string Ns = @"root\dcim\sysman\biosattributes";
    private const string ModeAttribute = "PrimaryBattChargeCfg";
    private const string StartAttribute = "CustomChargeStart";
    private const string StopAttribute = "CustomChargeStop";

    public DellChargeLimitBackend(IWmiReader wmi, IBatteryWriteExecutor executor, TimeProvider? time = null)
        : base(wmi, executor, time) { }

    public override string Id => "dell-bios-custom-charge";
    public override string Name => "Dell custom charge";
    public override BackendConfidence Confidence => BackendConfidence.Unverified;

    protected override bool DetectCore()
    {
        if (ReadEnum(ModeAttribute) is not { } mode || !mode.Possible.Contains("Custom", StringComparer.OrdinalIgnoreCase)) return false;
        if (ReadInt(StartAttribute) is not { } start || ReadInt(StopAttribute) is not { } stop) return false;
        Capabilities = new ChargeLimitCapabilities
        {
            Kind = ChargeLimitKind.ContinuousRange,
            MinLimit = (int)Math.Max(50, stop.Lower),
            MaxLimit = (int)Math.Min(100, stop.Upper),
            SupportsStartThreshold = true,
            MinStartThreshold = (int)Math.Max(40, start.Lower),
            MaxStartThreshold = (int)Math.Min(95, start.Upper),
            MinStartStopGap = 5,
        };
        return Capabilities.MinLimit < Capabilities.MaxLimit;
    }

    protected override ChargeLimitState GetStateCore()
    {
        var mode = ReadEnum(ModeAttribute);
        var start = ReadInt(StartAttribute);
        var stop = ReadInt(StopAttribute);
        if (mode is null || start is null || stop is null) return new ChargeLimitState { Message = "The Dell BIOS charge settings could not be read." };
        bool custom = string.Equals(mode.Value.Current, "Custom", StringComparison.OrdinalIgnoreCase);
        return new ChargeLimitState
        {
            Limit = custom ? (int)stop.Value.Current : 100,
            StartThreshold = custom ? (int)start.Value.Current : null,
            RawValue = $"{mode.Value.Current}; start {start.Value.Current}; stop {stop.Value.Current}",
        };
    }

    public override BackendOriginalState CaptureOriginal()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ReadEnum(ModeAttribute) is { } mode) values[ModeAttribute] = mode.Current;
        if (ReadInt(StartAttribute) is { } start) values[StartAttribute] = start.Current.ToString(CultureInfo.InvariantCulture);
        if (ReadInt(StopAttribute) is { } stop) values[StopAttribute] = stop.Current.ToString(CultureInfo.InvariantCulture);
        return NewOriginal(values);
    }

    protected override BatteryWritePlan? BuildPlan(ChargeLimitRequest request, ChargeLimitState current, out string? error)
    {
        error = null;
        if (request.ChargingPaused is not null) { error = "Dell firmware cannot pause charging."; return null; }
        int stop = request.Limit ?? current.Limit ?? 100;
        int start = request.ResetStartThreshold || request.StartThreshold is null && current.StartThreshold is null
            ? Math.Max(Capabilities.MinStartThreshold, stop - 5)
            : request.StartThreshold ?? current.StartThreshold!.Value;
        start = Math.Min(start, stop - Capabilities.MinStartStopGap);
        if (start < Capabilities.MinStartThreshold) { error = "The limit is too low for this BIOS."; return null; }

        string currentMode = ReadEnum(ModeAttribute)?.Current ?? string.Empty;
        return BuildSetPlan("Set Dell custom charge", "Custom", currentMode, start, stop);
    }

    protected override BatteryWritePlan? BuildRestorePlan(BackendOriginalState original, ChargeLimitState current, out string? error)
    {
        error = null;
        if (!original.Values.TryGetValue(ModeAttribute, out string? mode) ||
            !int.TryParse(original.Values.GetValueOrDefault(StartAttribute), NumberStyles.Integer, CultureInfo.InvariantCulture, out int start) ||
            !int.TryParse(original.Values.GetValueOrDefault(StopAttribute), NumberStyles.Integer, CultureInfo.InvariantCulture, out int stop))
        {
            error = "The original Dell values were never read.";
            return null;
        }
        string currentMode = ReadEnum(ModeAttribute)?.Current ?? string.Empty;
        return BuildSetPlan("Restore Dell charge settings", mode, currentMode, start, stop);
    }

    private BatteryWritePlan BuildSetPlan(string description, string mode, string currentMode, int start, int stop)
    {
        // Order by the raw BIOS values, which are kept even while the mode is not Custom.
        long? currentStop = ReadInt(StopAttribute)?.Current;
        var plan = new BatteryWritePlan
        {
            BackendId = Id,
            Description = description,
            Checks =
            {
                new BatteryWriteCheck
                {
                    Namespace = Ns, Query = "SELECT IsPasswordSet FROM PasswordObject WHERE NameId='Admin'",
                    Property = "IsPasswordSet", ExpectedValue = "0", MissingIsFailure = true,
                    FailMessage = "A BIOS administrator password is set, so the charge settings were not changed.",
                },
            },
        };
        // The BIOS rejects start >= stop at every point, so the order depends on the direction of the move.
        bool raiseStopFirst = currentStop is null || stop >= currentStop || start >= currentStop;
        if (raiseStopFirst) { AddSet(plan, StopAttribute, stop); AddSet(plan, StartAttribute, start); }
        else { AddSet(plan, StartAttribute, start); AddSet(plan, StopAttribute, stop); }
        if (!string.Equals(mode, currentMode, StringComparison.OrdinalIgnoreCase)) AddSet(plan, ModeAttribute, mode);
        return plan;
    }

    private static void AddSet(BatteryWritePlan plan, string attribute, object value)
    {
        string text = Convert.ToString(value, CultureInfo.InvariantCulture)!;
        plan.Steps.Add(new BatteryWriteStep
        {
            Kind = BatteryWriteStepKind.WmiMethod,
            Label = $"Set {attribute}",
            Namespace = Ns, ClassName = "BIOSAttributeInterface", MethodName = "SetAttribute", OnInstance = true,
            Arguments =
            {
                WmiArgument.UInt32("SecType", 0), WmiArgument.UInt32("SecHndCount", 0), WmiArgument.EmptyBytes("SecHandle"),
                WmiArgument.String("AttributeName", attribute), WmiArgument.String("AttributeValue", text),
            },
            SuccessProperty = "Status",
            SuccessValues = { "0" },
        });
        bool isEnum = attribute == ModeAttribute;
        plan.Steps.Add(new BatteryWriteStep
        {
            Kind = BatteryWriteStepKind.VerifyWmiProperty,
            Label = $"Read back {attribute}",
            Namespace = Ns,
            Query = $"SELECT CurrentValue FROM {(isEnum ? "EnumerationAttribute" : "IntegerAttribute")} WHERE AttributeName='{attribute}'",
            Property = "CurrentValue",
            ExpectedValue = text,
        });
    }

    private (string Current, string[] Possible)? ReadEnum(string name)
    {
        var row = Wmi.Query(Ns, $"SELECT CurrentValue, PossibleValue FROM EnumerationAttribute WHERE AttributeName='{name}'").FirstOrDefault();
        if (row is null) return null;
        string? current = row.TryGetValue("CurrentValue", out object? v) && v is string[] arr ? arr.FirstOrDefault() : v?.ToString();
        return current is null ? null : (current, row.AsStringArray("PossibleValue"));
    }

    private (long Current, long Lower, long Upper)? ReadInt(string name)
    {
        var row = Wmi.Query(Ns, $"SELECT CurrentValue, LowerBound, UpperBound FROM IntegerAttribute WHERE AttributeName='{name}'").FirstOrDefault();
        if (row?.AsLong("CurrentValue") is not long current) return null;
        return (current, row.AsLong("LowerBound") ?? 50, row.AsLong("UpperBound") ?? 100);
    }
}

/// <summary>
/// HP BIOS WMI (root\HP\InstrumentedBIOS): a battery-care enumeration setting whose possible values include
/// fixed percentages, written through HP_BIOSSettingInterface.SetBIOSSetting. The setting name and value
/// wording vary by model, so this stays unverified until checked on an HP device.
/// </summary>
public sealed class HpChargeLimitBackend : ChargeLimitBackendBase
{
    private const string Ns = @"root\HP\InstrumentedBIOS";
    private static readonly string[] CandidateSettings = { "Battery Care Function", "Adaptive Battery Optimizer" };
    private static readonly Regex PercentPattern = new(@"^\s*(\d{2,3})\s*%", RegexOptions.CultureInvariant);

    private string? _setting;
    private Dictionary<int, string> _valueByPercent = new();

    public HpChargeLimitBackend(IWmiReader wmi, IBatteryWriteExecutor executor, TimeProvider? time = null)
        : base(wmi, executor, time) { }

    public override string Id => "hp-bios-battery-care";
    public override string Name => "HP battery care";
    public override BackendConfidence Confidence => BackendConfidence.Unverified;

    protected override bool DetectCore()
    {
        foreach (string candidate in CandidateSettings)
        {
            var row = Wmi.Query(Ns, $"SELECT Name, CurrentValue, PossibleValues FROM HP_BIOSEnumeration WHERE Name='{candidate}'").FirstOrDefault();
            if (row is null) continue;
            var map = new Dictionary<int, string>();
            foreach (string value in row.AsStringArray("PossibleValues"))
            {
                Match m = PercentPattern.Match(value);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int p) && p is >= 50 and <= 100) map[p] = value;
            }
            if (map.Count < 2) continue;
            _setting = candidate;
            _valueByPercent = map;
            Capabilities = new ChargeLimitCapabilities { Kind = ChargeLimitKind.FixedValues, FixedValues = map.Keys.OrderBy(k => k).ToArray() };
            return true;
        }
        return false;
    }

    protected override ChargeLimitState GetStateCore()
    {
        string? current = ReadCurrent();
        if (current is null) return new ChargeLimitState { Message = "The HP battery care setting could not be read." };
        int? limit = _valueByPercent.FirstOrDefault(kv => kv.Value == current) is { Value: not null } kv ? kv.Key : null;
        return new ChargeLimitState { Limit = limit, RawValue = current, Message = limit is null ? $"Unrecognised value '{current}'." : null };
    }

    public override BackendOriginalState CaptureOriginal()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_setting is not null && ReadCurrent() is string current) values[_setting] = current;
        return NewOriginal(values);
    }

    protected override BatteryWritePlan? BuildPlan(ChargeLimitRequest request, ChargeLimitState current, out string? error)
    {
        error = null;
        if (request.Limit is not int limit || !_valueByPercent.TryGetValue(limit, out string? value)) { error = "Unsupported value."; return null; }
        return current.RawValue == value ? null : BuildSetPlan(value);
    }

    protected override BatteryWritePlan? BuildRestorePlan(BackendOriginalState original, ChargeLimitState current, out string? error)
    {
        error = null;
        if (_setting is null || !original.Values.TryGetValue(_setting, out string? value)) { error = "The original HP value was never read."; return null; }
        return current.RawValue == value ? null : BuildSetPlan(value);
    }

    private BatteryWritePlan BuildSetPlan(string value) => new()
    {
        BackendId = Id,
        Description = $"Set {_setting}",
        Checks =
        {
            new BatteryWriteCheck
            {
                Namespace = Ns, Query = "SELECT IsSet FROM HP_BIOSPassword WHERE Name='Setup Password'",
                Property = "IsSet", ExpectedValue = "0", MissingIsFailure = true,
                FailMessage = "A BIOS setup password is set, so the battery care setting was not changed.",
            },
        },
        Steps =
        {
            new BatteryWriteStep
            {
                Kind = BatteryWriteStepKind.WmiMethod, Label = $"Set {_setting}",
                Namespace = Ns, ClassName = "HP_BIOSSettingInterface", MethodName = "SetBIOSSetting", OnInstance = true,
                Arguments = { WmiArgument.String("Name", _setting!), WmiArgument.String("Value", value), WmiArgument.String("Password", "<utf-16/>") },
                SuccessProperty = "Return", SuccessValues = { "0" },
            },
            new BatteryWriteStep
            {
                Kind = BatteryWriteStepKind.VerifyWmiProperty, Label = $"Read back {_setting}",
                Namespace = Ns, Query = $"SELECT CurrentValue FROM HP_BIOSEnumeration WHERE Name='{_setting}'",
                Property = "CurrentValue", ExpectedValue = value,
            },
        },
    };

    private string? ReadCurrent() => _setting is null ? null
        : Wmi.Query(Ns, $"SELECT CurrentValue FROM HP_BIOSEnumeration WHERE Name='{_setting}'").FirstOrDefault()?.AsString("CurrentValue");
}

/// <summary>
/// ASUS ATK WMI (root\wmi AsusAtkWmi_WMNB): DEVS(0x00120057, limit) sets the charge limit, DSTS reports
/// whether the device id exists. Firmware does not report the current limit, so writes cannot be read back;
/// unverified and disabled by default.
/// </summary>
public sealed class AsusChargeLimitBackend : ChargeLimitBackendBase
{
    private const string Ns = @"root\wmi";
    private const string ClassName = "AsusAtkWmi_WMNB";
    public const uint ChargeLimitDeviceId = 0x00120057;
    private const long PresenceBit = 0x00010000;

    public AsusChargeLimitBackend(IWmiReader wmi, IBatteryWriteExecutor executor, TimeProvider? time = null)
        : base(wmi, executor, time) { }

    public override string Id => "asus-atk-charge-limit";
    public override string Name => "ASUS battery charge limit";
    public override BackendConfidence Confidence => BackendConfidence.Unverified;
    protected override bool AllowWriteWithoutReadBack => true;

    protected override bool DetectCore()
    {
        var result = Wmi.InvokeGetter(Ns, ClassName, "DSTS", new Dictionary<string, object> { ["Device_ID"] = ChargeLimitDeviceId });
        if (result?.AsLong("device_status") is not long status || (status & PresenceBit) == 0) return false;
        Capabilities = new ChargeLimitCapabilities { Kind = ChargeLimitKind.ContinuousRange, MinLimit = 50, MaxLimit = 100 };
        return true;
    }

    protected override ChargeLimitState GetStateCore() =>
        new() { Message = "ASUS firmware does not report the current charge limit." };

    public override BackendOriginalState CaptureOriginal() =>
        NewOriginal(new Dictionary<string, string> { ["Limit"] = "100" });

    protected override BatteryWritePlan? BuildPlan(ChargeLimitRequest request, ChargeLimitState current, out string? error)
    {
        error = null;
        if (request.Limit is not int limit) { error = "Only a charge limit is supported."; return null; }
        return BuildSetPlan(limit);
    }

    protected override BatteryWritePlan? BuildRestorePlan(BackendOriginalState original, ChargeLimitState current, out string? error)
    {
        error = null;
        return BuildSetPlan(100);
    }

    private BatteryWritePlan BuildSetPlan(int limit) => new()
    {
        BackendId = Id,
        Description = $"Set ASUS charge limit to {limit} %",
        Steps =
        {
            new BatteryWriteStep
            {
                Kind = BatteryWriteStepKind.WmiMethod, Label = "Set charge limit",
                Namespace = Ns, ClassName = ClassName, MethodName = "DEVS", OnInstance = true,
                Arguments = { WmiArgument.UInt32("Device_ID", ChargeLimitDeviceId), WmiArgument.UInt32("Control_status", (uint)limit) },
                SuccessProperty = "result", SuccessValues = { "1" },
            },
        },
    };
}
