namespace Portal.Core.Battery.Backends;

/// <summary>Shared validation, write gating (<see cref="WriteGate"/>), plan execution and read-back for hardware backends.</summary>
public abstract class ChargeLimitBackendBase : IChargeLimitBackend
{
    private bool _interfaceFound;

    protected ChargeLimitBackendBase(IWmiReader wmi, IBatteryWriteExecutor executor, TimeProvider? time)
    {
        Wmi = wmi;
        Executor = executor;
        Time = time ?? TimeProvider.System;
    }

    protected IWmiReader Wmi { get; }
    protected IBatteryWriteExecutor Executor { get; }
    protected TimeProvider Time { get; }

    /// <summary>Beta opt-in and other-owner inputs for the write gate. The default refuses beta.</summary>
    public WriteGateContext GateContext { get; init; } = WriteGateContext.Default;

    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract BackendConfidence Confidence { get; }
    public virtual bool RequiresElevation => true;
    public ChargeLimitCapabilities Capabilities { get; protected set; } = ChargeLimitCapabilities.Unsupported;

    /// <summary>False when the firmware cannot report the current value; such a backend never writes.</summary>
    public virtual bool FirmwareReportsState => true;

    /// <summary>
    /// Read-only. True when the interface exists and the write gate would allow a change (verified model, or the
    /// user's beta opt-in on a backend whose writes can be read back). Another owner does not hide the backend.
    /// </summary>
    public bool Detect()
    {
        if (!ProbeInterface()) return false;
        return WriteGate.Evaluate(GateInput(planHasReadBack: true, includeOwner: false)).Allowed;
    }

    /// <summary>True when this device is on the verified-models list for this backend.</summary>
    public bool IsVerifiedDevice()
    {
        try { return VerifiedModels.IsVerified(Id, DeviceIdentity.Read(Wmi)); }
        catch (Exception) { return false; }
    }

    /// <summary>Read-only probe that ignores the write gate. For the device check and the diagnostics report.</summary>
    public bool ProbeForDiagnostics() => ProbeInterface();

    private bool ProbeInterface()
    {
        try { _interfaceFound = DetectCore(); }
        catch (Exception) { _interfaceFound = false; }
        return _interfaceFound;
    }

    /// <summary>The gate decision as it stands now, before a plan exists. For the UI.</summary>
    public WriteGateResult EvaluateGate() => WriteGate.Evaluate(GateInput(planHasReadBack: true, includeOwner: true));

    private WriteGateInput GateInput(bool planHasReadBack, bool includeOwner)
    {
        OtherOwnerClaim? owner = null;
        if (includeOwner)
        {
            try { owner = GateContext.OtherOwner(); }
            catch (Exception) { owner = new OtherOwnerClaim("Another app", Confirmed: false); }
        }
        bool beta;
        try { beta = GateContext.BetaOptIn(); }
        catch (Exception) { beta = false; }
        return new WriteGateInput(Confidence, Confidence != BackendConfidence.Simulated && IsVerifiedDevice(), beta,
            _interfaceFound, FirmwareReportsState, planHasReadBack, owner?.Name, owner?.Confirmed ?? true);
    }

    private static bool HasReadBackStep(BatteryWritePlan plan) =>
        plan.Steps.Any(s => s.Kind is BatteryWriteStepKind.VerifyDeviceIoControlBit or BatteryWriteStepKind.VerifyWmiProperty);

    /// <summary>Read-only probe; must also fill <see cref="Capabilities"/>.</summary>
    protected abstract bool DetectCore();

    public ChargeLimitState GetState()
    {
        try { return GetStateCore(); }
        catch (Exception ex) { return new ChargeLimitState { Message = $"Could not read the current state: {ex.Message}" }; }
    }

    protected abstract ChargeLimitState GetStateCore();

    public abstract BackendOriginalState CaptureOriginal();

    /// <summary>The state a restore of <paramref name="original"/> must read back, or null when the original is unusable.</summary>
    public ChargeLimitState? ExpectedStateAfterRestore(BackendOriginalState original)
    {
        if (original.BackendId != Id) return null;
        try { return ExpectedAfterRestoreCore(original) is { Limit: not null } expected ? expected : null; }
        catch (Exception) { return null; }
    }

    protected abstract ChargeLimitState? ExpectedAfterRestoreCore(BackendOriginalState original);

    public async Task<BackendWriteResult> ApplyAsync(ChargeLimitRequest request, CancellationToken cancellationToken = default)
    {
        WriteGateResult gate = WriteGate.Evaluate(GateInput(planHasReadBack: true, includeOwner: true));
        if (!gate.Allowed) return BackendWriteResult.Refused(gate);

        ChargeLimitState current = GetState();
        string? error = ChargeLimitRequestValidator.Validate(request, Capabilities, current.Limit);
        if (error is not null) return BackendWriteResult.Fail(error);
        if (current.Message is not null && current.Limit is null && !AllowWriteWithoutReadBack)
            return BackendWriteResult.Fail(current.Message);

        BatteryWritePlan? plan;
        try { plan = BuildPlan(request, current, out error); }
        catch (Exception ex) { return BackendWriteResult.Fail(ex.Message); }
        if (plan is null)
        {
            return error is null
                ? BackendWriteResult.Ok("Already set.", current) with { ReadBackCheck = ReadBackVerifier.Compare(request, current) }
                : BackendWriteResult.Fail(error);
        }

        gate = WriteGate.Evaluate(GateInput(HasReadBackStep(plan), includeOwner: false));
        if (!gate.Allowed) return BackendWriteResult.Refused(gate);

        BackendWriteResult result = await RunPlanAsync(plan, request.AllowElevationPrompt, cancellationToken).ConfigureAwait(false);
        return VerifyAfterWrite(result, state => ReadBackVerifier.Compare(request, state));
    }

