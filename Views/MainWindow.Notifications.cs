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

    // Discordページ内Ctrl+1/2・通知の受信
    private void View_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // 注入スクリプトはサブフレームでも動くため、別オリジンのiframeからのメッセージは無視する
        // (iframeの自前スクリプトによる偽装を防ぐ。トップフレーム側のガードと併用)
        // Sourceが空の場合はWebView2側がURIを特定できていないだけなので通す(通知が止まる方が害が大きい)
        var source = e.Source;
        if (!string.IsNullOrEmpty(source) && !Discord.IsDiscordUri(source))
        {
            Diagnostics.Log($"Webメッセージ破棄: source={source}");
            return;
        }
        var msg = e.TryGetWebMessageAsString();
        if (msg == "switch:1") SwitchTo(1);
        else if (msg == "switch:2") SwitchTo(2);
        else if (msg is not null && msg.StartsWith("notify:", StringComparison.Ordinal))
            HandlePageNotify(sender, msg.Substring(7));
    }

    private void HandlePageNotify(object? sender, string json)
    {
        int n = ReferenceEquals(sender, View2) ? 2 : 1;
        // マスターOFFでは記録も通知もしない
        if (!_settings.NotifyEnabled) { Diagnostics.Log($"通知抑制(acc{n}): 通知OFF"); return; }
        // 前面で操作中のときだけ「表示中」とみなす(背面にあるだけでは通知を抑えない)
        bool focused = IsVisible && WindowState != WindowState.Minimized && IsActive;
        // 格納中のみ通知する設定なら、表示中はすべて出さない
        if (focused && _settings.NotifyHiddenOnly) { Diagnostics.Log($"通知抑制(acc{n}): 表示中は出さない設定"); return; }
        // 見ている最中のアカウントは通知不要(設定で変更可)
        if (focused && _current == n && _settings.SkipViewedAccount) { Diagnostics.Log($"通知抑制(acc{n}): 表示中のアカウント"); return; }
        var acc = AccName(n);
        string title = acc, body = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("title", out var t)) title = t.GetString() ?? acc;
            if (r.TryGetProperty("body", out var b)) body = b.GetString() ?? "";
        }
        catch { }
        if (body.Length > 120) body = body[..120] + "…";
        var text = string.IsNullOrEmpty(body) ? title : title + "\n" + body;
        Diagnostics.Log($"通知({acc}): {text.Replace("\r", " ").Replace("\n", " ")}");
        _history.Add(new NotificationRecord(DateTime.Now, n, acc, text));
        Dispatcher.BeginInvoke(() =>
        {
            _tray.ShowBalloon("TwinCord - " + acc, text, WinForms.ToolTipIcon.Info, 8000, () =>
            {
                BringToFront();
                SwitchTo(n);
                return Task.CompletedTask;
            });
        });
    }


    private void SendTestNotification()
    {
        var n = _current;
        var acc = AccName(n);
        Diagnostics.Log("テスト通知送信");
        _tray.ShowBalloon("TwinCord - " + acc, "テスト通知\nこれはテストです。クリックでこのアカウントを表示します。",
            WinForms.ToolTipIcon.Info, 8000, () =>
            {
                BringToFront();
                SwitchTo(n);
                return Task.CompletedTask;
            });
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
                _tray.ShowBalloon("TwinCord", msg, WinForms.ToolTipIcon.Warning, 8000, () =>
                {
                    BringToFront();
                    return Task.CompletedTask;
                });
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
