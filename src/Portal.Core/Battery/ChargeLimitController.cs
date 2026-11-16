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
    /// <summary>The app's intended-state file could not be read or written, so nothing was written.</summary>
    StateFileUnavailable,
    NothingToRestore,
    /// <summary>The firmware shows a value the app did not set; the user decides.</summary>
    ChangedElsewhere,
    NothingToDo,
    Failed,
    /// <summary>The unusable intent file was renamed aside; the app starts from a clean state.</summary>
    StateReset,
}

/// <summary>How the saved intent file stands for this backend.</summary>
public enum IntentFileState
{
    Ok,
    /// <summary>The file cannot be read, or its values do not make sense for this backend.</summary>
    Unusable,
}

public enum ReapplyReason
{
    /// <summary>App start. Also the crash recovery: an unfinished top-up or a pending re-apply is put right here.</summary>
    Startup,
    Resume,
    Exit,
}

public sealed record ChargeActionResult(ChargeActionStatus Status, string Message, ChargeLimitState? State = null,
    WriteRefusal Refusal = WriteRefusal.None)
{
    public bool Succeeded => Status is ChargeActionStatus.Applied or ChargeActionStatus.AlreadySet or ChargeActionStatus.AppliedNotConfirmed;

    /// <summary>The action stopped before any firmware write was attempted.</summary>
    public bool NotAttempted { get; init; }
}

/// <summary>
/// The app's single writer for the charge limit. Allows one write at a time, saves the original firmware values before
/// the first write, saves the intended state before every change, and checks every write by reading the state back itself.
/// Unattended re-applies (start, wake, quit) never prompt for elevation, never turn a limit off, never change a limit
/// another tool set and never retry a write that was attempted and failed; they only put the app's own steady-state limit
/// back after a top-up or an earlier re-apply that could not run.
/// </summary>
public sealed class ChargeLimitController
{
    public static readonly TimeSpan DefaultExitBudget = TimeSpan.FromSeconds(3);

    private readonly ChargeWriteLock _writeLock;
    private readonly OriginalStateStore _originals;
    private readonly ChargeIntentStore _intents;
    private readonly TimeProvider _time;

    public ChargeLimitController(IChargeLimitBackend backend, OriginalStateStore originals, ChargeIntentStore intents, TimeProvider? time = null,
        ChargeWriteLock? writeLock = null)
    {
        Backend = backend;
        _writeLock = writeLock ?? ChargeWriteLock.Process;
        _originals = originals;
        _intents = intents;
        _time = time ?? TimeProvider.System;
    }

    public IChargeLimitBackend Backend { get; }

    public bool IsBusy => _writeLock.IsHeld;

    public ChargeLimitCapabilities Capabilities => Backend.Capabilities;

    /// <summary>Read-only.</summary>
    public ChargeLimitState ReadState() => Backend.GetState();

    /// <summary>The write gate as it stands now. Read-only.</summary>
    public WriteGateResult EvaluateGate() => Backend is ChargeLimitBackendBase b
        ? b.EvaluateGate()
        : new WriteGateResult(WriteGateDecision.Simulated, WriteRefusal.None, "Simulated backend.");

    public bool HasSavedOriginals => _originals.Get(Backend.Id, out _) is not null;

    /// <summary>The saved intent for this backend, or null (also when the file is unusable; see <see cref="IntentState"/>).</summary>
    public ChargeIntent? LoadIntent() => LoadChecked(out ChargeIntent? intent) == IntentFileState.Ok ? intent : null;

    /// <summary>Whether the intent file can be used. Read-only.</summary>
    public IntentFileState IntentState => LoadChecked(out _);

