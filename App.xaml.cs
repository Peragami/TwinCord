using System.Threading;
using System.Windows;

namespace TwinCord;

/// <summary>
/// Interaction logic for App.xaml
/// 二重起動したら先発のウィンドウを表示して終了する。
/// </summary>
public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "TwinCord_SingleInstance", out var created);
        if (!created)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting("TwinCord_ShowWindow");
                ev.Set();
            }
            catch { }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "TwinCord_ShowWindow");
        var watcher = new Thread(() =>
        {
            while (true)
            {
                try { _showEvent.WaitOne(); }
                catch { return; }
                try
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (MainWindow is MainWindow w) w.BringToFront();
                    });
                }
                catch { return; }
            }
        })
        { IsBackground = true };
        watcher.Start();
        base.OnStartup(e);
    }
}
