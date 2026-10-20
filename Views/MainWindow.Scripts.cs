using System.Windows;
using TwinCord.Services;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;

namespace TwinCord;

public partial class MainWindow : Window
{
    /// <summary>
    /// ページ生成時に走るスクリプトを登録する。互いに独立なのでまとめて投げる(直列待ちを減らす)。
    /// ダウンロードボタン隠しは設定に関わらず登録し、表示/非表示はページごとにホストから指示する
    /// (設定OFFへ切り替えても解除できるようにするため)。
    /// </summary>
    private Task RegisterDocumentScriptsAsync(int n, WpfWebView2 view) =>
        Task.WhenAll(
            RegisterDocumentScriptAsync(n, view, PageScripts.Hotkey, Feature.HotkeyPage, "キー操作"),
            RegisterDocumentScriptAsync(n, view, PageScripts.Notify, null, "通知"),
            RegisterDocumentScriptAsync(n, view, PageScripts.HideDownload, null, "ダウンロードボタン"));

    /// <summary>ページ生成時に走るスクリプトを登録する。失敗はログと劣化判定へ反映する。</summary>
    private async Task RegisterDocumentScriptAsync(int n, WpfWebView2 view,
        string script, Feature? feature, string label)
    {
        try
        {
            var scriptId = await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script);
            if (feature is Feature f) _diag.ReportOk(f);
            Log.Write($"{label}スクリプト登録(acc{n}): {scriptId}");
        }
        catch (Exception ex)
        {
            if (feature is Feature f) _diag.ReportFail(f);
            Log.Write($"{label}スクリプト登録失敗(acc{n}): {ex.Message}");
        }
    }

    /// <summary>「アプリをダウンロード」ボタンの表示/非表示を今のページへ反映する(再読み込み不要)。</summary>
    private static async Task ApplyHideDownloadAsync(int n, WpfWebView2 view, bool hide)
    {
        try
        {
            var core = view.CoreWebView2;
            if (core is null) return;
            var result = (await core.ExecuteScriptAsync(PageScripts.HideDownloadApply(hide)))?.Trim();
            // 関数が無い(未注入・構文エラー)と null が返る。黙って効かなくなるのを避けるため記録する
            if (result is null or "null")
                Log.Write($"ダウンロードボタン反映(acc{n}): スクリプト未適用");
        }
        catch { }
    }
}
