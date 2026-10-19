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
    private bool _errorReported;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 例外でプロセスが黙って消えないよう、最後の砦でログに残す
        DispatcherUnhandledException += (_, args) =>
        {
            Diagnostics.Log("未処理例外(UI): " + args.Exception);
            if (!_errorReported)
            {
                _errorReported = true;
                try
                {
                    System.Windows.MessageBox.Show(
                        "予期しないエラーが発生しました。診断ログに記録しました。\n" + args.Exception.Message,
                        "TwinCord", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch { }
            }
            args.Handled = true; // 常駐アプリのため落とさず継続する
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Diagnostics.Log("未処理例外(致命的): " + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Diagnostics.Log("未処理タスク例外: " + args.Exception);
            args.SetObserved();
        };

        _mutex = new Mutex(true, AppConstants.SingleInstanceMutex, out var created);
        if (!created)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(AppConstants.ShowWindowEvent);
                ev.Set();
            }
            catch { }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, AppConstants.ShowWindowEvent);
        // 単一exe(展開方式)はバージョンごとに%TEMP%へ展開するため、古い展開フォルダを掃除する
        BundleCleanup.Start();
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
