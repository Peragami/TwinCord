using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TwinCord;

/// <summary>
/// ウィンドウのタイトルバーをダークテーマにする。Windows 11の新しい属性(20)を試し、
/// 古いWindows 10では従来の属性(19)へフォールバックする。
/// </summary>
public static class WindowTheme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void ApplyDarkTitleBar(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
        }
        catch { }
    }
}
