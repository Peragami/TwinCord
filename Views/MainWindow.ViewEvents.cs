using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using TwinCord.Services;
using WinForms = System.Windows.Forms;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;

namespace TwinCord;

public partial class MainWindow : Window
{
    // ProcessFailedの再読み込み回数(1/2のみ使用)。短時間に連続したら打ち切る。
    private readonly int[] _processReloadCount = new int[3];
    private readonly DateTime[] _processReloadAt = new DateTime[3];
    private readonly bool[] _reloadFailureShown = new bool[3];
    private int _browserProcessFailureShown;

    /// <summary>両アカウント共通の初期化(設定・スクリプト登録・イベント購読・初期表示)。</summary>
    private async Task ConfigureViewAsync(int n, WpfWebView2 view, double zoom)
    {
        // メモリ抑制(Low)は初回読み込みが終わってから掛ける(読み込み前のLowは初回表示を遅くするため)。
        // 適用は OnViewContentLoaded → PageWatchdog.SetViewLoaded 経由。
        // リンクホバー時の左下URLプレビュー(ステータスバー)を出さない
        try { view.CoreWebView2.Settings.IsStatusBarEnabled = false; } catch { }
        SetZoom(view, zoom);
        try
        {
            view.CoreWebView2.IsMuted = n == 1 ? _settings.Mute1 : _settings.Mute2;
        }
        catch { }
        // ホットキー・通知フック・ダウンロードボタン隠しは互いに独立なので、登録はまとめて投げる(直列待ちを減らす)。
        // ダウンロードボタン隠しは設定に関わらず登録し、表示/非表示はページごとにホストから指示する
        // (設定OFFへ切り替えても解除できるようにするため)
        await RegisterDocumentScriptsAsync(n, view);
        HookPageEvents(n, view);
        HookProcessFailure(n, view);
        HookNewWindow(view);
        view.CoreWebView2.Navigate(Discord.HomeUrl);
    }

