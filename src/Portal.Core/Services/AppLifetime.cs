namespace Portal.Core.Services;

/// <summary>
/// Quit and sign-out bookkeeping for the running app. Quitting stops the background work for good; Windows signing out
/// or shutting down does not, because the user can still cancel it and the app then keeps running.
/// </summary>
public sealed class AppLifetime
{
    private volatile bool _stopping;
    private int _quitReapplyDone;
    private int _sessionEndRunning;

    /// <summary>The app is quitting: the care loop and the history stop.</summary>
    public bool Stopping => _stopping;

    /// <summary>Marks the app as quitting. True only for the first call, which runs the bounded quit re-apply.</summary>
    public bool BeginQuit()
    {
        _stopping = true;
        return Interlocked.Exchange(ref _quitReapplyDone, 1) == 0;
    }

    /// <summary>
    /// Windows is signing out or shutting down. True when the session-end re-apply should run now: not after a quit and
    /// not while another one runs. Changes nothing else; call <see cref="EndSessionEnd"/> afterwards.
    /// </summary>
    public bool BeginSessionEnd() => !_stopping && Interlocked.CompareExchange(ref _sessionEndRunning, 1, 0) == 0;

    public void EndSessionEnd() => Volatile.Write(ref _sessionEndRunning, 0);
}
