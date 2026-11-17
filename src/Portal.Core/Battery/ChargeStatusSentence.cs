namespace Portal.Core.Battery;

/// <summary>Every state the charge-limit area can be in. The resource key for its sentence is "ChargeStatus" + the name.</summary>
public enum ChargeStatusKind
{
    NoBattery,
    MonitorMode,
    OneStepMissing,
    NotDetected,
    StateFileUnusable,
    Writing,
    NeedsElevation,
    Cancelled,
    ReadBackMismatch,
    PpmOwns,
    PpmUnconfirmed,
    BetaOff,
    NotVerified,
    ChangedElsewhere,
    ReapplyFailed,
    NeedsReapply,
    Unreadable,
    On,
    Off,
}

/// <summary>What the status sentence is worked out from. Plain values only, so it can be tested without hardware.</summary>
public sealed record ChargeStatusInput
{
    public DeviceSupportState Support { get; init; } = DeviceSupportState.Supported;
    /// <summary>A controller exists, so the write gate has an answer.</summary>
    public bool HasController { get; init; }
    public bool BetaOptIn { get; init; }
    public bool Busy { get; init; }
    /// <summary>Outcome of the last change the user made on this page, if it matters still.</summary>
    public ChargeActionStatus? LastAction { get; init; }
    public WriteRefusal Refusal { get; init; } = WriteRefusal.None;
    public IntentFileState IntentState { get; init; } = IntentFileState.Ok;
    /// <summary>The app manages the limit and wants it on.</summary>
    public bool ManagedLimitOn { get; init; }
    public bool NeedsReapply { get; init; }
    public bool ReapplyFailed { get; init; }
    public ChargeActionStatus? LastReapply { get; init; }
    /// <summary>Limit read back from the firmware (100 = none). Null when unreadable.</summary>
    public int? Limit { get; init; }
}

public static class ChargeStatusSentence
{
    public const string KeyPrefix = "ChargeStatus";

    public static string ResourceKey(ChargeStatusKind kind) => KeyPrefix + kind;

    public static ChargeStatusKind Resolve(ChargeStatusInput i)
    {
        switch (i.Support)
        {
            case DeviceSupportState.NoBattery: return ChargeStatusKind.NoBattery;
            case DeviceSupportState.MonitorMode: return ChargeStatusKind.MonitorMode;
            case DeviceSupportState.OneStepMissing: return ChargeStatusKind.OneStepMissing;
        }

        if (!i.HasController)
        {
            return i.Support == DeviceSupportState.OnTestList
                ? (i.BetaOptIn ? ChargeStatusKind.NotVerified : ChargeStatusKind.BetaOff)
                : ChargeStatusKind.NotDetected;
        }

        if (i.IntentState == IntentFileState.Unusable) return ChargeStatusKind.StateFileUnusable;
        if (i.Busy) return ChargeStatusKind.Writing;

        switch (i.LastAction)
        {
            case ChargeActionStatus.NeedsElevation: return ChargeStatusKind.NeedsElevation;
            case ChargeActionStatus.Cancelled: return ChargeStatusKind.Cancelled;
            case ChargeActionStatus.ReadBackMismatch: return ChargeStatusKind.ReadBackMismatch;
        }

        switch (i.Refusal)
        {
            case WriteRefusal.OtherOwner: return ChargeStatusKind.PpmOwns;
            case WriteRefusal.OtherOwnerUnconfirmed: return ChargeStatusKind.PpmUnconfirmed;
            case WriteRefusal.BetaOff: return ChargeStatusKind.BetaOff;
            case WriteRefusal.NoReadBack: return ChargeStatusKind.NotVerified;
            case WriteRefusal.NotDetected: return ChargeStatusKind.NotDetected;
        }

        if (i.LastReapply == ChargeActionStatus.ChangedElsewhere) return ChargeStatusKind.ChangedElsewhere;
        if (i.ManagedLimitOn && i.ReapplyFailed) return ChargeStatusKind.ReapplyFailed;
        if (i.ManagedLimitOn && i.NeedsReapply) return ChargeStatusKind.NeedsReapply;

        return i.Limit switch
        {
            null => ChargeStatusKind.Unreadable,
            < 100 => ChargeStatusKind.On,
            _ => ChargeStatusKind.Off,
        };
    }
}