    public Task<BackendWriteResult> SetLimitAsync(int limit, CancellationToken cancellationToken = default) =>
        ApplyAsync(new ChargeLimitRequest { Limit = limit }, cancellationToken);

    public Task<BackendWriteResult> SetStartThresholdAsync(int? threshold, CancellationToken cancellationToken = default) =>
        ApplyAsync(threshold is int t ? new ChargeLimitRequest { StartThreshold = t } : new ChargeLimitRequest { ResetStartThreshold = true }, cancellationToken);

    public Task<BackendWriteResult> SetChargingPausedAsync(bool paused, CancellationToken cancellationToken = default) =>
        ApplyAsync(new ChargeLimitRequest { ChargingPaused = paused }, cancellationToken);

    public Task<BackendWriteResult> RestoreAsync(BackendOriginalState original, CancellationToken cancellationToken = default) =>
        RestoreAsync(original, allowElevationPrompt: true, cancellationToken);

    public async Task<BackendWriteResult> RestoreAsync(BackendOriginalState original, bool allowElevationPrompt, CancellationToken cancellationToken = default)
    {
        if (original.BackendId != Id) return BackendWriteResult.Fail("The saved original values belong to a different backend.");
        WriteGateResult gate = WriteGate.Evaluate(GateInput(planHasReadBack: true, includeOwner: true));
        if (!gate.Allowed) return BackendWriteResult.Refused(gate);
        if (ExpectedStateAfterRestore(original) is not { } expected)
            return BackendWriteResult.Fail("The saved original values are incomplete, so nothing was changed.");

        BatteryWritePlan? plan;
        string? error;
        ChargeLimitState current = GetState();
        try { plan = BuildRestorePlan(original, current, out error); }
        catch (Exception ex) { return BackendWriteResult.Fail(ex.Message); }
        if (plan is null)
        {
            return error is null
                ? BackendWriteResult.Ok("Already at the original values.", current) with { ReadBackCheck = ReadBackVerifier.CompareRestore(expected, current) }
                : BackendWriteResult.Fail(error);
        }

        gate = WriteGate.Evaluate(GateInput(HasReadBackStep(plan), includeOwner: false));
        if (!gate.Allowed) return BackendWriteResult.Refused(gate);

        BackendWriteResult result = await RunPlanAsync(plan, allowElevationPrompt, cancellationToken).ConfigureAwait(false);
        return VerifyAfterWrite(result, state => ReadBackVerifier.CompareRestore(expected, state));
    }

    /// <summary>After a successful plan run the app reads the state itself; a mismatch turns the result into a failure.</summary>
    private BackendWriteResult VerifyAfterWrite(BackendWriteResult result, Func<ChargeLimitState, ReadBackCheck> compare)
    {
        if (!result.Success) return result;
        ChargeLimitState after = GetState();
        ReadBackCheck check = compare(after);
        return check switch
        {
            ReadBackCheck.Mismatch => result with
            {
                Success = false,
                ReadBack = after,
                ReadBackCheck = check,
                Message = $"The firmware reports a different value after the change (read back {after.RawValue ?? after.Limit?.ToString() ?? "nothing"}).",
            },
            ReadBackCheck.Unreadable => result with { ReadBack = result.ReadBack ?? after, ReadBackCheck = check },
            _ => result with { ReadBack = after, ReadBackCheck = check },
        };
    }

    /// <summary>True for backends whose current value may be unreadable before a write (the plan then reads back itself).</summary>
    protected virtual bool AllowWriteWithoutReadBack => false;

    /// <summary>Returns null with a null error when nothing needs to change, or null with an error to refuse.</summary>
    protected abstract BatteryWritePlan? BuildPlan(ChargeLimitRequest request, ChargeLimitState current, out string? error);

    protected abstract BatteryWritePlan? BuildRestorePlan(BackendOriginalState original, ChargeLimitState current, out string? error);

    protected virtual async Task<BackendWriteResult> RunPlanAsync(BatteryWritePlan plan, bool allowPrompt, CancellationToken cancellationToken)
    {
        if (!HasReadBackStep(plan))
            return BackendWriteResult.Fail("This change cannot be read back from the firmware, so it was not made.") with { Refusal = WriteRefusal.NoReadBack };
        if (!allowPrompt) return BackendWriteResult.ElevationNeeded();
        BatteryWritePlanResult result = await Executor.ExecuteAsync(plan, cancellationToken).ConfigureAwait(false);
        if (result.Cancelled) return BackendWriteResult.UserCancelled();
        ChargeLimitState readBack = GetState();
        if (!result.Ok)
        {
            bool verifyFailed = result.FailedStep >= 0 && result.FailedStep < plan.Steps.Count &&
                plan.Steps[result.FailedStep].Kind is BatteryWriteStepKind.VerifyDeviceIoControlBit or BatteryWriteStepKind.VerifyWmiProperty;
            return new BackendWriteResult
            {
                Success = false,
                Message = result.Message ?? "The firmware rejected the change.",
                ReadBack = readBack,
                ReadBackCheck = verifyFailed ? ReadBackCheck.Mismatch : ReadBackCheck.NotChecked,
            };
        }
        return BackendWriteResult.Ok("Applied.", readBack);
    }

    protected BackendOriginalState NewOriginal(Dictionary<string, string> values) =>
        new() { BackendId = Id, CapturedUtc = Time.GetUtcNow(), Values = values };
}
