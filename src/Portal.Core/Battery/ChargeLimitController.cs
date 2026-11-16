using Portal.Core.Battery.Backends;

namespace Portal.Core.Battery;

public enum ChargeActionStatus
{
    Applied,
    AlreadySet,
    /// <summary>The write ran and the elevated helper verified it, but the app could not read the state itself.</summary>
    AppliedNotConfirmed,
    /// <summary>Another write is in flight; nothing was done.</summary>
    Busy,
    /// <summary>The write gate refused; see <see cref="ChargeActionResult.Refusal"/>.</summary>
    Refused,
    NeedsElevation,
    Cancelled,
    ReadBackMismatch,
    /// <summary>The original firmware values could not be read or saved, so nothing was written.</summary>
    OriginalsUnavailable,
    NothingToRestore,
    /// <summary>The firmware shows a value the app did not set; the user decides.</summary>
    ChangedElsewhere,
    NothingToDo,
    Failed,
}

public sealed record ChargeActionResult(ChargeActionStatus Status, string Message, ChargeLimitState? State = null,
    WriteRefusal Refusal = WriteRefusal.None)
{
    public bool Succeeded => Status is ChargeActionStatus.Applied or ChargeActionStatus.AlreadySet or ChargeActionStatus.AppliedNotConfirmed;
}

