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
        // 1つの環境(ブラウザ/GPUプロセスを共有)に、アカウント別プロファイルを載せる。
        // クッキー/localStorageはプロファイルごとに分離されるため、ログインも通知も両方そのまま動く。
        var env = await GetSharedEnvironmentAsync();
        var options = env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = ProfileName(n);
        await view.EnsureCoreWebView2Async(env, options);
        await ConfigureViewAsync(n, view, zoom);
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
        var env = await CoreWebView2Environment.CreateAsync(null, EnsureDataFolder(AppConstants.WebViewDataFolder));
        _sharedEnv = env;
        return env;
    }

    private static string ProfileName(int n) => "acc" + n;

    /// <summary>両アカウント共通の初期化。</summary>
    private async Task ConfigureViewAsync(int n, Microsoft.Web.WebView2.Wpf.WebView2 view, double zoom)
    {
        // 読み込み中から非表示側は絞っておく(起動ピークの抑制。表示切替時にRefreshされる)
        PageWatchdog.SetMemoryLevel(view, n != _current);
        // リンクホバー時の左下URLプレビュー(ステータスバー)を出さない
        try { view.CoreWebView2.Settings.IsStatusBarEnabled = false; } catch { }
        SetZoom(view, zoom);
        try
        {
            await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(PageScripts.Hotkey);
            _diag.ReportOk(Feature.Hotkey);
        }
        catch
        {
            _diag.ReportFail(Feature.Hotkey);
        }
        try
        {
            var scriptId = await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(PageScripts.Notify);
            Diagnostics.Log($"通知スクリプト登録(acc{n}): {scriptId}");
        }
        catch (Exception ex) { Diagnostics.Log($"通知スクリプト登録失敗(acc{n}): {ex.Message}"); }
        // 通知の受け取り経路を記録(原因調査用)
        try
        {
            bool logged = false;
            view.CoreWebView2.NavigationCompleted += async (_, __) =>
            {
                if (logged) return;
                logged = true;
                try
                {
                    var r = await view.CoreWebView2.ExecuteScriptAsync(PageScripts.NotificationDiagnostic);
                    Diagnostics.Log($"通知診断(acc{n}): {r}");
                }
                catch (Exception ex) { Diagnostics.Log($"通知診断(acc{n}) 失敗: {ex.Message}"); }
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
                    try { e.Notification.ReportShown(); } catch { }
                    Diagnostics.Log($"通知ネイティブ(acc{n}): {title} / {body}");
                    HandlePageNotify(view, JsonSerializer.Serialize(new { title, body }));
                }
                catch (Exception ex) { Diagnostics.Log($"通知ネイティブ処理失敗(acc{n}): {ex.Message}"); }
            };
            view.CoreWebView2.PermissionRequested += (_, e) =>
            {
                try
                {
                    Diagnostics.Log($"権限要求(acc{n}): {e.PermissionKind} {e.Uri}");
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
        catch (Exception ex) { Diagnostics.Log($"通知フック設定失敗(acc{n}): {ex.Message}"); }
        if (_settings.HideDownloadButton)
        {
            try
            {
                await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(PageScripts.HideDownload);
            }
            catch { }
        }
        view.CoreWebView2.ProcessFailed += (_, _) => view.Dispatcher.BeginInvoke(() =>
        {
            // 通話中は再読み込みしない(切断防止)
            if (ReferenceEquals(view, View1) ? _watchdog?.InVoice(1) == true : _watchdog?.InVoice(2) == true) return;
            _diag.ReportFail(Feature.Page);
            try { view.CoreWebView2?.Reload(); } catch { }
        });
        // Discord内部のポップアップ(OAuthログイン等)は既定の新規ウィンドウに任せ、
        // 外部サイトのリンクだけ既定ブラウザで開く
        view.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            var uri = e.Uri ?? "";
            if (Discord.IsDiscordUri(uri)) return;
            e.Handled = true;
            if (string.IsNullOrEmpty(uri)) return;
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch { }
        };
        view.CoreWebView2.Navigate(Discord.HomeUrl);
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
            if (await Task.WhenAny(t, Task.Delay(8000)) != t)
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
