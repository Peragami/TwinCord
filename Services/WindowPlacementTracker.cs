using System.Windows;
using System.Windows.Threading;

namespace TwinCord.Services;

/// <summary>
/// ウィンドウ配置の復元・保存。移動/リサイズは2秒デバウンスで書き込む。
/// </summary>
public sealed class WindowPlacementTracker
{
    private readonly Window _window;
    private readonly Func<AppSettings> _settings;
    private DispatcherTimer? _timer;

    public WindowPlacementTracker(Window window, Func<AppSettings> settings)
    {
        _window = window;
        _settings = settings;
        window.LocationChanged += (_, _) => Schedule();
        window.SizeChanged += (_, _) => Schedule();
    }

    private const double MinSize = 200;

    public void Restore()
    {
        try
        {
            var s = _settings();
            if (s.WinWidth is not double width || s.WinHeight is not double height) return;
            // 壊れた値(NaN/∞)は復元しない
            if (!double.IsFinite(width) || !double.IsFinite(height)) return;
            // 画面より大きすぎる値は仮想スクリーン内に丸める(操作不能にならないように)
            var maxW = Math.Max(MinSize, SystemParameters.VirtualScreenWidth);
            var maxH = Math.Max(MinSize, SystemParameters.VirtualScreenHeight);
            width = Math.Clamp(width, MinSize, maxW);
            height = Math.Clamp(height, MinSize, maxH);
            _window.Width = width;
            _window.Height = height;
            if (s.WinLeft is double left && s.WinTop is double top
                && double.IsFinite(left) && double.IsFinite(top))
            {
                // モニター構成変更で画面外へ出ていた場合は、操作できる位置まで戻す
                var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                var (visibleLeft, visibleTop) = ClampToVisible(vs, left, top, width);
                _window.Left = visibleLeft;
                _window.Top = visibleTop;
            }
            if (s.WinMaximized) _window.WindowState = WindowState.Maximized;
        }
        catch { }
    }

    private const double MinVisible = 80;

    /// <summary>
    /// 保存された位置が画面外でも、少なくともタイトルバー付近が画面内に残るよう補正する。
    /// マルチモニターでは仮想スクリーン全体を基準にする。
    /// </summary>
    internal static (double Left, double Top) ClampToVisible(
        Rect screen, double left, double top, double width)
    {
        if (screen.Width <= 0 || screen.Height <= 0) return (left, top);

        var minLeft = screen.Left - Math.Max(0, width - MinVisible);
        var maxLeft = screen.Right - MinVisible;
        if (minLeft > maxLeft) minLeft = maxLeft;

        var minTop = screen.Top;
        var maxTop = screen.Bottom - MinVisible;
        if (minTop > maxTop) minTop = maxTop;

        return (Math.Clamp(left, minLeft, maxLeft), Math.Clamp(top, minTop, maxTop));
    }

    public void Save()
    {
        try
        {
            var s = _settings();
            s.WinMaximized = _window.WindowState == WindowState.Maximized;
            if (_window.WindowState == WindowState.Normal)
            {
                s.WinLeft = _window.Left;
                s.WinTop = _window.Top;
                s.WinWidth = _window.Width;
                s.WinHeight = _window.Height;
            }
            s.Save();
        }
        catch { }
    }

    private void Schedule()
    {
        try
        {
            if (_window.WindowState != WindowState.Normal) return;
            if (_timer is null)
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _timer.Tick += (_, _) => { _timer?.Stop(); Save(); };
            }
            _timer.Stop();
            _timer.Start();
        }
        catch { }
    }
}
