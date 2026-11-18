using Portal.Core.Battery;

namespace Portal.Core.Services;

/// <summary>When crash reports may be sent: only with the user's opt-in, and never in tests, harness runs or fake-battery mode.</summary>
public static class CrashReportPolicy
{
    public const string HarnessVariable = "BCP_HARNESS";
    public const string BlockVariable = "NO_CRASH_REPORTS";

    /// <summary>True when the environment forbids reporting whatever the setting says.</summary>
    public static bool IsBlocked(Func<string, string?> getVariable, bool fakeBattery) =>
        fakeBattery || getVariable(HarnessVariable) is { Length: > 0 } || getVariable(BlockVariable) == "1";

    /// <summary>True when the SDK may run: a DSN is compiled in, the user opted in and nothing blocks it.</summary>
    public static bool MayReport(string? dsn, bool userOptedIn, bool blocked) =>
        userOptedIn && !blocked && !string.IsNullOrWhiteSpace(dsn);

    /// <summary>store, beta or dev (anything else is dev).</summary>
    public static string NormalizeChannel(string? channel) =>
        channel?.Trim().ToLowerInvariant() is ("store" or "beta" or "dev") and string known ? known : "dev";
}
