using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
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

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private int _current = 1;

    private AppSettings _settings = AppSettings.Load();

    // 起動〜初回表示までの所要時間をログに残すための基準時刻
    private static readonly DateTime AppStartedUtc = DateTime.UtcNow;
    private static long ElapsedMs() => (long)(DateTime.UtcNow - AppStartedUtc).TotalMilliseconds;

    private bool _view1Ready;
    private bool _view2Ready;
    private Task? _initTask1;
    private Task? _initTask2;
    // 初回読み込み(ページ表示)が済んだか。済むまでメモリ抑制を掛けない。
    private readonly bool[] _viewContentLoaded = new bool[3];
    // 1枚目の初回表示完了。2枚目の初期化はこれ(またはタイムアウト)を待ってから始める。
    private readonly TaskCompletionSource _firstViewLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // 2アカウントで共有するWebView2環境(1つだけ作る)
    private CoreWebView2Environment? _sharedEnv;
    private Task<CoreWebView2Environment>? _sharedEnvTask;

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
    private HwndSource? _hotkeySource;

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
                IsReady = () => _view1Ready,
                ActionButton = Acc1Button,
                AccountName = () => _settings.Acc1Name,
                Badge = Badge1,
                BadgeText = Badge1Text,
            },
            new PageWatchdog.AccountSlot
            {
                View = View2,
                IsReady = () => _view2Ready,
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
            // MOD_NOREPEAT(0x4000)でキー長押しの連続発火を防ぐ
            var hwnd = new WindowInteropHelper(this).Handle;
            _hotkeySource = HwndSource.FromHwnd(hwnd);
            _hotkeySource?.AddHook(WndProcHook);
            const uint mod = 0x0001 | 0x0002 | 0x4000; // Alt | Ctrl | MOD_NOREPEAT
            if (!RegisterHotKey(hwnd, 1, mod, 0x31))
                ReportHotkeyFailure("Ctrl+Alt+1");
            if (!RegisterHotKey(hwnd, 2, mod, 0x32))
                ReportHotkeyFailure("Ctrl+Alt+2");
            if (!RegisterHotKey(hwnd, 3, mod, 0x4D))
                ReportHotkeyFailure("Ctrl+Alt+M");
        }
        catch { }
    }

    private void ReportHotkeyFailure(string keys)
    {
        Log.Write($"グローバルホットキー登録失敗: {keys} (他アプリが使用中の可能性)");
        _diag.ReportFail(Feature.HotkeyGlobal);
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312)
        {
            switch (wParam.ToInt32())
            {
                case 1: SwitchTo(1); handled = true; break;
                case 2: SwitchTo(2); handled = true; break;
                case 3: ToggleMute(_current); handled = true; break;
            }
        }
        return IntPtr.Zero;
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
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                UnregisterHotKey(hwnd, 1);
                UnregisterHotKey(hwnd, 2);
                UnregisterHotKey(hwnd, 3);
            }
            _hotkeySource?.RemoveHook(WndProcHook);
        }
        catch { }
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

    // ---- 未読バッジ・死活監視(実体は PageWatchdog) ----

    private async Task<string> RunDiagnosticsAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"TwinCord v{Diagnostics.AppVersion()} / {DateTime.Now:yyyy-MM-dd HH:mm}");
        try
        {
            long mb = Process.GetCurrentProcess().PrivateMemorySize64 / 1024 / 1024;
            sb.AppendLine($"プロセス全体: {mb}MB");
        }
        catch { }
        sb.AppendLine($"2アカウント共有: {(_sharedEnv is not null ? "有効" : "未初期化")}");
        try
        {
            sb.AppendLine("WebView2: " + CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch
        {
            sb.AppendLine("WebView2: 取得失敗(未インストールの疑い)");
        }
        sb.AppendLine(await CheckViewAsync("Acc1", View1, _view1Ready, _settings.Zoom1));
        sb.AppendLine(await CheckViewAsync("Acc2", View2, _view2Ready, _settings.Zoom2));
        var bad = _diag.DegradedKeys();
        sb.Append("劣化中の機能: ");
        sb.AppendLine(bad.Count == 0 ? "なし" : string.Join(", ", bad.Select(Diagnostics.DisplayName)));
        var report = sb.ToString();
        Log.Write("自己診断:\n" + report);
        return report;
    }

}
