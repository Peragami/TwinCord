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

    public void Restore()
    {
        try
        {
            var s = _settings();
            if (s.WinWidth is not double width || s.WinHeight is not double height
                || width < 200 || height < 200) return;
            _window.Width = width;
            _window.Height = height;
            if (s.WinLeft is double left && s.WinTop is double top)
            {
                // モニター構成変更で完全画面外になっていたら既定位置のままにする
                var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                if (vs.IntersectsWith(new Rect(left, top, width, height)))
                {
                    _window.Left = left;
                    _window.Top = top;
                }
            }
            if (s.WinMaximized) _window.WindowState = WindowState.Maximized;
        }
        catch { }
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
