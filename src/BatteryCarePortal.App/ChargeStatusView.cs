using Portal.Core.Battery;

namespace BatteryCarePortal;

/// <summary>The status sentence and the read-back limit for the Overview and Charging pages. Reads only.</summary>
internal sealed record ChargeStatusView(ChargeStatusKind Kind, int? Limit)
{
    public string Sentence => string.Format(Strings.Get(ChargeStatusSentence.ResourceKey(Kind)), Limit ?? 0);

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
            LastReapply = intent is { Managed: true } ? AppServices.LastReapply?.Status : null,
            Limit = state.Limit,
        };
        return new ChargeStatusView(ChargeStatusSentence.Resolve(input), state.Limit);
    }
}
