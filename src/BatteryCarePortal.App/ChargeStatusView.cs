using Portal.Core.Battery;

namespace BatteryCarePortal;

/// <summary>The status sentence and the read-back limit for the Overview and Charging pages. Reads only.</summary>
internal sealed record ChargeStatusView(ChargeStatusKind Kind, int? Limit)
{
    /// <summary>The app's intent, read with the state; the Top up controls use it.</summary>
    public ChargeIntent? Intent { get; init; }

    public string Sentence => Kind == ChargeStatusKind.TopUp && Intent is { } intent
        ? string.Format(Strings.Get(ChargeStatusSentence.ResourceKey(Kind)), intent.Limit, TopUpEndText(intent.TopUpUntil))
        : string.Format(Strings.Get(ChargeStatusSentence.ResourceKey(Kind)), Limit ?? 0);

    public static string TopUpEndText(DateTimeOffset? until) =>
        until is DateTimeOffset u ? u.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture) : BatteryFormat.None;

    public static string SentenceFor(ChargeStatusKind kind, int? limit = null) =>
        string.Format(Strings.Get(ChargeStatusSentence.ResourceKey(kind)), limit ?? 0);

    /// <summary>Call off the UI thread: it reads the firmware state and the saved intent file.</summary>
    public static ChargeStatusView Build(ChargeActionStatus? lastAction, bool busy)
    {
        DeviceSupport? support = AppServices.Support;
        ChargeLimitController? controller = AppServices.Charge;
        if (support is null) return new ChargeStatusView(ChargeStatusKind.Unreadable, null);

        if (controller is null)
        {
            return new ChargeStatusView(ChargeStatusSentence.Resolve(new ChargeStatusInput
            {
                Support = support.State,
                BetaOptIn = support.BetaOptIn,
            }), support.CurrentLimit);
        }

        ChargeLimitState state = controller.ReadState();
        WriteGateResult gate = controller.EvaluateGate();
        IntentFileState intentState = controller.IntentState;
        ChargeIntent? intent = controller.LoadIntent();
        bool managed = intent is { Managed: true, LimitOn: true };
        var input = new ChargeStatusInput
        {
            Support = support.State,
            HasController = true,
            BetaOptIn = support.BetaOptIn,
            Busy = busy,
            LastAction = lastAction,
            Refusal = gate.Allowed ? WriteRefusal.None : gate.Refusal,
            IntentState = intentState,
            ManagedLimitOn = managed,
            NeedsReapply = intent?.NeedsReapply ?? false,
            ReapplyFailed = intent?.ReapplyFailed ?? false,
            TopUpActive = intent?.TopUpActive ?? false,
            LastReapply = intent is { Managed: true } ? AppServices.LastReapply?.Status : null,
            Limit = state.Limit,
        };
        bool topUpOffered = gate.Allowed && Portal.Core.Services.FeatureTiers.IsAvailable(Portal.Core.Services.PortalFeature.TopUp, hasPro: false);
        return new ChargeStatusView(ChargeStatusSentence.Resolve(input), state.Limit)
        {
            Intent = intent,
            CanStartTopUp = topUpOffered &&
                TopUpPolicy.CanStart(intent, state.Limit, AppServices.Battery.Current.Percent, controller.TopUpSupported),
            CanStopTopUp = TopUpPolicy.CanStop(intent),
        };
    }

    /// <summary>
    /// Show "Top up to 100%": the backend writes without elevation, the app's limit is on and in place, and the write gate
    /// allows a change.
    /// </summary>
    public bool CanStartTopUp { get; init; }

    /// <summary>Show "Stop top-up": a top-up is saved as running, even when the write gate refuses (the click then shows why).</summary>
    public bool CanStopTopUp { get; init; }

    /// <summary>The user-facing text for a write-gate refusal.</summary>
    public static string RefusalText(WriteRefusal refusal, string fallback) => refusal switch
    {
        WriteRefusal.OtherOwner => Strings.Get("ChargeStatusPpmOwns"),
        WriteRefusal.OtherOwnerUnconfirmed => Strings.Get("ChargeStatusPpmUnconfirmed"),
        WriteRefusal.BetaOff => Strings.Get("ChargeStatusBetaOff"),
        WriteRefusal.NoReadBack => Strings.Get("ChargeStatusNotVerified"),
        WriteRefusal.NotDetected => Strings.Get("ChargeStatusNotDetected"),
        _ => fallback,
    };
}