    /// <summary>
    /// A managed intent must say what it means: "on" is a limit below 100 that this backend supports, "off" is 100.
    /// Anything else (a damaged or hand-edited file) is never acted on.
    /// </summary>
    public bool IsConsistent(ChargeIntent intent) =>
        !intent.Managed || (intent.LimitOn
            ? intent.Limit < 100 && Capabilities.IsValidLimit(intent.Limit)
            : intent.Limit == 100);

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
            return new(ChargeActionStatus.Failed, "This laptop does not support a charge limit.") { NotAttempted = true };
        if (!_writeLock.TryEnter()) return Busy();
        try
        {
            WriteGateResult gate = EvaluateGate();
            if (!gate.Allowed) return Refused(gate.Refusal, gate.Message);
            if (!TryLoadIntent(out ChargeIntent? previous)) return StateFileUnavailable();
            if (EnsureOriginals() is { } failed) return failed;

            var next = new ChargeIntent { BackendId = Backend.Id, Managed = true, LimitOn = target < 100, Limit = target, UpdatedUtc = _time.GetUtcNow() };
            if (!_intents.TrySave(next)) return StateFileUnavailable();

            ChargeActionResult result = await WriteLimitAsync(target, allowPrompt: true, cancellationToken).ConfigureAwait(false);
            if (!WriteMayHaveHappened(result)) RevertIntent(previous);
            return result;
        }
        finally { _writeLock.Exit(); }
    }

    /// <summary>
    /// Turns the limit off for a while. The intent (limit on, top-up active) is saved before the write, so a quit, crash
    /// or sleep during the top-up puts the limit back.
    /// </summary>
    public async Task<ChargeActionResult> StartTopUpAsync(TimeSpan duration, bool allowPrompt = true, CancellationToken cancellationToken = default)
    {
        if (!_writeLock.TryEnter()) return Busy();
        try
        {
            if (!TryLoadIntent(out ChargeIntent? previous)) return StateFileUnavailable();
            if (previous is not { Managed: true, LimitOn: true }) return new(ChargeActionStatus.NothingToDo, "The charge limit is not on, so there is nothing to top up.");
            WriteGateResult gate = EvaluateGate();
            if (!gate.Allowed) return Refused(gate.Refusal, gate.Message);
            if (EnsureOriginals() is { } failed) return failed;

            var next = previous with { TopUpActive = true, TopUpUntil = _time.GetUtcNow() + duration, UpdatedUtc = _time.GetUtcNow() };
            if (!_intents.TrySave(next)) return StateFileUnavailable();

            ChargeActionResult result = await WriteLimitAsync(100, allowPrompt, cancellationToken).ConfigureAwait(false);
            if (!WriteMayHaveHappened(result)) RevertIntent(previous);
            return result;
        }
        finally { _writeLock.Exit(); }
    }

    /// <summary>Ends a top-up by putting the steady-state limit back.</summary>
    public async Task<ChargeActionResult> EndTopUpAsync(bool allowPrompt = true, CancellationToken cancellationToken = default)
    {
        if (!_writeLock.TryEnter()) return Busy();
        try
        {
            if (!TryLoadIntent(out ChargeIntent? intent)) return StateFileUnavailable();
            if (intent is not { Managed: true, LimitOn: true, TopUpActive: true }) return new(ChargeActionStatus.NothingToDo, "No top-up is running.");
            return await PutSteadyLimitBackAsync(intent, allowPrompt, cancellationToken).ConfigureAwait(false);
        }
        finally { _writeLock.Exit(); }
    }

    /// <summary>
    /// User action: puts the saved original firmware values back and reads them back. May prompt for elevation.
    /// An unusable intent file does not block it (the restore ends with the app not managing the limit, which is safe);
    /// that file is renamed aside first, never overwritten.
    /// </summary>
    public async Task<ChargeActionResult> RestoreDefaultsAsync(CancellationToken cancellationToken = default)
    {
        if (!_writeLock.TryEnter()) return Busy();
        try
        {
            BackendOriginalState? original = _originals.Get(Backend.Id, out bool unreadable);
            if (unreadable)
                return new(ChargeActionStatus.OriginalsUnavailable, "The saved original values cannot be read, so nothing was changed.") { NotAttempted = true };
            if (original is null)
                return new(ChargeActionStatus.NothingToRestore, "The app has not changed this laptop's charge settings, so there is nothing to restore.") { NotAttempted = true };
            if (Backend.ExpectedStateAfterRestore(original) is not { } expected)
                return new(ChargeActionStatus.OriginalsUnavailable, "The saved original values are incomplete, so nothing was changed.") { NotAttempted = true };

            WriteGateResult gate = EvaluateGate();
            if (!gate.Allowed) return Refused(gate.Refusal, gate.Message);

            bool intentFileUsable = true;
            if (LoadChecked(out ChargeIntent? previous) == IntentFileState.Unusable)
            {
                try { _intents.QuarantineIfUnusable(IsUsable, _time.GetUtcNow()); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { intentFileUsable = false; }
                previous = null;
            }

            // Saved first so a crash during the restore never re-applies the app's limit afterwards. When the unusable
            // file could not be renamed it is left as it is; it already stops every unattended re-apply.
            if (intentFileUsable && !_intents.TrySave(new ChargeIntent { BackendId = Backend.Id, Managed = false, UpdatedUtc = _time.GetUtcNow() }))
                return StateFileUnavailable();

            BackendWriteResult write = await Backend.RestoreAsync(original, cancellationToken).ConfigureAwait(false);
            ChargeActionResult result = Verify(write, state => ReadBackVerifier.CompareRestore(expected, state));
            if (intentFileUsable && !WriteMayHaveHappened(result)) RevertIntent(previous);
            return result;
        }
        finally { _writeLock.Exit(); }
    }

    /// <summary>
    /// User action: renames an unusable intent file to a unique "charge-intent.json.corrupt-&lt;time&gt;" so the app can
    /// start clean. Never deletes or overwrites anything and never touches the firmware.
    /// </summary>
    public ChargeActionResult ResetAppState()
    {
        if (!_writeLock.TryEnter()) return Busy();
        try
        {
            string? moved;
            try { moved = _intents.QuarantineIfUnusable(IsUsable, _time.GetUtcNow()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new(ChargeActionStatus.Failed, $"The app's state file could not be renamed ({ex.Message}), so nothing was changed.") { NotAttempted = true };
            }
            return moved is null
                ? new(ChargeActionStatus.NothingToDo, "The app's charge-limit state is fine, so nothing was changed.") { NotAttempted = true }
                : new(ChargeActionStatus.StateReset,
                    $"The unreadable state file was kept as {Path.GetFileName(moved)}. The app does not manage the charge limit until you turn it on again.")
                    { NotAttempted = true };
        }
        finally { _writeLock.Exit(); }
    }

    /// <summary>
    /// Unattended: never prompts. Puts the app's steady-state limit back only after a top-up (quit, crash, expiry) or an
    /// earlier re-apply that could not run. Never turns a limit off, never overrides a limit another tool set or turned off
    /// (reports <see cref="ChargeActionStatus.ChangedElsewhere"/> instead) and never retries a re-apply that was attempted and failed.
    /// </summary>
    public async Task<ChargeActionResult> ReapplyAsync(ReapplyReason reason, CancellationToken cancellationToken = default)
    {
        if (!_writeLock.TryEnter())
        {
            if (reason == ReapplyReason.Exit) MarkNeedsReapply();
            return Busy();
        }
        try
        {
            if (!TryLoadIntent(out ChargeIntent? intent)) return StateFileUnavailable();
            if (intent is not { Managed: true }) return new(ChargeActionStatus.NothingToDo, "The app does not manage the charge limit.");
            if (!intent.LimitOn)
            {
                if (HasPendingFlags(intent)) _intents.TrySave(Settled(intent));
                return new(ChargeActionStatus.NothingToDo, "The charge limit is meant to be off.");
            }

            bool topUpRunning = intent.TopUpActive && intent.TopUpUntil is DateTimeOffset until && until > _time.GetUtcNow();
            if (reason == ReapplyReason.Resume && topUpRunning)
                return new(ChargeActionStatus.NothingToDo, "A top-up is still running.");

            bool mayWrite = (intent.TopUpActive || intent.NeedsReapply) && !intent.ReapplyFailed;
            ChargeLimitState state = Backend.GetState();
            if (state.Limit is int now)
            {
                if (now == intent.Limit)
                {
                    if (HasPendingFlags(intent)) _intents.TrySave(Settled(intent));
                    return new(ChargeActionStatus.AlreadySet, "The charge limit is in place.", state);
                }
                if (now < 100)
                {
                    if (HasPendingFlags(intent)) _intents.TrySave(Settled(intent));
                    return new(ChargeActionStatus.ChangedElsewhere, $"The laptop reports a charge limit of {now} %, which the app did not set.", state);
                }
            }
            if (!mayWrite)
            {
                if (intent.ReapplyFailed)
                {
                    return new(ChargeActionStatus.Failed,
                        "An earlier try to put the charge limit back did not work, so the app does not try again by itself.", state) { NotAttempted = true };
                }
                return state.Limit is null
                    ? new(ChargeActionStatus.NothingToDo, state.Message ?? "The current value could not be read.", state)
                    : new(ChargeActionStatus.ChangedElsewhere, "The charge limit is off, but the app had it on.", state);
            }

            return await PutSteadyLimitBackAsync(intent, allowPrompt: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MarkReapplyFailed();
            return new(ChargeActionStatus.Failed, ex.Message);
        }
        finally { _writeLock.Exit(); }
    }

    /// <summary>
    /// For the quit path, on the UI thread: re-applies within <paramref name="budget"/> and never waits longer. When it
    /// cannot finish (timeout, elevation needed, refused) the intent keeps "needs re-apply" so the next start fixes it.
    /// </summary>
    public ChargeActionResult ReapplyOnExit(TimeSpan budget)
    {
        using var cts = new CancellationTokenSource(budget);
        Task<ChargeActionResult> task = Task.Run(() => ReapplyAsync(ReapplyReason.Exit, cts.Token));
        try
        {
            if (task.Wait(budget)) return task.Result;
        }
        catch (AggregateException) { }
        MarkNeedsReapply();
        return new(ChargeActionStatus.Failed, "Putting the charge limit back took too long; the app will finish it at the next start.");
    }

    /// <summary>User choice after <see cref="ChargeActionStatus.ChangedElsewhere"/>: keep what the firmware shows. No firmware write.</summary>
    public ChargeActionResult KeepFirmwareValue()
    {
        if (!_writeLock.TryEnter()) return Busy();
        try
        {
            if (!TryLoadIntent(out _)) return StateFileUnavailable();
            ChargeLimitState state = Backend.GetState();
            if (state.Limit is not int limit) return new(ChargeActionStatus.Failed, state.Message ?? "The current value could not be read.") { NotAttempted = true };
            var intent = new ChargeIntent { BackendId = Backend.Id, Managed = true, LimitOn = limit < 100, Limit = limit, UpdatedUtc = _time.GetUtcNow() };
            if (!IsConsistent(intent))
                return new(ChargeActionStatus.Failed, $"The laptop reports {limit} %, which the app cannot manage, so nothing was saved.", state) { NotAttempted = true };
            return _intents.TrySave(intent) ? new(ChargeActionStatus.Applied, "Kept the laptop's current value.", state) : StateFileUnavailable();
        }
        finally { _writeLock.Exit(); }
    }

    private async Task<ChargeActionResult> PutSteadyLimitBackAsync(ChargeIntent intent, bool allowPrompt, CancellationToken cancellationToken)
    {
        // Last guard before the write: putting the limit back must turn a valid limit on, never off.
        if (!intent.LimitOn || intent.Limit >= 100 || !Capabilities.IsValidLimit(intent.Limit))
            return new(ChargeActionStatus.Failed, "The saved charge limit is not a limit this laptop supports, so nothing was changed.") { NotAttempted = true };

        ChargeActionResult result = await WriteLimitAsync(intent.Limit, allowPrompt, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _time.GetUtcNow();
        ChargeIntent next = result.Succeeded ? Settled(intent)
            : !WriteMayHaveHappened(result) ? intent with { NeedsReapply = true, UpdatedUtc = now }
            : intent with { TopUpActive = false, TopUpUntil = null, NeedsReapply = false, ReapplyFailed = true, UpdatedUtc = now };
        _intents.TrySave(next);
        return result;
    }

    private static bool HasPendingFlags(ChargeIntent intent) => intent.TopUpActive || intent.NeedsReapply || intent.ReapplyFailed;

    private ChargeIntent Settled(ChargeIntent intent) =>
        intent with { TopUpActive = false, TopUpUntil = null, NeedsReapply = false, ReapplyFailed = false, UpdatedUtc = _time.GetUtcNow() };

    private void MarkNeedsReapply()
    {
        if (LoadChecked(out ChargeIntent? intent) == IntentFileState.Ok &&
            intent is { Managed: true, LimitOn: true, NeedsReapply: false, ReapplyFailed: false })
            _intents.TrySave(intent with { NeedsReapply = true, UpdatedUtc = _time.GetUtcNow() });
    }

    private void MarkReapplyFailed()
    {
        if (LoadChecked(out ChargeIntent? intent) == IntentFileState.Ok && intent is { Managed: true, LimitOn: true })
            _intents.TrySave(intent with { TopUpActive = false, TopUpUntil = null, NeedsReapply = false, ReapplyFailed = true, UpdatedUtc = _time.GetUtcNow() });
    }

    private async Task<ChargeActionResult> WriteLimitAsync(int limit, bool allowPrompt, CancellationToken cancellationToken)
    {
        var request = new ChargeLimitRequest { Limit = limit, AllowElevationPrompt = allowPrompt };
        BackendWriteResult result = await Backend.ApplyAsync(request, cancellationToken).ConfigureAwait(false);
        return Verify(result, state => ReadBackVerifier.Compare(request, state));
    }

    /// <summary>False only for outcomes where the firmware certainly was not changed, so the previous intent can stand.</summary>
    private static bool WriteMayHaveHappened(ChargeActionResult result) => !result.NotAttempted && result.Status is not (
        ChargeActionStatus.Refused or ChargeActionStatus.NeedsElevation or ChargeActionStatus.Cancelled or ChargeActionStatus.Busy);

    private bool IsUsable(ChargeIntent intent) => intent.BackendId != Backend.Id || IsConsistent(intent);

    private IntentFileState LoadChecked(out ChargeIntent? intent)
    {
        if (_intents.Load(out intent) == AtomicJsonFile.ReadStatus.Unreadable)
        {
            intent = null;
            return IntentFileState.Unusable;
        }
        if (intent is not null && intent.BackendId != Backend.Id) intent = null;
        if (intent is not null && !IsConsistent(intent))
        {
            intent = null;
            return IntentFileState.Unusable;
        }
        return IntentFileState.Ok;
    }

    private bool TryLoadIntent(out ChargeIntent? intent) => LoadChecked(out intent) == IntentFileState.Ok;

    private void RevertIntent(ChargeIntent? previous) =>
        _intents.TrySave(previous ?? new ChargeIntent { BackendId = Backend.Id, Managed = false, UpdatedUtc = _time.GetUtcNow() });

    /// <summary>Captures the original values before the first write on this device. Null when it is safe to write.</summary>
    private ChargeActionResult? EnsureOriginals()
    {
        if (_originals.Get(Backend.Id, out bool unreadable) is not null) return null;
        if (unreadable)
            return new(ChargeActionStatus.OriginalsUnavailable, "The saved original values cannot be read, so nothing was changed.") { NotAttempted = true };

        BackendOriginalState captured;
        try { captured = Backend.CaptureOriginal(); }
        catch (Exception ex)
        {
            return new(ChargeActionStatus.OriginalsUnavailable, $"The current firmware values could not be read ({ex.Message}), so nothing was changed.") { NotAttempted = true };
        }

        OriginalCaptureResult saved = _originals.SaveIfAbsent(captured, o => Backend.ExpectedStateAfterRestore(o) is not null);
        return saved switch
        {
            OriginalCaptureResult.Saved or OriginalCaptureResult.AlreadyCaptured => null,
            OriginalCaptureResult.Incomplete => new(ChargeActionStatus.OriginalsUnavailable,
                "The current firmware values could not be read, so they could not be saved for Restore laptop defaults. Nothing was changed.") { NotAttempted = true },
            _ => new(ChargeActionStatus.OriginalsUnavailable, "The original values could not be saved, so nothing was changed.") { NotAttempted = true },
        };
    }

    /// <summary>Maps a backend result and re-reads the state in the app, whatever the backend reported.</summary>
    private ChargeActionResult Verify(BackendWriteResult result, Func<ChargeLimitState, ReadBackCheck> compare)
    {
        if (result.Refusal != WriteRefusal.None) return Refused(result.Refusal, result.Message);
        if (result.Cancelled) return new(ChargeActionStatus.Cancelled, result.Message) { NotAttempted = result.NotAttempted };
        if (result.NeedsElevation) return new(ChargeActionStatus.NeedsElevation, result.Message) { NotAttempted = true };

        ChargeLimitState state = Backend.GetState();
        if (!result.Success)
        {
            return result.ReadBackCheck == ReadBackCheck.Mismatch
                ? new(ChargeActionStatus.ReadBackMismatch, result.Message, state)
                : new(ChargeActionStatus.Failed, result.Message, state) { NotAttempted = result.NotAttempted };
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

    private static ChargeActionResult Busy() => new(ChargeActionStatus.Busy, "Another change is still running.") { NotAttempted = true };

    private static ChargeActionResult StateFileUnavailable() =>
        new(ChargeActionStatus.StateFileUnavailable, "The app could not read or save its charge-limit state file, so nothing was changed.") { NotAttempted = true };

    private static ChargeActionResult Refused(WriteRefusal refusal, string message) =>
        new(ChargeActionStatus.Refused, message, null, refusal) { NotAttempted = true };
}
