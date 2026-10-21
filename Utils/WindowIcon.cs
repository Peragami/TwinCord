using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace TwinCord;

/// <summary>exe内蔵アイコンをウィンドウに設定する。</summary>
public static class WindowIcon
{
    public static void Set(Window w)
    {
        // すべてのウィンドウ(設定・履歴・更新・名前変更)のタイトルバーを暗色に揃える
        w.SourceInitialized += (_, _) => WindowTheme.ApplyDarkTitleBar(w);
        try
        {
            using var icon = AppIcon.Extract();
            if (icon is null) return;
            w.Icon = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        catch { }
    }
}
