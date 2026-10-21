using WinForms = System.Windows.Forms;

namespace TwinCord.Services;

/// <summary>
/// トレイアイコン・メニュー・バルーン。クリック時の動作は呼び出し側が渡す。
/// </summary>
public sealed class TrayManager : IDisposable
{
    private WinForms.NotifyIcon? _tray;
    private System.Drawing.Icon? _icon;
    private readonly Queue<Balloon> _balloons = new();
    private Balloon? _currentBalloon;
    private System.Windows.Threading.DispatcherTimer? _clickTimer;
    private bool _disposed;
    private const int MaxPendingBalloons = 5;

    private sealed record Balloon(
        string Title, string Text, WinForms.ToolTipIcon Icon, int TimeoutMs, Func<Task>? OnClick);

    public event Action? ShowMainRequested;
    public event Action? HistoryRequested;
    public event Action? SettingsRequested;
    public event Action? UpdateCheckRequested;
    public event Action? ExitRequested;

    public bool IsVisible => _tray?.Visible == true;

    public void Ensure()
    {
        if (_disposed) return;
        if (_tray is not null)
        {
            _tray.Visible = true;
            return;
        }
        _icon ??= AppIcon.Extract();
        _tray = new WinForms.NotifyIcon
        {
            Icon = _icon ?? System.Drawing.SystemIcons.Application,
            Text = "TwinCord",
            Visible = true,
        };
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("表示", null, (_, _) => ShowMainRequested?.Invoke());
        menu.Items.Add("通知履歴", null, (_, _) => HistoryRequested?.Invoke());
        menu.Items.Add("設定", null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add("更新を確認", null, (_, _) => UpdateCheckRequested?.Invoke());
        menu.Items.Add("終了", null, (_, _) => ExitRequested?.Invoke());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowMainRequested?.Invoke();
        _tray.BalloonTipClicked += (_, _) =>
        {
            var balloon = _currentBalloon;
            if (balloon is null) return;
            _currentBalloon = null;
            _clickTimer?.Stop();
            try
            {
                if (balloon.OnClick is not null) _ = balloon.OnClick();
            }
            catch (Exception ex)
            {
                Log.Write("バルーンクリック処理に失敗: " + ex.Message);
            }
            ShowNextBalloon();
        };
    }

    public void ShowBalloon(string title, string text, WinForms.ToolTipIcon icon, int timeoutMs, Func<Task>? onClick)
    {
        if (_disposed) return;
        // NotifyIcon/DispatcherTimerはUIスレッドで扱う。バックグラウンドから来た場合は戻す
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => ShowBalloon(title, text, icon, timeoutMs, onClick));
            return;
        }
        Ensure();
        // 連続表示で古いバルーンのクリック動作が新しいものに上書きされないよう、
        // Windowsが1つずつ表示する前提でキューに積む
        if (_balloons.Count >= MaxPendingBalloons) _balloons.Dequeue();
        _balloons.Enqueue(new Balloon(title, text, icon, timeoutMs, onClick));
        if (_currentBalloon is null) ShowNextBalloon();
    }

    private void ShowNextBalloon()
    {
        if (_disposed || _tray is null) { _balloons.Clear(); _currentBalloon = null; return; }
        if (_currentBalloon is not null || _balloons.Count == 0)
        {
            if (_balloons.Count == 0) _clickTimer?.Stop();
            return;
        }
        if (_clickTimer is null)
        {
            _clickTimer = new System.Windows.Threading.DispatcherTimer();
            _clickTimer.Tick += (_, _) =>
            {
                _clickTimer?.Stop();
                _currentBalloon = null;
                ShowNextBalloon();
            };
        }
        _currentBalloon = _balloons.Dequeue();
        _clickTimer.Stop();
        _clickTimer.Interval =
            TimeSpan.FromMilliseconds(Math.Max(1000, _currentBalloon.TimeoutMs) + 500);
        _clickTimer.Start();
        _tray.ShowBalloonTip(
            _currentBalloon.TimeoutMs, _currentBalloon.Title, _currentBalloon.Text, _currentBalloon.Icon);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _clickTimer?.Stop(); } catch { }
        _clickTimer = null;
        _currentBalloon = null;
        _balloons.Clear();
        if (_tray is not null)
        {
            // 先に非表示にしないとアイコンが残ることがある
            try { _tray.Visible = false; } catch { }
            _tray.Dispose();
            _tray = null;
        }
        _icon?.Dispose();
        _icon = null;
    }
}
