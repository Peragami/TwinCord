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
        // ポータブル形式: データはexeと同じフォルダのdata配下に置く。書けない場所では起動しない
        var dataError = AppSettings.PrepareDataFolder();
        if (dataError is not null)
        {
            System.Windows.MessageBox.Show(
                "データを保存できません。デスクトップなど書き込みできる場所へフォルダごと移動してから、" +
                "TwinCord.exeを起動してください。\n\n対象: " + AppSettings.DataFolder() + "\n理由: " + dataError,
                "TwinCord", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }
        // 旧バージョン(1.10.xまで)のデータがあれば、一度だけ引き継ぎを確認する
        OfferDataMigration();
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

    /// <summary>
    /// 旧バージョンのデータ(%LOCALAPPDATA%\TwinCord)をdataフォルダへ引き継ぐか一度だけ確認する。
    /// 開発ビルドでは作業中のデータを動かさないよう何もしない。
    /// </summary>
    private static void OfferDataMigration()
    {
        if (Diagnostics.IsDevBuild()) return;
        var data = AppSettings.DataFolder();
        var legacy = DataMigration.LegacyFolder();
        if (!DataMigration.ShouldOffer(data, legacy)) return;
        try
        {
            var res = System.Windows.MessageBox.Show(
                "以前のバージョンのデータが見つかりました。設定とログイン情報をこのフォルダへ引き継ぎますか？\n\n" +
                "元: " + legacy + "\n先: " + data + "\n\n" +
                "「いいえ」を選ぶと引き継がずに新しく始めます(後から手動でコピーすることもできます)。",
                "TwinCord データの引き継ぎ", MessageBoxButton.YesNo, MessageBoxImage.Question);
            var result = res == MessageBoxResult.Yes
                ? DataMigration.Migrate(data, legacy)
                : "引き継がずに開始";
            DataMigration.MarkDone(data, result);
            Diagnostics.Log("データ引き継ぎ: " + result);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("データ引き継ぎに失敗: " + ex.Message);
        }
    }
}
