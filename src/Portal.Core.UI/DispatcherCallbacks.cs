using Microsoft.UI.Dispatching;

namespace Portal.Core.UI;

/// <summary>
/// Queued callbacks for the shared controls. A callback that reaches a window which has closed since it was queued
/// (0x800710DD, or RO_E_CLOSED) is dropped; anything else goes to <see cref="Report"/> and is rethrown.
/// </summary>
public static class DispatcherCallbacks
{
    private const int WindowClosed = unchecked((int)0x800710DD);
    private const int ObjectClosed = unchecked((int)0x80000013);

    /// <summary>Receives an exception that escaped a guarded callback, so the host app can log it.</summary>
    public static Action<Exception, string>? Report { get; set; }

    public static bool TryEnqueueGuarded(this DispatcherQueue? queue, DispatcherQueueHandler callback) =>
        queue is not null && queue.TryEnqueue(Guard(callback));

    public static bool TryEnqueueGuarded(this DispatcherQueue? queue, DispatcherQueuePriority priority, DispatcherQueueHandler callback) =>
        queue is not null && queue.TryEnqueue(priority, Guard(callback));

    public static Windows.Foundation.TypedEventHandler<DispatcherQueueTimer, object> GuardedTick(Windows.Foundation.TypedEventHandler<DispatcherQueueTimer, object> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return (timer, args) =>
        {
            try { handler(timer, args); }
            catch (Exception ex) when (Escaped(ex, "a timer tick"))
            {
                try { timer.Stop(); } catch (Exception stop) when (stop is System.Runtime.InteropServices.COMException or ObjectDisposedException) { }
            }
        };
    }

    private static DispatcherQueueHandler Guard(DispatcherQueueHandler callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return () =>
        {
            try { callback(); }
            catch (Exception ex) when (Escaped(ex, "a queued callback")) { }
        };
    }

    private static bool Escaped(Exception exception, string where)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current.HResult is WindowClosed or ObjectClosed) return true;
        try { Report?.Invoke(exception, where); } catch (Exception) { }
        return false;
    }
}
