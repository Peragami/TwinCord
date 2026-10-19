using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using TwinCord.Services;
using WinForms = System.Windows.Forms;

namespace TwinCord;

public partial class MainWindow : Window
{
    // ProcessFailedの再読み込み回数(1/2のみ使用)。短時間に連続したら打ち切る。
    private readonly int[] _processReloadCount = new int[3];
    private readonly DateTime[] _processReloadAt = new DateTime[3];
    private readonly bool[] _reloadFailureShown = new bool[3];
    private int _browserProcessFailureShown;

    private static void SetZoom(Microsoft.Web.WebView2.Wpf.WebView2 view, double factor)
    {
        try
        {
            view.ZoomFactor = AppSettings.ClampZoom(factor);
        }
        catch { }
    }

    private Task EnsureViewAsync(int n)
    {
        if (n == 1)
        {
            if (_view1Ready) return Task.CompletedTask;
            _initTask1 ??= InitViewAsync(1, View1, _settings.Zoom1).ContinueWith(t =>
            {
                if (t.IsFaulted) { _initTask1 = null; throw t.Exception!.InnerException!; }
                _view1Ready = true;
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return _initTask1;
        }
        else
        {
            if (_view2Ready) return Task.CompletedTask;
            _initTask2 ??= InitViewAsync(2, View2, _settings.Zoom2).ContinueWith(t =>
            {
                if (t.IsFaulted) { _initTask2 = null; throw t.Exception!.InnerException!; }
                _view2Ready = true;
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return _initTask2;
        }
    }

    private async Task InitViewAsync(int n, Microsoft.Web.WebView2.Wpf.WebView2 view, double zoom)
    {
        // 表示中(1枚目)の初回表示を優先するため、2枚目は1枚目の初回表示完了まで待つ。
        // 1枚目が失敗してもこの待ちはタイムアウトで解ける(常駐が止まらないように)。
        if (n == 2) await WaitForFirstViewLoadedAsync();
        var sw = Stopwatch.StartNew();
        // 1つの環境(ブラウザ/GPUプロセスを共有)に、アカウント別プロファイルを載せる。
        // クッキー/localStorageはプロファイルごとに分離されるため、ログインも通知も両方そのまま動く。
        var env = await GetSharedEnvironmentAsync();
        var envMs = sw.ElapsedMilliseconds;
        var options = env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = ProfileName(n);
        await view.EnsureCoreWebView2Async(env, options);
        var ctlMs = sw.ElapsedMilliseconds;
        await ConfigureViewAsync(n, view, zoom);
        Log.Write($"acc{n} 初期化: 環境={envMs}ms コントローラ={ctlMs - envMs}ms 設定={sw.ElapsedMilliseconds - ctlMs}ms 累計={sw.ElapsedMilliseconds}ms");
    }

    /// <summary>1枚目の初回表示完了を待つ。上限を超えたら諦めて2枚目を始める。</summary>
    private async Task WaitForFirstViewLoadedAsync()
    {
        var t = _firstViewLoaded.Task;
        if (t.IsCompleted) return;
        if (await Task.WhenAny(t, Task.Delay(AppConstants.SecondViewWait)) != t)
            Log.Write($"acc1の初回表示待ちがタイムアウト({AppConstants.SecondViewWait.TotalSeconds}秒)したためacc2を開始");
    }

    /// <summary>1枚目の初回読み込みが失敗したときに、2枚目の開始条件を解く(待ち続けない)。</summary>
    private void FirstViewLoadFailed()
    {
        if (_firstViewLoaded.Task.IsCompleted) return;
        Log.Write("acc1の初回読み込みに失敗したためacc2を開始");
        _firstViewLoaded.TrySetResult();
    }

    /// <summary>ページ生成時に走るスクリプトを登録する。失敗はログと劣化判定(機能=page)へ反映する。</summary>
    private async Task RegisterDocumentScriptAsync(int n, Microsoft.Web.WebView2.Wpf.WebView2 view,
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
    private static async Task ApplyHideDownloadAsync(int n, Microsoft.Web.WebView2.Wpf.WebView2 view, bool hide)
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

    private static string EnsureDataFolder(string name)
    {
        var dir = Path.Combine(AppSettings.DataFolder(), name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>共有環境は1つだけ作る(2つのViewから呼ばれても二重生成しない)。</summary>
    private async Task<CoreWebView2Environment> GetSharedEnvironmentAsync()
    {
        if (_sharedEnvTask is null) _sharedEnvTask = CreateSharedEnvironmentAsync();
        try
        {
            return await _sharedEnvTask;
        }
        catch
        {
            // 失敗したタスクを残すと再試行しても同じ失敗を返すため、作り直せるようにする
            _sharedEnvTask = null;
            throw;
        }
    }

    private async Task<CoreWebView2Environment> CreateSharedEnvironmentAsync()
    {
        var folder = EnsureDataFolder(AppConstants.WebViewDataFolder);
        // 初回(プロファイル新規)はキャッシュが無く読み込みが遅い。原因切り分け用に記録する。
        bool cold = !Directory.Exists(Path.Combine(folder, "EBWebView"));
        var sw = Stopwatch.StartNew();
        var env = await CoreWebView2Environment.CreateAsync(null, folder);
        Log.Write($"共有環境を作成: {sw.ElapsedMilliseconds}ms{(cold ? "(初回/キャッシュ無し)" : "")}");
        _sharedEnv = env;
        return env;
    }

    private static string ProfileName(int n) => "acc" + n;

    /// <summary>両アカウント共通の初期化。</summary>
    private async Task ConfigureViewAsync(int n, Microsoft.Web.WebView2.Wpf.WebView2 view, double zoom)
    {
        // メモリ抑制(Low)は初回読み込みが終わってから掛ける(読み込み前のLowは初回表示を遅くするため)。
        // 適用は OnViewContentLoaded → PageWatchdog.SetViewLoaded 経由。
        // リンクホバー時の左下URLプレビュー(ステータスバー)を出さない
        try { view.CoreWebView2.Settings.IsStatusBarEnabled = false; } catch { }
        SetZoom(view, zoom);
        // ホットキー・通知フック・ダウンロードボタン隠しは互いに独立なので、登録はまとめて投げる(直列待ちを減らす)。
        // ダウンロードボタン隠しは設定に関わらず登録し、表示/非表示はページごとにホストから指示する
        // (設定OFFへ切り替えても解除できるようにするため)
        await Task.WhenAll(
            RegisterDocumentScriptAsync(n, view, PageScripts.Hotkey, Feature.HotkeyPage, "キー操作"),
            RegisterDocumentScriptAsync(n, view, PageScripts.Notify, null, "通知"),
            RegisterDocumentScriptAsync(n, view, PageScripts.HideDownload, null, "ダウンロードボタン"));
        // 通知の受け取り経路を記録(原因調査用)
        try
        {
            bool logged = false;
            view.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (e.IsSuccess)
                {
                    _processReloadCount[n] = 0;
                    _reloadFailureShown[n] = false;
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
                    // 念のため: JSフックを通らない通知もTwinCordのバルーンで出す
                    if (!Discord.IsDiscordUri(e.SenderOrigin)) return;
                    var title = e.Notification.Title ?? "";
                    var body = e.Notification.Body ?? "";
                    e.Handled = true;
                    Log.Write($"通知ネイティブ(acc{n}): {title} / 本文{body.Length}文字");
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
                }
                catch { }
            };
        }
        catch (Exception ex) { Log.Write($"通知フック設定失敗(acc{n}): {ex.Message}"); }
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
        view.CoreWebView2.Navigate(Discord.HomeUrl);
    }

    /// <summary>
    /// Discordの初回読み込み完了時に1度だけ呼ばれる。メモリ抑制の適用開始と、
    /// 2枚目の開始条件(1枚目の完了)の通知を行う。
    /// </summary>
    private void OnViewContentLoaded(int n, Microsoft.Web.WebView2.Wpf.WebView2 view)
    {
        if (_viewContentLoaded[n]) return;
        // 起動直後のabout:blank等を「読み込み完了」と数えない(メモリ抑制を早く掛けないため)
        string source;
        try { source = view.CoreWebView2?.Source ?? ""; }
        catch { source = ""; }
        if (!Discord.IsDiscordUri(source)) return;

        _viewContentLoaded[n] = true;
        Log.Write($"acc{n} 初回表示完了: 起動から{ElapsedMs()}ms ({source})");
        _watchdog?.SetViewLoaded(n);
        if (n == 1) _firstViewLoaded.TrySetResult();
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
    private void ReloadAfterProcessFailure(
        Microsoft.Web.WebView2.Wpf.WebView2 view, int acc, CoreWebView2ProcessFailedKind kind)
    {
        if (_exiting || _closing) return;
        var now = DateTime.UtcNow;
        if (now - _processReloadAt[acc] > TimeSpan.FromMinutes(2))
            _processReloadCount[acc] = 0;
        _processReloadAt[acc] = now;
        _processReloadCount[acc]++;
        _diag.ReportFail(Feature.Page);

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

    private async Task<string> CheckViewAsync(
        string label, Microsoft.Web.WebView2.Wpf.WebView2 view, bool ready, double zoom)
    {
        if (!ready) return $"{label}: 未起動";
        try
        {
            var core = view.CoreWebView2;
            if (core is null) return $"{label}: 未起動";
            var sw = Stopwatch.StartNew();
            var t = core.ExecuteScriptAsync(PageScripts.ReadyState);
            if (await Task.WhenAny(t, Task.Delay(AppConstants.DiagnosticsEvalTimeoutMs)) != t)
                return $"{label}: 応答なし(タイムアウト)";
            var state = await t;
            sw.Stop();
            int accNo = label == "Acc1" ? 1 : 2;
            string badge = _watchdog is not null
                ? await _watchdog.ProbeBadgeAsync(accNo)
                : "判定不能";
            string mem;
            try
            {
                var env = _sharedEnv;
                if (env is null) mem = "計測前";
                else
                {
                    long bytes = 0;
                    foreach (var p in env.GetProcessInfos())
                    {
                        try
                        {
                            using var proc = Process.GetProcessById(p.ProcessId);
                            bytes += proc.PrivateMemorySize64;
                        }
                        catch { }
                    }
                    // プロセスは2アカウント共通なので、同じ数字を2回出さない
                    mem = accNo == 1 ? $"{bytes / 1024 / 1024}MB(2アカウント合算)" : "同上(合算)";
                }
            }
            catch
            {
                mem = "取得失敗";
            }
            bool voice = _watchdog?.InVoice(accNo) == true;
            string avatar = (_watchdog?.HasAvatar(accNo) == true) ? "取得済" : "未取得";
            return $"{label}: readyState={state}({sw.ElapsedMilliseconds}ms) メモリ={mem} 通話={(voice ? "通話中" : "なし")} バッジテスト={badge} 拡大率={(int)(zoom * 100)}% アバター={avatar}";
        }
        catch (Exception ex)
        {
            return $"{label}: エラー({ex.GetType().Name})";
        }
    }
}