/// <summary>
/// The app's single writer for the charge limit. Allows one write at a time, saves the original firmware values before
/// the first write, and checks every write by reading the state back itself.
/// </summary>
public sealed class ChargeLimitController
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly OriginalStateStore _originals;
    private readonly TimeProvider _time;

    public ChargeLimitController(IChargeLimitBackend backend, OriginalStateStore originals, TimeProvider? time = null)
    {
        Backend = backend;
        _originals = originals;
        _time = time ?? TimeProvider.System;
    }

    public IChargeLimitBackend Backend { get; }

    public bool IsBusy => _writeLock.CurrentCount == 0;

    public ChargeLimitCapabilities Capabilities => Backend.Capabilities;

    /// <summary>Read-only.</summary>
    public ChargeLimitState ReadState() => Backend.GetState();

    /// <summary>The write gate as it stands now. Read-only.</summary>
    public WriteGateResult EvaluateGate() => Backend is ChargeLimitBackendBase b
        ? b.EvaluateGate()
        : new WriteGateResult(WriteGateDecision.Simulated, WriteRefusal.None, "Simulated backend.");

    public bool HasSavedOriginals => _originals.Get(Backend.Id, out _) is not null;

    /// <summary>The limit a request for on/off and an optional value resolves to, or null when unsupported.</summary>
    public int? TargetLimit(bool on, int? value)
    {
        if (!Capabilities.SupportsLimit) return null;
        if (!on) return 100;
        if (Capabilities.Kind == ChargeLimitKind.OnOffConservation) return Capabilities.ConservationPercent;
        return Capabilities.Snap(value ?? 80);
    }

    /// <summary>User action: turns the limit on (at <paramref name="value"/> where the backend supports a value) or off. May prompt for elevation.</summary>
    public async Task<ChargeActionResult> SetLimitAsync(bool on, int? value = null, CancellationToken cancellationToken = default)
    {
        if (TargetLimit(on, value) is not int target)
            return new(ChargeActionStatus.Failed, "This laptop does not support a charge limit.");
        if (!_writeLock.Wait(0)) return Busy();
        try
        {
            WriteGateResult gate = EvaluateGate();
            if (!gate.Allowed) return Refused(gate.Refusal, gate.Message);
            if (EnsureOriginals() is { } failed) return failed;

            var request = new ChargeLimitRequest { Limit = target, AllowElevationPrompt = true };
            BackendWriteResult result = await Backend.ApplyAsync(request, cancellationToken).ConfigureAwait(false);
            return Verify(result, state => ReadBackVerifier.Compare(request, state));
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>User action: puts the saved original firmware values back and reads them back. May prompt for elevation.</summary>
    public async Task<ChargeActionResult> RestoreDefaultsAsync(CancellationToken cancellationToken = default)
    {
        if (!_writeLock.Wait(0)) return Busy();
        try
        {
            BackendOriginalState? original = _originals.Get(Backend.Id, out bool unreadable);
            if (unreadable)
                return new(ChargeActionStatus.OriginalsUnavailable, "The saved original values cannot be read, so nothing was changed.");
            if (original is null)
                return new(ChargeActionStatus.NothingToRestore, "The app has not changed this laptop's charge settings, so there is nothing to restore.");
            if (Backend.ExpectedStateAfterRestore(original) is not { } expected)
                return new(ChargeActionStatus.OriginalsUnavailable, "The saved original values are incomplete, so nothing was changed.");

            WriteGateResult gate = EvaluateGate();
            if (!gate.Allowed) return Refused(gate.Refusal, gate.Message);

            BackendWriteResult result = await Backend.RestoreAsync(original, cancellationToken).ConfigureAwait(false);
            return Verify(result, state => ReadBackVerifier.CompareRestore(expected, state));
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>Captures the original values before the first write on this device. Null when it is safe to write.</summary>
    private ChargeActionResult? EnsureOriginals()
    {
        if (_originals.Get(Backend.Id, out bool unreadable) is not null) return null;
        if (unreadable)
            return new(ChargeActionStatus.OriginalsUnavailable, "The saved original values cannot be read, so nothing was changed.");

        BackendOriginalState captured;
        try { captured = Backend.CaptureOriginal(); }
        catch (Exception ex) { return new(ChargeActionStatus.OriginalsUnavailable, $"The current firmware values could not be read ({ex.Message}), so nothing was changed."); }

        OriginalCaptureResult saved = _originals.SaveIfAbsent(captured, o => Backend.ExpectedStateAfterRestore(o) is not null);
        return saved switch
        {
            OriginalCaptureResult.Saved or OriginalCaptureResult.AlreadyCaptured => null,
            OriginalCaptureResult.Incomplete => new(ChargeActionStatus.OriginalsUnavailable,
                "The current firmware values could not be read, so they could not be saved for Restore laptop defaults. Nothing was changed."),
            _ => new(ChargeActionStatus.OriginalsUnavailable, "The original values could not be saved, so nothing was changed."),
        };
    }

    /// <summary>Maps a backend result and re-reads the state in the app, whatever the backend reported.</summary>
    private ChargeActionResult Verify(BackendWriteResult result, Func<ChargeLimitState, ReadBackCheck> compare)
    {
        if (result.Refusal != WriteRefusal.None) return Refused(result.Refusal, result.Message);
        if (result.Cancelled) return new(ChargeActionStatus.Cancelled, result.Message);
        if (result.NeedsElevation) return new(ChargeActionStatus.NeedsElevation, result.Message);

        ChargeLimitState state = Backend.GetState();
        if (!result.Success)
        {
            return result.ReadBackCheck == ReadBackCheck.Mismatch
                ? new(ChargeActionStatus.ReadBackMismatch, result.Message, state)
                : new(ChargeActionStatus.Failed, result.Message, state);
        }

        return compare(state) switch
        {
            ReadBackCheck.Mismatch => new(ChargeActionStatus.ReadBackMismatch,
                $"The firmware reports a different value after the change (read back {state.RawValue ?? state.Limit?.ToString() ?? "nothing"}).", state),
            ReadBackCheck.Unreadable => new(ChargeActionStatus.AppliedNotConfirmed,
                "The change was made and checked by the administrator helper, but the app could not read it back itself.", result.ReadBack ?? state),
            _ => new(result.Message.StartsWith("Already", StringComparison.Ordinal) ? ChargeActionStatus.AlreadySet : ChargeActionStatus.Applied,
                result.Message, state),
        };
    }

    private static ChargeActionResult Busy() => new(ChargeActionStatus.Busy, "Another change is still running.");

    private static ChargeActionResult Refused(WriteRefusal refusal, string message) => new(ChargeActionStatus.Refused, message, null, refusal);
}
