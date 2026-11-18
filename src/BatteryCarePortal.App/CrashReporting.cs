using Portal.Core.Battery;
using Portal.Core.Services;
using Sentry;
using Sentry.Protocol;

namespace BatteryCarePortal;

/// <summary>
/// Crash reports through Sentry (EU region), behind the user's opt-in. The SDK is started only while the setting is on, so an
/// exception raised before consent (or with the setting off) is never kept and never sent later. Every event passes
/// <see cref="BeforeSend"/>: paths, the user name and the PC name are replaced and the server name, user details and request data are dropped.
/// No tracing, no profiling, no user feedback, no battery serials or history. Never started in tests, harness runs, fake-battery
/// mode, or when no DSN was compiled in (<c>secrets\sentry-dsn.txt</c> at build time).
/// </summary>
internal static class CrashReporting
{
    private static readonly object Gate = new();
    private static IDisposable? _sdk;
    private static CrashReportRedactor? _redactor;

    public static bool IsRunning => _sdk is not null;

#if DEBUG
    // Debug builds only: BCP_CRASH_TEST=1 forces consent on and the environment to dev-test (for the single verification event).
    internal static bool TestOverride => Environment.GetEnvironmentVariable("BCP_CRASH_TEST") == "1";
#else
    internal static bool TestOverride => false;
#endif

    private static bool Blocked =>
        !TestOverride && CrashReportPolicy.IsBlocked(Environment.GetEnvironmentVariable, BatteryEnvironment.IsFakeBatteryEnabled);

    /// <summary>Starts or stops the SDK to match the setting. Safe to call again; call after <see cref="AppServices"/> has read the fake-battery flag.</summary>
    public static void Apply(bool userOptedIn)
    {
        lock (Gate)
        {
            bool run = CrashReportPolicy.MayReport(CrashReportConfig.Dsn, userOptedIn || TestOverride, Blocked);
            if (run == (_sdk is not null)) return;
            if (!run)
            {
                Stop();
                return;
            }

            try { _sdk = Start(); }
            catch (Exception) { _sdk = null; }
        }
    }

    // Kept apart so that a run that never reports never loads the SDK.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static IDisposable Start()
    {
        _redactor = CrashReportRedactor.ForThisPc(AppContext.BaseDirectory);
        return SentrySdk.Init(Configure);
    }

    private static void Configure(SentryOptions options)
    {
        options.Dsn = CrashReportConfig.Dsn;
        options.Debug = false;
        options.SendDefaultPii = false;
        options.IsEnvironmentUser = false;
        options.IsGlobalModeEnabled = true;
        options.AutoSessionTracking = false;
        options.Release = AppVersion();
        options.Environment = TestOverride ? "dev-test" : CrashReportPolicy.NormalizeChannel(CrashReportConfig.Channel);
        options.AttachStacktrace = false;
        options.SendClientReports = false;
        options.MaxBreadcrumbs = 30;
        options.TracesSampleRate = 0;
        options.ProfilesSampleRate = 0;
        options.ShutdownTimeout = TimeSpan.FromSeconds(2);
        // Unobserved task exceptions are captured by our own handler, as handled.
        options.DisableUnobservedTaskExceptionCapture();
        options.SetBeforeSend(BeforeSend);
        options.SetBeforeBreadcrumb(RedactBreadcrumb);
        options.SetBeforeSendTransaction(static _ => null);
        options.SetBeforeSendFeedback(static _ => null);
    }

    private static string AppVersion()
    {
        try
        {
            Windows.ApplicationModel.PackageVersion v = Windows.ApplicationModel.Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
        catch (Exception)
        {
            return System.Diagnostics.FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? string.Empty).FileVersion ?? "unknown";
        }
    }

    public static void CaptureCrash(Exception? exception, string source, bool handled)
    {
        if (!IsRunning || exception is null) return;
        try
        {
            exception.Data[Mechanism.HandledKey] = handled;
            exception.Data[Mechanism.MechanismKey] = source;
            SentrySdk.CaptureException(exception, scope => scope.SetTag("source", source));
            // The process ends next: give the event a moment to leave.
            if (!handled) SentrySdk.Flush(TimeSpan.FromSeconds(2));
        }
        catch (Exception) { }
    }

