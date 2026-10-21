using System.Diagnostics;
using System.Text;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using TwinCord.Services;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;

namespace TwinCord;

public partial class MainWindow : Window
{
    // ---- 自己診断 ----

    private async Task<string> RunDiagnosticsAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"TwinCord v{Diagnostics.AppVersion()} / {DateTime.Now:yyyy-MM-dd HH:mm}");
        try
        {
            using var p = Process.GetCurrentProcess();
            long mb = p.PrivateMemorySize64 / 1024 / 1024;
            sb.AppendLine($"プロセス全体: {mb}MB");
        }
        catch { }
        sb.AppendLine($"2アカウント共有: {(_sharedEnv is not null ? "有効" : "未初期化")}");
        try
        {
            sb.AppendLine("WebView2: " + CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch
        {
            sb.AppendLine("WebView2: 取得失敗(未インストールの疑い)");
        }
        sb.AppendLine(await CheckViewAsync(1, View1, _settings.Zoom1));
        sb.AppendLine(await CheckViewAsync(2, View2, _settings.Zoom2));
        var bad = _diag.DegradedDetails();
        sb.Append("劣化中の機能: ");
        sb.AppendLine(bad.Count == 0
            ? "なし"
            : string.Join(", ", bad.Select(x => Diagnostics.DisplayName(x.Feature, x.Account))));
        var report = sb.ToString();
        Log.Write("自己診断:\n" + report);
        return report;
    }

    private async Task<string> CheckViewAsync(int n, WpfWebView2 view, double zoom)
    {
        var label = AppConstants.DefaultAccountName(n);
        if (!_viewReady[n]) return $"{label}: 未起動";
        try
        {
            var core = view.CoreWebView2;
            if (core is null) return $"{label}: 未起動";
            var sw = Stopwatch.StartNew();
            var t = core.ExecuteScriptAsync(PageScripts.ReadyState);
            if (await Task.WhenAny(t, Task.Delay(AppConstants.DiagnosticsEvalTimeoutMs)) != t)
            {
                // 打ち切った後もCOM側の処理は続くため、例外だけは観測しておく
                _ = t.ContinueWith(x => { _ = x.Exception; }, TaskScheduler.Default);
                return $"{label}: 応答なし(タイムアウト)";
            }
            var state = await t;
            sw.Stop();
            string badge = _watchdog is not null
                ? await _watchdog.ProbeBadgeAsync(n)
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
                    mem = n == 1 ? $"{bytes / 1024 / 1024}MB(2アカウント合算)" : "同上(合算)";
                }
            }
            catch
            {
                mem = "取得失敗";
            }
            bool voice = _watchdog?.InVoice(n) == true;
            string avatar = (_watchdog?.HasAvatar(n) == true) ? "取得済" : "未取得";
            return $"{label}: readyState={state}({sw.ElapsedMilliseconds}ms) メモリ={mem} 通話={(voice ? "通話中" : "なし")} バッジテスト={badge} 拡大率={(int)(zoom * 100)}% アバター={avatar}";
        }
        catch (Exception ex)
        {
            return $"{label}: エラー({ex.GetType().Name})";
        }
    }
}
