using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Portal.Core.Battery;

namespace BatteryCarePortal;

/// <summary>Windows notifications for the battery events. Silent; a click opens the page the notification is about.</summary>
internal static class AppToasts
{
    private const string PageArgument = "page";
    private static readonly HashSet<string> Pages = new(StringComparer.Ordinal) { "Overview", "Charging" };
    private static bool _registered;

    /// <summary>Raised with the page to open when the user clicks a notification (any thread).</summary>
    public static event Action<string>? PageRequested;

    /// <summary>Call once at launch, before reading the activation arguments.</summary>
    public static void Register()
    {
        try
        {
            if (!AppNotificationManager.IsSupported()) return;
            AppNotificationManager.Default.NotificationInvoked += (_, args) => OpenFrom(args);
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception) { }
    }

    /// <summary>The page a notification launch asks for, or null when this launch did not come from a notification.</summary>
    public static string? PageFromActivation(AppActivationArguments activation) =>
        activation.Kind == ExtendedActivationKind.AppNotification && activation.Data is AppNotificationActivatedEventArgs args
            ? PageFrom(args)
            : null;

    public static void Show(BatteryToast toast)
    {
        if (!_registered) return;
        (string title, string text, string page) = Content(toast);
        try
        {
            AppNotification notification = new AppNotificationBuilder()
                .AddArgument(PageArgument, page)
                .AddText(title)
                .AddText(text)
                .MuteAudio()
                .BuildNotification();
            notification.Tag = toast.Kind.ToString();
            notification.Group = "battery";
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception) { }
    }

    private static DateTime _lastFailureUtc = DateTime.MinValue;

    /// <summary>A failed action the user could not see (tray menu with the pop-up hidden). Same sentence as the pop-up; at most one per 10 s; a click opens the Charging page.</summary>
    public static void ShowFailure(string text)
    {
        if (!_registered || (DateTime.UtcNow - _lastFailureUtc).TotalSeconds < 10) return;
        _lastFailureUtc = DateTime.UtcNow;
        try
        {
            AppNotification notification = new AppNotificationBuilder()
                .AddArgument(PageArgument, "Charging")
                .AddText(Strings.Get("AppName"))
                .AddText(text)
                .MuteAudio()
                .BuildNotification();
            notification.Tag = "ChargeActionFailed";
            notification.Group = "battery";
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception) { }
    }

    private static (string Title, string Text, string Page) Content(BatteryToast t) => t.Kind switch
    {
        BatteryToastKind.LimitReached => (Fmt("ToastLimitReachedTitle", t.Percent), Strings.Get("ToastLimitReachedText"), "Charging"),
        BatteryToastKind.Unplug => (Fmt("ToastUnplugTitle", t.Percent), Strings.Get("ToastUnplugText"), "Charging"),
        BatteryToastKind.PlugIn => (Fmt("ToastPlugInTitle", t.Percent), Strings.Get("ToastPlugInText"), "Overview"),
        _ when t.TooHot => (Strings.Get("ToastTopUpHotTitle"), Fmt("ToastTopUpHotText", t.Limit ?? 0), "Charging"),
        _ when t.Full => (Strings.Get("ToastTopUpFullTitle"), Fmt("ToastTopUpFullText", t.Limit ?? 0), "Charging"),
        _ => (Strings.Get("ToastTopUpEndedTitle"), Fmt("ToastTopUpEndedText", t.Limit ?? 0, t.Percent), "Charging"),
    };

    private static string Fmt(string key, params object[] args) => string.Format(Strings.Get(key), args);

    private static void OpenFrom(AppNotificationActivatedEventArgs args)
    {
        if (PageFrom(args) is { } page) PageRequested?.Invoke(page);
    }

    private static string? PageFrom(AppNotificationActivatedEventArgs args) =>
        args.Arguments.TryGetValue(PageArgument, out string? page) && Pages.Contains(page) ? page : null;
}
