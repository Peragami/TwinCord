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
    // JS経路とネイティブ経路が同じ通知を二重に出すのを抑える(UIスレッドのみで使用)
    private readonly NotificationDedup _dedup = new(TimeSpan.FromSeconds(2));
    // 「通知抑制(acc1): 通知OFF」のようなログは理由ごとに1回だけ出す(通知が多いとログが埋まるため)
    private readonly HashSet<string> _suppressLogged = new();

    // Discordページ内Ctrl+1/2・通知の受信
    private void View_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // 注入スクリプトはサブフレームでも動くため、別オリジンのiframeからのメッセージは無視する
        // (iframeの自前スクリプトによる偽装を防ぐ。トップフレーム側のガードと併用)
        // Sourceが空でもアカウント切替は通すが、通知は送信元不明として捨てる(偽装防止)
        var source = e.Source;
        bool sourceKnown = !string.IsNullOrEmpty(source);
        if (sourceKnown && !Discord.IsDiscordUri(source))
        {
            Diagnostics.Log($"Webメッセージ破棄: source={source}");
            return;
        }
        string? msg;
        try
        {
            // 文字列以外(オブジェクト等)を送られると例外になる
            msg = e.TryGetWebMessageAsString();
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"Webメッセージを無視(文字列以外): {ex.GetType().Name}");
            return;
        }
        if (msg == "switch:1") SwitchTo(1);
        else if (msg == "switch:2") SwitchTo(2);
        else if (msg is not null && msg.StartsWith("notify:", StringComparison.Ordinal))
        {
            // chrome.webview.postMessageはサブフレームからも送れる。
            // 送信元が分からない通知は偽装の可能性があるため受理しない。
            if (!sourceKnown)
            {
                Diagnostics.Log("Webメッセージ破棄: 送信元不明の通知");
                return;
            }
            HandlePageNotify(sender, msg.Substring(7), viaPage: true);
        }
    }

    /// <summary>通知を表示した(または履歴に積んだ)ならtrue。抑制・重複ならfalse。</summary>
    private bool HandlePageNotify(object? sender, string json, bool viaPage)
    {
        int n = ReferenceEquals(sender, View2) ? 2 : 1;
        // マスターOFFでは記録も通知もしない
        if (!_settings.NotifyEnabled) { LogSuppressed(n, "通知OFF"); return false; }
        // 前面で操作中のときだけ「表示中」とみなす(背面にあるだけでは通知を抑えない)
        bool focused = IsVisible && WindowState != WindowState.Minimized && IsActive;
        // 格納中のみ通知する設定なら、表示中はすべて出さない
        if (focused && _settings.NotifyHiddenOnly) { LogSuppressed(n, "表示中は出さない設定"); return false; }
        // 見ている最中のアカウントは通知不要(設定で変更可)
        if (focused && _current == n && _settings.SkipViewedAccount) { LogSuppressed(n, "表示中のアカウント"); return false; }
        var acc = AccName(n);
        string title = acc, body = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("title", out var t)) title = t.GetString() ?? acc;
            if (r.TryGetProperty("body", out var b)) body = b.GetString() ?? "";
        }
        catch (Exception ex)
        {
            // JS側は常にJSONを送る。壊れたペイロードは表示しない。
            Diagnostics.Log($"通知データ不正(acc{n}): {ex.Message}");
            return false;
        }
        title = ToSingleLine(title, 80);
        if (title.Length == 0) title = acc;
        body = TruncateSafe(body, 120);

        // 同じ通知がJS経路とネイティブ経路の両方から来た場合の重複排除
        var origin = viaPage ? NotificationDedup.Origin.Page : NotificationDedup.Origin.Native;
        if (_dedup.IsDuplicate(n, title, body, origin, DateTime.UtcNow))
        {
            LogSuppressed(n, $"重複({origin})");
            return false;
        }

        var text = string.IsNullOrEmpty(body) ? title : title + "\n" + body;
        // 本文そのものは履歴に残るため、ログは「本文N文字」に留める(診断ログが長く残るため)
        Diagnostics.Log($"通知({acc}): {title} / 本文{body.Length}文字");
        _history.Add(new NotificationRecord(DateTime.Now, n, acc, text));
        Dispatcher.BeginInvoke(() => ShowAccountBalloon(n, "TwinCord - " + acc, text, 8000));
        return true;
    }

    /// <summary>抑止ログは理由ごとに1回だけ出す(通知が多い日にログを埋めない)。</summary>
    private void LogSuppressed(int n, string reason)
    {
        if (_suppressLogged.Add(n + ":" + reason))
            Diagnostics.Log($"通知抑制(acc{n}): {reason}");
    }

    /// <summary>タイトル用に制御文字を空白へ置換し、最大長で切る。</summary>
    private static string ToSingleLine(string? raw, int max)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(Math.Min(raw.Length, max));
        foreach (var ch in raw)
        {
            if (sb.Length >= max) break;
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        }
        return sb.ToString().Trim();
    }

    /// <summary>サロゲートペアを分割しないように最大長で切る。</summary>
    private static string TruncateSafe(string s, int max)
    {
        if (s.Length <= max) return s;
        var cut = max;
        if (cut > 0 && char.IsHighSurrogate(s[cut - 1])) cut--;
        return s[..cut] + "…";
    }


    private void SendTestNotification()
    {
        var n = _current;
        var acc = AccName(n);
        Diagnostics.Log("テスト通知送信");
        ShowAccountBalloon(n, "TwinCord - " + acc, "テスト通知\nこれはテストです。クリックでこのアカウントを表示します。", 8000);
    }


    private void OpenHistory()
    {
        if (_historyWindow?.IsLoaded == true)
        {
            _historyWindow.Activate();
            return;
        }
        var w = new HistoryWindow(new List<NotificationRecord>(_history.Items), n =>
        {
            BringToFront();
            SwitchTo(n);
        });
        if (IsVisible) w.Owner = this;
        w.Closed += (_, _) => _historyWindow = null;
        _historyWindow = w;
        w.Show();
    }


    // ---- 劣化通知・自己診断 ----

    private void OnFeatureDegraded(Feature key)
    {
        Dispatcher.BeginInvoke(() =>
        {
            UpdateGearAlert();
            var name = Diagnostics.DisplayName(key);
            var msg = $"「{name}」が動作しなくなりました。Discord側の更新により TwinCord v{Diagnostics.AppVersion()} が古くなった可能性があります。設定(歯車)→アプリについて→自己診断で詳細を確認してください。";
            Diagnostics.Log("通知: " + msg.Replace(Environment.NewLine, " "));
            if (_tray.IsVisible)
                ShowBalloon("TwinCord", msg, WinForms.ToolTipIcon.Warning, 8000);
            else
                System.Windows.MessageBox.Show(msg, "TwinCord", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    private void UpdateGearAlert()
    {
        var bad = _diag.DegradedKeys().Count > 0;
        SettingsButton.Foreground = bad ? AlertBrush : NormalRailBrush;
        SettingsButton.ToolTip = bad ? "設定(要確認: 動作しなくなった機能があります)" : "設定";
    }

}
