using System.Drawing;

namespace TwinCord;

/// <summary>exeに埋め込まれたアイコンの取得(トレイとウィンドウで共有する)。</summary>
public static class AppIcon
{
    /// <summary>取得できなければnull。呼び出し側がDisposeする。</summary>
    public static Icon? Extract()
    {
        try
        {
            var path = Environment.ProcessPath;
            return string.IsNullOrEmpty(path) ? null : Icon.ExtractAssociatedIcon(path);
        }
        catch
        {
            return null;
        }
    }
}
