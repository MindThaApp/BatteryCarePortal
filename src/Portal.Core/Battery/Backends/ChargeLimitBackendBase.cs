namespace Portal.Core.Battery.Backends;

/// <summary>Shared validation, verified-models gating and plan execution for hardware backends.</summary>
public abstract class ChargeLimitBackendBase : IChargeLimitBackend
{
    protected ChargeLimitBackendBase(IWmiReader wmi, IBatteryWriteExecutor executor, TimeProvider? time)
    {
        Wmi = wmi;
        Executor = executor;
        Time = time ?? TimeProvider.System;
    }

    protected IWmiReader Wmi { get; }
    protected IBatteryWriteExecutor Executor { get; }
    protected TimeProvider Time { get; }

    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract BackendConfidence Confidence { get; }
    public virtual bool RequiresElevation => true;
    public ChargeLimitCapabilities Capabilities { get; protected set; } = ChargeLimitCapabilities.Unsupported;

    public bool Detect()
    {
        if (Confidence != BackendConfidence.Simulated && !IsVerifiedDevice()) return false;
        try { return DetectCore(); }
        catch (Exception) { return false; }
    }

    /// <summary>True when this device is on the verified-models list for this backend. Writes are refused otherwise.</summary>
    public bool IsVerifiedDevice()
    {
        try { return VerifiedModels.IsVerified(Id, DeviceIdentity.Read(Wmi)); }
        catch (Exception) { return false; }
    }

    private BackendWriteResult? RefuseUnverified() => IsVerifiedDevice()
        ? null
        : BackendWriteResult.Fail("This laptop model is not on the verified list for this battery feature, so nothing was changed.");

    /// <summary>Read-only probe that ignores the verified-models gate. For the diagnostics report only.</summary>
    public bool ProbeForDiagnostics()
    {
        try { return DetectCore(); }
        catch (Exception) { return false; }
    }

    /// <summary>Read-only probe; must also fill <see cref="Capabilities"/>.</summary>
    protected abstract bool DetectCore();

    public ChargeLimitState GetState()
    {
        try { return GetStateCore(); }
        catch (Exception ex) { return new ChargeLimitState { Message = $"Could not read the current state: {ex.Message}" }; }
    }

    protected abstract ChargeLimitState GetStateCore();

    public abstract BackendOriginalState CaptureOriginal();

    public async Task<BackendWriteResult> ApplyAsync(ChargeLimitRequest request, CancellationToken cancellationToken = default)
    {
        if (RefuseUnverified() is { } refused) return refused;
        ChargeLimitState current = GetState();
        string? error = ChargeLimitRequestValidator.Validate(request, Capabilities, current.Limit);
        if (error is not null) return BackendWriteResult.Fail(error);
        if (current.Message is not null && current.Limit is null && !AllowWriteWithoutReadBack)
            return BackendWriteResult.Fail(current.Message);

        BatteryWritePlan? plan;
        try { plan = BuildPlan(request, current, out error); }
        catch (Exception ex) { return BackendWriteResult.Fail(ex.Message); }
        if (plan is null) return error is null ? BackendWriteResult.Ok("Already set.", current) : BackendWriteResult.Fail(error);
        return await RunPlanAsync(plan, request.AllowElevationPrompt, cancellationToken).ConfigureAwait(false);
    }

    public Task<BackendWriteResult> SetLimitAsync(int limit, CancellationToken cancellationToken = default) =>
        ApplyAsync(new ChargeLimitRequest { Limit = limit }, cancellationToken);

    public Task<BackendWriteResult> SetStartThresholdAsync(int? threshold, CancellationToken cancellationToken = default) =>
        ApplyAsync(threshold is int t ? new ChargeLimitRequest { StartThreshold = t } : new ChargeLimitRequest { ResetStartThreshold = true }, cancellationToken);

    public Task<BackendWriteResult> SetChargingPausedAsync(bool paused, CancellationToken cancellationToken = default) =>
        ApplyAsync(new ChargeLimitRequest { ChargingPaused = paused }, cancellationToken);

    public async Task<BackendWriteResult> RestoreAsync(BackendOriginalState original, CancellationToken cancellationToken = default)
    {
        if (original.BackendId != Id) return BackendWriteResult.Fail("The saved original values belong to a different backend.");
        if (RefuseUnverified() is { } refused) return refused;
        BatteryWritePlan? plan;
        string? error;
        try { plan = BuildRestorePlan(original, GetState(), out error); }
        catch (Exception ex) { return BackendWriteResult.Fail(ex.Message); }
        if (plan is null) return error is null ? BackendWriteResult.Ok("Already at the original values.") : BackendWriteResult.Fail(error);
        return await RunPlanAsync(plan, allowPrompt: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>True for backends whose firmware cannot report the current value (the write is then not verifiable).</summary>
    protected virtual bool AllowWriteWithoutReadBack => false;

    /// <summary>Returns null with a null error when nothing needs to change, or null with an error to refuse.</summary>
    protected abstract BatteryWritePlan? BuildPlan(ChargeLimitRequest request, ChargeLimitState current, out string? error);

    protected abstract BatteryWritePlan? BuildRestorePlan(BackendOriginalState original, ChargeLimitState current, out string? error);

    protected virtual async Task<BackendWriteResult> RunPlanAsync(BatteryWritePlan plan, bool allowPrompt, CancellationToken cancellationToken)
    {
        if (!plan.Steps.Any(s => s.Kind is BatteryWriteStepKind.VerifyDeviceIoControlBit or BatteryWriteStepKind.VerifyWmiProperty))
            return BackendWriteResult.Fail("This change cannot be read back from the firmware, so it was not made.");
        if (!allowPrompt) return BackendWriteResult.ElevationNeeded();
        BatteryWritePlanResult result = await Executor.ExecuteAsync(plan, cancellationToken).ConfigureAwait(false);
        if (result.Cancelled) return BackendWriteResult.UserCancelled();
        ChargeLimitState readBack = GetState();
        if (!result.Ok)
        {
            return new BackendWriteResult
            {
                Success = false,
                Message = result.Message ?? "The firmware rejected the change.",
                ReadBack = readBack,
            };
        }
        return BackendWriteResult.Ok("Applied.", readBack);
    }

    protected BackendOriginalState NewOriginal(Dictionary<string, string> values) =>
        new() { BackendId = Id, CapturedUtc = Time.GetUtcNow(), Values = values };
}
