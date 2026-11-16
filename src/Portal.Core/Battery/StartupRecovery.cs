namespace Portal.Core.Battery;

/// <summary>
/// Crash and logon recovery rules. At logon the firmware interface or driver may not be ready for the first device
/// check; when the saved intent shows an unfinished top-up or a pending re-apply, the check is tried again a few times.
/// </summary>
public static class StartupRecovery
{
    public static IReadOnlyList<TimeSpan> RetryDelays { get; } =
        new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5) };

    /// <summary>True when the saved intent still needs the limit put back automatically. Read-only.</summary>
    public static bool ShouldRetryCheck(ChargeIntentStore intents) =>
        intents.Load(out ChargeIntent? intent) == AtomicJsonFile.ReadStatus.Ok &&
        intent is { Managed: true, LimitOn: true, ReapplyFailed: false } &&
        (intent.TopUpActive || intent.NeedsReapply);

    /// <summary>
    /// Unattended re-apply outcomes that leave the limit off until the user acts. The app tells the user with a
    /// notification that opens the Charging page; it never raises an administrator prompt by itself.
    /// </summary>
    public static bool NeedsUserNotice(ChargeActionResult result) =>
        result.Status is ChargeActionStatus.NeedsElevation or ChargeActionStatus.Failed or ChargeActionStatus.ReadBackMismatch;
}
