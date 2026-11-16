namespace Portal.Core.Battery;

/// <summary>Why a firmware write is allowed. Anything else is <see cref="Refused"/>.</summary>
public enum WriteGateDecision
{
    Refused,
    /// <summary>The device matches a <see cref="VerifiedModels"/> rule for this backend.</summary>
    Verified,
    /// <summary>Not verified, but the user explicitly opted into beta testing and the change can be read back.</summary>
    BetaOptIn,
    /// <summary>The simulated backend. Never touches hardware.</summary>
    Simulated,
}

public enum WriteRefusal
{
    None,
    /// <summary>The backend's firmware interface was not found on this device.</summary>
    NotDetected,
    /// <summary>The firmware cannot report the value, or the plan has no read-back step. Refused even in beta.</summary>
    NoReadBack,
    /// <summary>The model is not verified and the beta opt-in is off.</summary>
    BetaOff,
    /// <summary>Another app (Power Plan Manager) manages the charge limit on this PC.</summary>
    OtherOwner,
}

/// <param name="Confidence">The backend's own confidence.</param>
/// <param name="ModelVerified">The device matches a <see cref="VerifiedModels"/> rule for this backend.</param>
/// <param name="BetaOptIn">The persisted, user-confirmed beta opt-in.</param>
/// <param name="Detected">The backend's read-only probe found its firmware interface.</param>
/// <param name="FirmwareReportsState">The firmware can report the current value (false for ASUS).</param>
/// <param name="PlanHasReadBack">The write plan contains a read-back verify step. Pass true before a plan exists.</param>
/// <param name="OtherOwner">Name of another app that manages the limit, or null.</param>
public sealed record WriteGateInput(
    BackendConfidence Confidence,
    bool ModelVerified,
    bool BetaOptIn,
    bool Detected,
    bool FirmwareReportsState,
    bool PlanHasReadBack,
    string? OtherOwner);

public sealed record WriteGateResult(WriteGateDecision Decision, WriteRefusal Refusal, string Message)
{
    public bool Allowed => Decision != WriteGateDecision.Refused;
}

/// <summary>
/// The one place that decides whether a firmware write or restore may run. Every hardware backend calls it before
/// building a plan and again with the finished plan. <see cref="VerifiedModels"/> stays the only source of "verified";
/// the beta opt-in never edits that list.
/// </summary>
public static class WriteGate
{
    public static WriteGateResult Evaluate(WriteGateInput input)
    {
        if (input.Confidence == BackendConfidence.Simulated)
            return new(WriteGateDecision.Simulated, WriteRefusal.None, "Simulated backend.");
        if (input.OtherOwner is { Length: > 0 } owner)
            return Refuse(WriteRefusal.OtherOwner, $"{owner} manages the charge limit on this PC, so nothing was changed. Turn off charge control in one of the two apps first.");
        if (!input.Detected)
            return Refuse(WriteRefusal.NotDetected, "The charge-limit interface for this laptop was not found, so nothing was changed.");
        if (!input.FirmwareReportsState || !input.PlanHasReadBack)
            return Refuse(WriteRefusal.NoReadBack, "This change cannot be read back from the firmware, so it was not made.");
        if (input.ModelVerified)
            return new(WriteGateDecision.Verified, WriteRefusal.None, "Verified model.");
        if (input.BetaOptIn)
            return new(WriteGateDecision.BetaOptIn, WriteRefusal.None, "Beta testing is on for this laptop.");
        return Refuse(WriteRefusal.BetaOff, "This laptop model is not on the verified list and beta testing is off, so nothing was changed.");
    }

    private static WriteGateResult Refuse(WriteRefusal refusal, string message) => new(WriteGateDecision.Refused, refusal, message);
}

/// <summary>Live inputs the gate needs from the app. The defaults refuse beta and report no other owner.</summary>
public sealed class WriteGateContext
{
    public static WriteGateContext Default { get; } = new();

    public Func<bool> BetaOptIn { get; init; } = () => false;

    /// <summary>Returns the name of another app that manages the limit, or null.</summary>
    public Func<string?> OtherOwner { get; init; } = () => null;
}

public enum ReadBackCheck
{
    /// <summary>No write was attempted, or the result does not carry a check.</summary>
    NotChecked,
    Matched,
    Mismatch,
    /// <summary>The app could not read the state itself (the elevated helper's own verify step passed).</summary>
    Unreadable,
}

/// <summary>Compares what was asked for with what the app itself read back from the firmware.</summary>
public static class ReadBackVerifier
{
    public static ReadBackCheck Compare(ChargeLimitRequest request, ChargeLimitState state)
    {
        bool anyRead = state.Limit is not null || state.StartThreshold is not null || state.ChargingPaused is not null;
        if (request.Limit is int limit)
        {
            if (state.Limit is not int read) return ReadBackCheck.Unreadable;
            if (read != limit) return ReadBackCheck.Mismatch;
        }
        if (request.StartThreshold is int start)
        {
            if (state.StartThreshold is not int read) return anyRead ? ReadBackCheck.Mismatch : ReadBackCheck.Unreadable;
            if (read != start) return ReadBackCheck.Mismatch;
        }
        if (request.ChargingPaused is bool paused)
        {
            if (state.ChargingPaused is not bool read) return anyRead ? ReadBackCheck.Mismatch : ReadBackCheck.Unreadable;
            if (read != paused) return ReadBackCheck.Mismatch;
        }
        return anyRead ? ReadBackCheck.Matched : ReadBackCheck.Unreadable;
    }

    /// <summary>Compares a restore against the state the saved originals describe.</summary>
    public static ReadBackCheck CompareRestore(ChargeLimitState expected, ChargeLimitState state) =>
        Compare(new ChargeLimitRequest { Limit = expected.Limit, StartThreshold = expected.StartThreshold }, state);
}
