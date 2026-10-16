using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace TwinCord;

/// <summary>exe内蔵アイコンをウィンドウに設定する。</summary>
public static class WindowIcon
{
    public static void Set(Window w)
    {
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return;
            w.Icon = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        catch { }
    }
}
