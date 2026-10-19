using WinForms = System.Windows.Forms;

namespace TwinCord.Services;

/// <summary>
/// トレイアイコン・メニュー・バルーン。クリック時の動作は呼び出し側が渡す。
/// </summary>
public sealed class TrayManager : IDisposable
{
    private WinForms.NotifyIcon? _tray;
    private System.Drawing.Icon? _icon;
    private Func<Task>? _clickAction;
    private System.Windows.Threading.DispatcherTimer? _clickTimer;
    private bool _disposed;

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
            var action = _clickAction;
            _clickAction = null;
            if (action is not null) _ = action();
        };
    }

    public void ShowBalloon(string title, string text, WinForms.ToolTipIcon icon, int timeoutMs, Func<Task>? onClick)
    {
        if (_disposed) return;
        Ensure();
        _clickAction = onClick;
        // 表示が消えた後に古いクリック動作が残らないよう、表示時間の経過でクリアする
        if (_clickTimer is null)
        {
            _clickTimer = new System.Windows.Threading.DispatcherTimer();
            _clickTimer.Tick += (_, _) =>
            {
                _clickTimer?.Stop();
                _clickAction = null;
            };
        }
        _clickTimer.Stop();
        _clickTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1000, timeoutMs) + 500);
        _clickTimer.Start();
        _tray?.ShowBalloonTip(timeoutMs, title, text, icon);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _clickTimer?.Stop(); } catch { }
        _clickTimer = null;
        _clickAction = null;
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
