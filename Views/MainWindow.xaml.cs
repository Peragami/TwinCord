using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TwinCord.Services;
using WinForms = System.Windows.Forms;

namespace TwinCord;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private int _current = 1;

    private AppSettings _settings = AppSettings.Load();

    private readonly TrayManager _tray = new();
    private readonly HistoryStore _history = new();
    private readonly UpdateService _updates;
    private WindowPlacementTracker? _placement;
    private PageWatchdog? _watchdog;
    private SettingsWindow? _settingsWindow;
    private HistoryWindow? _historyWindow;
    private UpdateDialog? _updateDialog;
    private bool _exiting;
    private bool _closing;

    private static readonly System.Windows.Media.Brush SelectedBrush =
        new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#5865F2"));
    private static readonly System.Windows.Media.Brush NormalBrush =
        new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2B2D31"));
    private static readonly System.Windows.Media.Brush AlertBrush =
        new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F0A020"));
    private static readonly System.Windows.Media.Brush NormalRailBrush =
        new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#B5BAC1"));

    private readonly Diagnostics _diag = new();

    public MainWindow()
    {
        InitializeComponent();
        WindowIcon.Set(this);
        _updates = new UpdateService(Http.Shared, Diagnostics.AppVersion, () => _exiting);
        _placement = new WindowPlacementTracker(this, () => _settings);
        _placement.Restore();
        _watchdog = new PageWatchdog(
            new PageWatchdog.AccountSlot
            {
                View = View1,
                IsReady = () => _viewReady[1],
                ActionButton = Acc1Button,
                AccountName = () => _settings.Acc1Name,
                Badge = Badge1,
                BadgeText = Badge1Text,
            },
            new PageWatchdog.AccountSlot
            {
                View = View2,
                IsReady = () => _viewReady[2],
                ActionButton = Acc2Button,
                AccountName = () => _settings.Acc2Name,
                Badge = Badge2,
                BadgeText = Badge2Text,
            },
            () => _settings, _diag, () => _current);
        _tray.ShowMainRequested += ShowMain;
        _tray.HistoryRequested += OpenHistory;
        _tray.SettingsRequested += OpenSettings;
        _tray.UpdateCheckRequested += () => _ = CheckUpdateFromTrayAsync();
        _tray.ExitRequested += () => { _exiting = true; Close(); };
        if (_settings.StartMinimized) WindowState = WindowState.Minimized;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
        SourceInitialized += MainWindow_SourceInitialized;
        _diag.Degraded += OnFeatureDegraded;
        View1.WebMessageReceived += View_WebMessageReceived;
        View2.WebMessageReceived += View_WebMessageReceived;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // Win11ダークタイトルバー
        }
        catch { }
        try
        {
            // グローバルホットキー: Ctrl+Alt+1/2=切替、Ctrl+Alt+M=表示中ミュート
            RegisterGlobalHotkeys(new WindowInteropHelper(this).Handle);
        }
        catch { }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await StartAsync();
        }
        catch (Exception ex)
        {
            await ReportStartupErrorAsync(ex);
        }
    }

    /// <summary>起動処理。順序は起動時間とメモリ抑制の前提に関わるため変えない。</summary>
    private async Task StartAsync()
    {
        Log.Write($"TwinCord v{Diagnostics.AppVersion()} 起動");
        RefreshRail();
        _history.Load();
        ApplySettings();
        _watchdog?.Start();
        HideToTrayIfStartMinimized();
        // 起動時から最小化/非表示なら、初回読み込み完了後に表示側もLowになる
        // (読み込み完了前は SetViewLoaded まで抑制しない)
        _watchdog?.SetWindowHidden(WindowState == WindowState.Minimized || !IsVisible);
        // 逐次起動(起動ピークを抑えつつ両方常駐)
        await EnsureViewAsync(1);
        RefreshRail();
        StartSecondViewInBackground();
        SwitchTo(_current);
        RefreshAccountAvatars();
        if (_settings.AutoUpdateCheck)
            _ = CheckUpdateOnStartupAsync();
    }

    private void HideToTrayIfStartMinimized()
    {
        // 「起動時に最小化」はUIの説明どおりトレイへ格納する
        // (コンストラクタでWindowStateを設定した時点ではStateChanged未購読のためHideされない)
        if (_settings.StartMinimized && IsVisible)
        {
            Hide();
            _tray.Ensure();
        }
    }

    /// <summary>2枚目は1枚目の初回表示を待ってから始める(表示中アカの初回表示を優先)。</summary>
    private void StartSecondViewInBackground()
    {
        // 常駐が目的なので完了は待たず、失敗だけログに残す
        _ = EnsureViewAsync(2).ContinueWith(
            t => Log.Write("acc2の初期化に失敗: " + t.Exception?.GetBaseException().Message),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private void RefreshAccountAvatars()
    {
        if (_watchdog is null) return;
        _ = _watchdog.TryUpdateAvatarAsync(1);
        _ = _watchdog.TryUpdateAvatarAsync(2);
    }

    /// <summary>起動に失敗したときは、メッセージとログ(ファイル)を残す。</summary>
    private static async Task ReportStartupErrorAsync(Exception ex)
    {
        var log = Path.Combine(AppSettings.DataFolder(), "startup.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        await File.WriteAllTextAsync(log, ex.ToString());
        System.Windows.MessageBox.Show(ex.Message, "TwinCord init error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    // ---- トレイ常駐 ----

    internal void BringToFront() => ShowMain();

    private void ShowMain()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        _watchdog?.SetWindowHidden(false);
    }

    /// <summary>トレイのバルーン。クリックで必ず前面化し、onClickがあれば続けて実行する。</summary>
    private void ShowBalloon(string title, string text, WinForms.ToolTipIcon icon, int timeoutMs, Action? onClick = null)
    {
        _tray.ShowBalloon(title, text, icon, timeoutMs, () =>
        {
            BringToFront();
            onClick?.Invoke();
            return Task.CompletedTask;
        });
    }

    /// <summary>クリックでそのアカウントへ切り替える通知用バルーン。</summary>
    private void ShowAccountBalloon(int n, string title, string text, int timeoutMs)
        => ShowBalloon(title, text, WinForms.ToolTipIcon.Info, timeoutMs, () => SwitchTo(n));

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        _placement?.Save();
        if (_settings.MinimizeToTray && !_exiting)
        {
            e.Cancel = true;
            _closing = false;
            // トレイ格納中は表示側もLowに(通話中はPageWatchdog側で除外)
            _watchdog?.SetWindowHidden(true);
            TrimHostMemory();
            Hide();
            ShowBalloon("TwinCord", "トレイに格納しました", WinForms.ToolTipIcon.Info, 2000);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        UnregisterGlobalHotkeys();
        _tray.Dispose();
        // 書き込み待ちのログ・通知履歴を残す
        _history.Flush();
        Log.Flush();
        // トレイ格納中に開いた履歴ウィンドウ等が残っていても確実に終了させる。
        // 既定のShutdownMode(OnLastWindowClose)だと、他のウィンドウが開いたままだと
        // プロセスが生き残り、トレイも無いため操作不能になる。
        Dispatcher.BeginInvoke(() =>
        {
            try { System.Windows.Application.Current?.Shutdown(); }
            catch { }
        });
        base.OnClosed(e);
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            _watchdog?.SetWindowHidden(false);
            return;
        }
        // 最小化中は表示側もLowに(通話中はPageWatchdog側で除外)
        _watchdog?.SetWindowHidden(true);
        TrimHostMemory();
        if (_settings.MinimizeToTray)
        {
            Hide();
            _tray.Ensure();
        }
    }

    // ホスト側のヒープ返却。Chromium側には触らない(常駐・通話に影響させないため)
    private static void TrimHostMemory()
    {
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            using var p = Process.GetCurrentProcess();
            EmptyWorkingSet(p.Handle);
        }
        catch { }
    }
}