    /// <summary>On quit: lets a pending event leave (bounded) and stops the SDK.</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            if (_sdk is null) return;
            try { SentrySdk.Flush(TimeSpan.FromSeconds(2)); } catch (Exception) { }
            Stop();
        }
    }

    private static void Stop()
    {
        try { _sdk?.Dispose(); } catch (Exception) { }
        _sdk = null;
    }

#if DEBUG
    /// <summary>
    /// <c>--crash-test</c> (Debug builds, before any window or WinRT call): sends one message event, flushes and returns the
    /// exit code. The event id goes to %TEMP%\bcp-crash-test.txt.
    /// </summary>
    internal static int RunCrashTest()
    {
        if (string.IsNullOrWhiteSpace(CrashReportConfig.Dsn)) return 2;
        Environment.SetEnvironmentVariable("BCP_CRASH_TEST", "1");
        Apply(userOptedIn: true);
        if (!IsRunning) return 3;
        SentryId id = SentrySdk.CaptureMessage("Battery Care Portal crash reporting test");
        SentrySdk.FlushAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "bcp-crash-test.txt"), $"{id} {DateTimeOffset.UtcNow:O}");
        Shutdown();
        return id == SentryId.Empty ? 4 : 0;
    }
#endif

    private static SentryEvent? BeforeSend(SentryEvent evt, SentryHint hint)
    {
        try
        {
            hint.Attachments.Clear();
            Redact(evt);
            return evt;
        }
        catch (Exception)
        {
            // Unsure what is in it: it does not go.
            return null;
        }
    }

    private static Breadcrumb? RedactBreadcrumb(Breadcrumb crumb)
    {
        try
        {
            CrashReportRedactor redactor = _redactor!;
            var data = crumb.Data?.ToDictionary(static p => p.Key, p => redactor.Redact(p.Value) ?? string.Empty);
            return new Breadcrumb(redactor.Redact(crumb.Message) ?? string.Empty, crumb.Type ?? "default", data, crumb.Category, crumb.Level);
        }
        catch (Exception) { return null; }
    }

    private static void Redact(SentryEvent evt)
    {
        CrashReportRedactor r = _redactor!;
        evt.ServerName = null;
        evt.User.Username = null;
        evt.User.Email = null;
        // An explicit placeholder stops the server inferring a location from the sending connection.
        evt.User.IpAddress = "0.0.0.0";
        evt.Contexts.Device.Name = null;
        evt.Contexts.Device.Timezone = null;
        evt.Contexts.Device.BootTime = null;
        evt.Request = new SentryRequest();
        evt.TransactionName = r.Redact(evt.TransactionName);
        evt.Logger = r.Redact(evt.Logger);
        if (evt.Message is { } message)
        {
            message.Message = r.Redact(message.Message);
            message.Formatted = r.Redact(message.Formatted);
            message.Params = null;
        }

        foreach (SentryException exception in evt.SentryExceptions ?? [])
        {
            exception.Value = r.Redact(exception.Value);
            RedactFrames(r, exception.Stacktrace);
        }
        foreach (SentryThread thread in evt.SentryThreads ?? [])
        {
            thread.Name = r.Redact(thread.Name);
            RedactFrames(r, thread.Stacktrace);
        }
        foreach (DebugImage image in evt.DebugImages ?? [])
        {
            image.CodeFile = r.Redact(image.CodeFile);
            image.DebugFile = r.Redact(image.DebugFile);
        }
        foreach ((string key, object? value) in evt.Extra.ToList())
            evt.SetExtra(key, value?.ToString() is { } text ? r.Redact(text) : null);
        foreach ((string key, string value) in evt.Tags.ToList())
            evt.SetTag(key, r.Redact(value) ?? string.Empty);
    }

    private static void RedactFrames(CrashReportRedactor r, SentryStackTrace? stack)
    {
        foreach (SentryStackFrame frame in stack?.Frames ?? [])
        {
            frame.FileName = r.Redact(frame.FileName);
            frame.AbsolutePath = r.Redact(frame.AbsolutePath);
            frame.ContextLine = r.Redact(frame.ContextLine);
            frame.Package = r.Redact(frame.Package);
            frame.Vars.Clear();
        }
    }
}
