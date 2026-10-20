using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TwinCord;

public partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private HwndSource? _hotkeySource;

    /// <summary>グローバルホットキーを登録する(Ctrl+Alt+1/2=切替、Ctrl+Alt+M=表示中ミュート)。</summary>
    private void RegisterGlobalHotkeys(IntPtr hwnd)
    {
        // MOD_NOREPEAT(0x4000)でキー長押しの連続発火を防ぐ
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

    private void UnregisterGlobalHotkeys()
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
    }
}