    /// <summary>ナビゲーション・通知・権限要求の購読(失敗しても他の初期化は続ける)。</summary>
    private void HookPageEvents(int n, WpfWebView2 view)
    {
        // 通知の受け取り経路を記録(原因調査用)
        try
        {
            // トップレベル遷移はDiscord内だけに限定する。外部サイトをアプリ内に読み込ませない
            view.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (Discord.IsAllowedNavigation(e.Uri)) return;
                e.Cancel = true;
                // ユーザーが外部リンクをクリックした場合は既定ブラウザへ逃がす。
                // リダイレクト等の自動遷移はアプリ内にもブラウザにも出さない
                if (e.IsUserInitiated && TryGetExternalUri(e.Uri, out var target))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(target!.AbsoluteUri) { UseShellExecute = true });
                        Log.Write($"外部リンクをブラウザで開く(acc{n}): {target}");
                    }
                    catch (Exception ex) { Log.Write($"外部リンク起動失敗(acc{n}): {ex.Message}"); }
                    return;
                }
                Log.Write($"外部への遷移をブロック(acc{n}): {e.Uri}");
            };
            bool logged = false;
            view.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (e.IsSuccess)
                {
                    _processReloadCount[n] = 0;
                    _reloadFailureShown[n] = false;
                    // レンダラ異常後の再読み込みなど、表示できた時点で死活劣化を解除する
                    _diag.ReportOk(Feature.Page, n);
                    OnViewContentLoaded(n, view);
                    // 再読み込み後・起動後も設定どおりにする(ONの適用もOFFの解除も同じ経路で反映される)
                    await ApplyHideDownloadAsync(n, view, _settings.HideDownloadButton);
                }
                else
                {
                    // 初回読み込みに失敗したら、2枚目を待たせ続けない(オフライン起動対策)
                    if (n == 1) FirstViewLoadFailed();
                }
                if (logged) return;
                logged = true;
                try
                {
                    var r = await view.CoreWebView2.ExecuteScriptAsync(PageScripts.NotificationDiagnostic);
                    Log.Write($"通知診断(acc{n}): {r}");
                }
                catch (Exception ex) { Log.Write($"通知診断(acc{n}) 失敗: {ex.Message}"); }
            };
            view.CoreWebView2.NotificationReceived += (_, e) =>
            {
                try
                {
                    // 送信元が分かる場合はDiscordだけを受理する。分からない場合(サービスワーカー等)は
                    // トップレベル遷移の制限と併せてDiscord由来とみなす
                    if (!string.IsNullOrEmpty(e.SenderOrigin) && !Discord.IsDiscordUri(e.SenderOrigin))
                    {
                        e.Handled = true; // 外部オリジンの既定UI表示を抑止する
                        Log.Write($"通知ネイティブを破棄(acc{n}): 送信元={e.SenderOrigin}");
                        return;
                    }
                    var title = e.Notification.Title ?? "";
                    var body = e.Notification.Body ?? "";
                    e.Handled = true;
                    Log.Write($"通知ネイティブ(acc{n}): {ToSingleLine(title, 80)} / 本文{body.Length}文字");
                    var shown = HandlePageNotify(view, JsonSerializer.Serialize(new { title, body }), viaPage: false);
                    // 実際にバルーンを出したときだけ「表示した」とページへ返す
                    if (shown) { try { e.Notification.ReportShown(); } catch { } }
                }
                catch (Exception ex) { Log.Write($"通知ネイティブ処理失敗(acc{n}): {ex.Message}"); }
            };
            view.CoreWebView2.PermissionRequested += (_, e) =>
            {
                try
                {
                    Log.Write($"権限要求(acc{n}): {e.PermissionKind} {e.Uri}");
                    if (e.PermissionKind == CoreWebView2PermissionKind.Notifications
                        && Discord.IsDiscordUri(e.Uri))
                    {
                        e.State = CoreWebView2PermissionState.Allow;
                        e.Handled = true;
                    }
                    else if (!Discord.IsDiscordUri(e.Uri))
                    {
                        // 外部サイトをアプリ内に読み込ませないのと同じ境界で、権限も与えない
                        e.State = CoreWebView2PermissionState.Deny;
                        e.Handled = true;
                        Log.Write($"権限要求を拒否(acc{n}): 送信元={e.Uri}");
                    }
                }
                catch { }
            };
        }
        catch (Exception ex) { Log.Write($"通知フック設定失敗(acc{n}): {ex.Message}"); }
    }

    private void HookProcessFailure(int n, WpfWebView2 view)
    {
        view.CoreWebView2.ProcessFailed += (_, e) =>
        {
            var kind = e.ProcessFailedKind;
            Log.Write($"WebView2プロセス異常(acc{n}): {kind} reason={e.Reason} exit={e.ExitCode} desc={e.ProcessDescription}");
            if (kind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                OnBrowserProcessExited(n);
                return;
            }
            // レンダラの異常だけ再読み込みで復帰を試みる。
            // GPU等はWebView2側の自動復旧に任せ、ログだけ残す。
            if (kind != CoreWebView2ProcessFailedKind.RenderProcessExited
                && kind != CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                return;
            // 通話中でもProcessFailedは「すでに落ちた」状態なので、
            // 通話を守るために再読み込みを止める理由はない。
            view.Dispatcher.BeginInvoke(() => ReloadAfterProcessFailure(view, n, kind));
        };
    }

    private void HookNewWindow(WpfWebView2 view)
    {
        // Discord内部のポップアップ(OAuthログイン等)は既定の新規ウィンドウに任せ、
        // 外部サイトのリンクだけ既定ブラウザで開く
        view.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            var uri = e.Uri ?? "";
            if (Discord.IsDiscordUri(uri)) return;
            e.Handled = true;
            if (!TryGetExternalUri(uri, out var target))
            {
                Log.Write($"外部リンクを無視(未許可スキーム): {uri}");
                return;
            }
            try { Process.Start(new ProcessStartInfo(target!.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write($"外部リンク起動失敗: {ex.Message}"); }
        };
    }

    /// <summary>
    /// ブラウザプロセスが落ちたときの通知。共有環境なので両アカウントが使えなくなる。
    /// WebView2のコントロールは環境終了後に再利用できないため、ここでは再起動を促す。
    /// </summary>
    private void OnBrowserProcessExited(int acc)
    {
        if (_exiting || _closing) return;
        if (Interlocked.Exchange(ref _browserProcessFailureShown, 1) != 0) return;
        _diag.ReportFail(Feature.Page);
        Dispatcher.BeginInvoke(() =>
        {
            if (_exiting || _closing) return;
            try
            {
                if (!IsVisible) ShowMain();
                System.Windows.MessageBox.Show(this,
                    "WebView2のブラウザプロセスが終了しました。両方のアカウントが操作できません。\nTwinCordを再起動してください。",
                    "TwinCord", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                Log.Write($"ブラウザプロセス終了通知の表示に失敗(acc{acc}): {ex.Message}");
            }
        });
    }

    /// <summary>レンダラ異常後の再読み込み。短時間に連続したら打ち切って通知する。</summary>
    private void ReloadAfterProcessFailure(WpfWebView2 view, int acc, CoreWebView2ProcessFailedKind kind)
    {
        if (_exiting || _closing) return;
        var now = DateTime.UtcNow;
        if (now - _processReloadAt[acc] > TimeSpan.FromMinutes(2))
            _processReloadCount[acc] = 0;
        _processReloadAt[acc] = now;
        _processReloadCount[acc]++;
        _diag.ReportFail(Feature.Page, acc);

        if (_processReloadCount[acc] > 3)
        {
            if (_reloadFailureShown[acc]) return;
            _reloadFailureShown[acc] = true;
            var msg = $"アカウント{acc}のページが繰り返し異常終了しました({kind})。自動再読み込みを停止しました。TwinCordを再起動してください。";
            Log.Write(msg);
            if (_tray.IsVisible)
            {
                ShowBalloon("TwinCord", msg, WinForms.ToolTipIcon.Warning, 8000);
            }
            else
            {
                System.Windows.MessageBox.Show(msg, "TwinCord", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        try { view.CoreWebView2?.Reload(); }
        catch (Exception ex) { Log.Write($"プロセス異常後の再読み込み失敗(acc{acc}): {ex.Message}"); }
    }

    /// <summary>外部ブラウザで開いてよいURLか。任意のプロトコルハンドラ起動を防ぐ。</summary>
    private static bool TryGetExternalUri(string raw, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp
            && u.Scheme != Uri.UriSchemeHttps
            && u.Scheme != Uri.UriSchemeMailto) return false;
        // 認証情報付きURLは、表示上のホストと実際の接続先が同じでも紛らわしいので開かない
        if (!string.IsNullOrEmpty(u.UserInfo)) return false;
        uri = u;
        return true;
    }
}
