using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Charon;

public partial class App : Application
{
    private const string MutexName = "Local\\CharonSingleInstanceMutex";
    private const string ShowEventName = "Local\\CharonShowWindowEvent";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            // Signal the already-running instance to restore from the tray, then exit
            try
            {
                using var existingEvent = EventWaitHandle.OpenExisting(ShowEventName);
                existingEvent.Set();
            }
            catch { }

            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _ = Task.Run(() =>
        {
            while (_showEvent.WaitOne())
            {
                Dispatcher.Invoke(() =>
                {
                    if (MainWindow is MainWindow mw)
                        mw.RestoreFromTray();
                });
            }
        });

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showEvent?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}