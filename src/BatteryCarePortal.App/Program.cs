using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace BatteryCarePortal;

public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (RedirectToRunningInstance()) return 0;

        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    private static bool RedirectToRunningInstance()
    {
        AppInstance mainInstance = AppInstance.FindOrRegisterForKey("main");
        if (mainInstance.IsCurrent)
        {
            mainInstance.Activated += (_, e) => App.Instance?.OnRedirectedActivation(e);
            return false;
        }

        AppActivationArguments activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        AllowSetForegroundWindow(mainInstance.ProcessId);
        // Redirect off the STA thread; awaiting it here would block without pumping COM.
        Task.Run(() => mainInstance.RedirectActivationToAsync(activation).AsTask()).Wait();
        return true;
    }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
}
