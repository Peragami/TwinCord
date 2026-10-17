using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using TwinCord.Services;
using WinForms = System.Windows.Forms;

namespace TwinCord;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // Discordページ内でCtrl+1/2を拾ってホストに通知するJS(トップフレームのみ)
    private const string HotkeyScript =
        "(function(){try{if(window.top!==window)return;}catch(e){return;}window.addEventListener('keydown',function(e){if(e.ctrlKey&&!e.shiftKey&&!e.altKey&&(e.key==='1'||e.key==='2')){var t=e.target;if(t){var tn=(t.tagName||'').toUpperCase();if(tn==='INPUT'||tn==='TEXTAREA')return;if(t.closest&&t.closest('[contenteditable=\"true\"]'))return;}chrome.webview.postMessage('switch:'+e.key);}});})();";

    // Discordのデスクトップ通知を横取りしてホストに転送するJS(DM・メンション等すべて、トップフレームのみ)
    // 本物のNotificationは作らない(二重通知防止のためダミーを返す)
    private const string NotifyScript =
        "(function(){try{if(window.top!==window)return;var stub={close:function(){},addEventListener:function(){},removeEventListener:function(){}};function Hooked(t,o){try{chrome.webview.postMessage('notify:'+JSON.stringify({title:t||'',body:(o&&o.body)||''}));}catch(e){}return stub;}Hooked.permission='granted';Hooked.requestPermission=function(){return Promise.resolve('granted');};window.Notification=Hooked;}catch(e){}})();";

    // サーバーリストの「アプリをダウンロード」ボタンを隠すJS(ベストエフォート)
    // 実体はdiv系要素のため、短文テキスト一致＋属性判定で探す(祖先巻き込み防止に文字数制限)
    private const string HideDownloadScript =
        "(function(){try{if(window.top!==window)return;}catch(e){return;}try{var css='a[href=\"/download\"],a[href^=\"/download?\"],a[href*=\"discord.com/download\"]{display:none!important}';var st=document.createElement('style');st.textContent=css;(document.head||document.documentElement).appendChild(st);}catch(e){}function isDl(el){try{var g=function(n){try{return el.getAttribute(n)||'';}catch(x){return '';};};var al=g('aria-label')+'|'+g('title');if(/download/i.test(al)&&/apps?/i.test(al))return true;if(al.indexOf('ダウンロード')>=0&&al.indexOf('アプリ')>=0)return true;var t='';try{t=(el.textContent||'').trim();}catch(x){}if(t.length>0&&t.length<=30){if(t==='アプリをダウンロード')return true;if(/download apps?/i.test(t))return true;}return false;}catch(e){return false;}}function kill(){try{var els=document.querySelectorAll('nav div,div[role=\"button\"],div[role=\"treeitem\"],div[tabindex],button,a');for(var i=0;i<els.length;i++){if(isDl(els[i])){els[i].style.setProperty('display','none','important');}}}catch(e){}}try{kill();setInterval(kill,3000);}catch(e){}})();";

    private int _current = 1;

    private AppSettings _settings = AppSettings.Load();

    private bool _view1Ready;
    private bool _view2Ready;
    private Task? _initTask1;
    private Task? _initTask2;
    private CoreWebView2Environment? _env1;
    private CoreWebView2Environment? _env2;

    private readonly TrayManager _tray = new();
    private readonly HistoryStore _history = new();
    private readonly UpdateService _updates;
    private WindowPlacementTracker? _placement;
    private PageWatchdog? _watchdog;
    private SettingsWindow? _settingsWindow;
    private HistoryWindow? _historyWindow;
    private UpdateDialog? _updateDialog;
    private bool _exiting;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly System.Windows.Media.Brush SelectedBrush =
        new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#5865F2"));
    private static readonly System.Windows.Media.Brush NormalBrush =
        new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2B2D31"));
    private static readonly System.Windows.Media.Brush AlertBrush =
        new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F0A020"));
    private static readonly System.Windows.Media.Brush NormalRailBrush =
        new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#B5BAC1"));

    private readonly Diagnostics _diag = new();
    private HwndSource? _hotkeySource;

    public MainWindow()
    {
        InitializeComponent();
        WindowIcon.Set(this);
        _updates = new UpdateService(_http, Diagnostics.AppVersion, () => _exiting);
        _placement = new WindowPlacementTracker(this, () => _settings);
        _placement.Restore();
        _watchdog = new PageWatchdog(
            new PageWatchdog.AccountSlot
            {
                View = View1,
                IsReady = () => _view1Ready,
                ActionButton = Acc1Button,
                AccountName = () => _settings.Acc1Name,
                Badge = Badge1,
                BadgeText = Badge1Text,
            },
            new PageWatchdog.AccountSlot
            {
                View = View2,
                IsReady = () => _view2Ready,
                ActionButton = Acc2Button,
                AccountName = () => _settings.Acc2Name,
                Badge = Badge2,
                BadgeText = Badge2Text,
            },
            () => _settings, _diag, () => _current);
        _tray.ShowMainRequested += ShowMain;
        _tray.HistoryRequested += OpenHistory;
        _tray.SettingsRequested += OpenSettings;
        _tray.UpdateCheckRequested += () => _ = CheckUpdateFromTrayAsync();
        _tray.ExitRequested += () => { _exiting = true; Close(); };
        if (_settings.StartMinimized) WindowState = WindowState.Minimized;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
        SourceInitialized += MainWindow_SourceInitialized;
        _diag.Degraded += OnFeatureDegraded;
        View1.WebMessageReceived += View_WebMessageReceived;
        View2.WebMessageReceived += View_WebMessageReceived;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // Win11ダークタイトルバー
        }
        catch { }
        try
        {
            // グローバルホットキー: Ctrl+Alt+1/2=切替、Ctrl+Alt+M=表示中ミュート
            // MOD_NOREPEAT(0x4000)でキー長押しの連続発火を防ぐ
            var hwnd = new WindowInteropHelper(this).Handle;
            _hotkeySource = HwndSource.FromHwnd(hwnd);
            _hotkeySource?.AddHook(WndProcHook);
            const uint mod = 0x0001 | 0x0002 | 0x4000; // Alt | Ctrl | MOD_NOREPEAT
            if (!RegisterHotKey(hwnd, 1, mod, 0x31))
                ReportHotkeyFailure("Ctrl+Alt+1");
            if (!RegisterHotKey(hwnd, 2, mod, 0x32))
                ReportHotkeyFailure("Ctrl+Alt+2");
            if (!RegisterHotKey(hwnd, 3, mod, 0x4D))
                ReportHotkeyFailure("Ctrl+Alt+M");
        }
        catch { }
    }

    private void ReportHotkeyFailure(string keys)
    {
        Diagnostics.Log($"グローバルホットキー登録失敗: {keys} (他アプリが使用中の可能性)");
        _diag.ReportFail("hotkey");
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312)
        {
            switch (wParam.ToInt32())
            {
                case 1: SwitchTo(1); handled = true; break;
                case 2: SwitchTo(2); handled = true; break;
                case 3: ToggleMute(_current); handled = true; break;
            }
        }
        return IntPtr.Zero;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Diagnostics.Log($"TwinCord v{Diagnostics.AppVersion()} 起動");
            RefreshRail();
            _history.Load();
            ApplySettings();
            _watchdog?.Start();
            // 逐次起動(起動ピークを抑えつつ両方常駐)
            await EnsureViewAsync(1);
            RefreshRail();
            await EnsureViewAsync(2);
            SwitchTo(_current);
            if (_watchdog is not null)
            {
                _ = _watchdog.TryUpdateAvatarAsync(1);
                _ = _watchdog.TryUpdateAvatarAsync(2);
            }
            if (_settings.AutoUpdateCheck)
                _ = CheckUpdateOnStartupAsync();
        }
        catch (Exception ex)
        {
            var log = Path.Combine(AppSettings.DataFolder(), "startup.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            await File.WriteAllTextAsync(log, ex.ToString());
            System.Windows.MessageBox.Show(ex.Message, "TwinCord init error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---- アカウント切替 ----

    private string AccName(int n) => n == 1 ? _settings.Acc1Name : _settings.Acc2Name;

    private void Acc1Button_Click(object sender, RoutedEventArgs e) => SwitchTo(1);
    private void Acc2Button_Click(object sender, RoutedEventArgs e) => SwitchTo(2);
    private void Acc1Label_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => SwitchTo(1);
    private void Acc2Label_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => SwitchTo(2);

    private async void SwitchTo(int n)
    {
        _current = n;
        if (n == 1) Badge1.Visibility = Visibility.Collapsed;
        else Badge2.Visibility = Visibility.Collapsed;
        RefreshRail();
        try
        {
            await EnsureViewAsync(n);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "TwinCord init error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (_current != n) return;
        RefreshRail();
        // 通話中は両方とも抑制しない(音声優先)。通常時は非表示側のヒープを絞る
        _watchdog?.RefreshMemoryLevels();
    }

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
            _initTask1 ??= InitViewAsync(1, View1, "acc1", _settings.Zoom1).ContinueWith(t =>
            {
                if (t.IsFaulted) { _initTask1 = null; throw t.Exception!.InnerException!; }
                _view1Ready = true;
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return _initTask1;
        }
        else
        {
            if (_view2Ready) return Task.CompletedTask;
            _initTask2 ??= InitViewAsync(2, View2, "acc2", _settings.Zoom2).ContinueWith(t =>
            {
                if (t.IsFaulted) { _initTask2 = null; throw t.Exception!.InnerException!; }
                _view2Ready = true;
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return _initTask2;
        }
    }

    private async Task InitViewAsync(int n, Microsoft.Web.WebView2.Wpf.WebView2 view, string folder, double zoom)
    {
        var userDataFolder = Path.Combine(AppSettings.DataFolder(), folder);
        Directory.CreateDirectory(userDataFolder);
        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
        if (n == 1) _env1 = env; else _env2 = env;
        await view.EnsureCoreWebView2Async(env);
        // 読み込み中から非表示側は絞っておく(起動ピークの抑制。表示切替時にRefreshされる)
        PageWatchdog.SetMemoryLevel(view, n != _current);
        // リンクホバー時の左下URLプレビュー(ステータスバー)を出さない
        try { view.CoreWebView2.Settings.IsStatusBarEnabled = false; } catch { }
        SetZoom(view, zoom);
        try
        {
            await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(HotkeyScript);
            _diag.ReportOk("hotkey");
        }
        catch
        {
            _diag.ReportFail("hotkey");
        }
        try
        {
            await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(NotifyScript);
        }
        catch { }
        if (_settings.HideDownloadButton)
        {
            try
            {
                await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(HideDownloadScript);
            }
            catch { }
        }
        view.CoreWebView2.ProcessFailed += (_, _) => view.Dispatcher.BeginInvoke(() =>
        {
            // 通話中は再読み込みしない(切断防止)
            if (ReferenceEquals(view, View1) ? _watchdog?.InVoice(1) == true : _watchdog?.InVoice(2) == true) return;
            _diag.ReportFail("page");
            try { view.CoreWebView2?.Reload(); } catch { }
        });
        // Discord内部のポップアップ(OAuthログイン等)は既定の新規ウィンドウに任せ、
        // 外部サイトのリンクだけ既定ブラウザで開く
        view.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            var uri = e.Uri ?? "";
            if (uri.Contains("discord.com", StringComparison.OrdinalIgnoreCase)) return;
            e.Handled = true;
            if (string.IsNullOrEmpty(uri)) return;
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch { }
        };
        view.CoreWebView2.Navigate("https://discord.com/channels/@me");
    }

    // Discordページ内Ctrl+1/2・通知の受信
    private void View_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // 注入スクリプトはサブフレームでも動くため、別オリジンのiframeからのメッセージは無視する
        // (iframeの自前スクリプトによる偽装を防ぐ。トップフレーム側のガードと併用)
        // Sourceが空の場合はWebView2側がURIを特定できていないだけなので通す(通知が止まる方が害が大きい)
        var source = e.Source;
        if (!string.IsNullOrEmpty(source)
            && !source.Contains("discord.com", StringComparison.OrdinalIgnoreCase)) return;
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
        if (!_settings.NotifyEnabled) return;
        // 前面で操作中のときだけ「表示中」とみなす(背面にあるだけでは通知を抑えない)
        bool focused = IsVisible && WindowState != WindowState.Minimized && IsActive;
        // 格納中のみ通知する設定なら、表示中はすべて出さない
        if (focused && _settings.NotifyHiddenOnly) return;
        // 見ている最中のアカウントは通知不要(設定で変更可)
        if (focused && _current == n && _settings.SkipViewedAccount) return;
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
        Diagnostics.Log($"通知({acc}): {text.Replace(Environment.NewLine, " ")}");
        _history.Add(new HistoryWindow.Item(DateTime.Now, n, acc, text));
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

    // ---- レール表示・改名 ----

    private void RefreshRail()
    {
        _watchdog?.RefreshAccountIcons();
        Acc1Button.ToolTip = _settings.Acc1Name + (IsMuted(View1) ? " (ミュート中)" : "");
        Acc1Label.Text = _settings.Acc1Name;
        Acc2Button.ToolTip = _settings.Acc2Name + (IsMuted(View2) ? " (ミュート中)" : "");
        Acc2Label.Text = _settings.Acc2Name;
        View1.Visibility = _current == 1 && _view1Ready ? Visibility.Visible : Visibility.Collapsed;
        View2.Visibility = _current == 2 && _view2Ready ? Visibility.Visible : Visibility.Collapsed;
        Pill1.Visibility = _current == 1 ? Visibility.Visible : Visibility.Collapsed;
        Pill2.Visibility = _current == 2 ? Visibility.Visible : Visibility.Collapsed;
        Acc1Button.Background = _current == 1 ? SelectedBrush : NormalBrush;
        Acc2Button.Background = _current == 2 ? SelectedBrush : NormalBrush;
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

    private void RenameAcc1_Click(object sender, RoutedEventArgs e) => RenameAcc(1);

    private void RenameAcc2_Click(object sender, RoutedEventArgs e) => RenameAcc(2);

    private void MuteAcc1_Click(object sender, RoutedEventArgs e) => ToggleMute(1);

    private void MuteAcc2_Click(object sender, RoutedEventArgs e) => ToggleMute(2);

    private void ToggleMute(int n)
    {
        try
        {
            var view = n == 1 ? View1 : View2;
            var c = view.CoreWebView2;
            if (c is null) return;
            c.IsMuted = !c.IsMuted;
            Diagnostics.Log($"{AccName(n)} ミュート{(c.IsMuted ? "ON" : "OFF")}");
            RefreshRail();
        }
        catch { }
    }

    private static bool IsMuted(Microsoft.Web.WebView2.Wpf.WebView2 view)
    {
        try { return view.CoreWebView2?.IsMuted == true; }
        catch { return false; }
    }

    private void Acc_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender == Acc1Button) RenameAcc(1);
        else if (sender == Acc2Button) RenameAcc(2);
    }

    private void RenameAcc(int n)
    {
        var next = PromptName(AccName(n));
        if (next is null) return;
        if (n == 1) _settings.Acc1Name = next; else _settings.Acc2Name = next;
        _settings.Save();
        SaveNames();
        RefreshRail();
    }

    private string? PromptName(string current)
    {
        var dlg = new Window
        {
            Title = "名前の変更",
            Width = 300,
            Height = 150,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
        };
        var panel = new StackPanel { Margin = new Thickness(12) };
        var box = new System.Windows.Controls.TextBox { Text = current };
        var btns = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new System.Windows.Controls.Button { Content = "OK", Width = 75, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "キャンセル", Width = 75, IsCancel = true };
        ok.Click += (_, _) => dlg.DialogResult = true;
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "アカウント名:" });
        panel.Children.Add(box);
        panel.Children.Add(btns);
        dlg.Content = panel;
        box.Focus();
        box.SelectAll();
        return dlg.ShowDialog() == true && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }

    /// <summary>旧バージョン(と設定リセット時)の互換用に names.txt も更新しておく。</summary>
    private void SaveNames()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataFolder());
            File.WriteAllLines(Path.Combine(AppSettings.DataFolder(), "names.txt"), new[] { _settings.Acc1Name, _settings.Acc2Name });
        }
        catch { }
    }

    // ---- 再読み込み・設定 ----

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            (_current == 1 ? View1 : View2).CoreWebView2?.Reload();
        }
        catch { }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        // 二重に開いて後から保存した方だけが反映されるのを防ぐ
        if (_settingsWindow?.IsLoaded == true)
        {
            _settingsWindow.Activate();
            return;
        }
        var draft = _settings.Clone();
        var w = new SettingsWindow(draft) { Owner = IsVisible ? this : null };
        w.Diagnose = RunDiagnosticsAsync;
        w.CheckUpdate = CheckForUpdateAsync;
        w.ExportRequested = ExportBundle;
        w.ImportRequested = ImportBundle;
        w.TestNotifyRequested = SendTestNotification;
        _settingsWindow = w;
        try
        {
            if (w.ShowDialog() == true)
            {
                _settings = draft;
                _settings.Save();
                ApplySettings();
            }
        }
        finally
        {
            _settingsWindow = null;
        }
        UpdateGearAlert();
    }

    private async Task<string> CheckForUpdateAsync()
    {
        try
        {
            var info = await Updater.FetchLatestAsync(_http);
            if (info is null)
                return "確認できませんでした。";
            if (Updater.IsNewer(Diagnostics.AppVersion(), info.Version))
                return $"新しいバージョンがあります: {info.Tag}\nトレイメニューの更新確認か、起動時の通知から更新できます。";
            return $"最新です(v{Diagnostics.AppVersion()})。";
        }
        catch (Exception ex)
        {
            return "確認できませんでした: " + ex.GetType().Name;
        }
    }

    // ---- 起動時更新確認・ワンクリック更新 ----

    private Task CheckUpdateOnStartupAsync() =>
        _updates.StartupCheckAsync(info =>
        {
            // 自己完結版は自動差し替えの対象外なので、手動更新が必要なことを伝える
            var text = Updater.IsSelfContainedBuild
                ? $"新しいバージョン {info.Tag} があります。クリックで詳細(自己完結版は手動更新)"
                : $"新しいバージョン {info.Tag} があります。クリックで更新";
            return Dispatcher.InvokeAsync(() =>
            {
                _tray.ShowBalloon("TwinCord", text,
                    WinForms.ToolTipIcon.Info, 15000, () =>
                    {
                        BringToFront();
                        OpenUpdateDialog(info);
                        return Task.CompletedTask;
                    });
            }).Task;
        });

    private Task CheckUpdateFromTrayAsync() =>
        _updates.TrayCheckAsync(
            info =>
            {
                OpenUpdateDialog(info);
                return Task.CompletedTask;
            },
            (message, warning) =>
            {
                _tray.ShowBalloon("TwinCord", message,
                    warning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info,
                    5000, () =>
                    {
                        BringToFront();
                        return Task.CompletedTask;
                    });
                return Task.CompletedTask;
            });

    private void OpenUpdateDialog(UpdateInfo info)
    {
        if (_updateDialog?.IsLoaded == true)
        {
            _updateDialog.Activate();
            return;
        }
        var w = new UpdateDialog(info) { Owner = IsVisible ? this : null };
        _updateDialog = w;
        bool applied;
        try
        {
            applied = w.ShowDialog() == true;
        }
        finally
        {
            _updateDialog = null;
        }
        if (applied)
        {
            _exiting = true;
            Close();
        }
    }

    private string ExportBundle()
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "TwinCord設定(*.twincord.json)|*.twincord.json",
                FileName = "twincord-backup.twincord.json",
            };
            if (dlg.ShowDialog() != true) return "キャンセルしました。";
            var bundle = new
            {
                app = "TwinCord",
                version = 1,
                settings = _settings,
                names = new[] { _settings.Acc1Name, _settings.Acc2Name },
            };
            File.WriteAllText(dlg.FileName,
                JsonSerializer.Serialize(bundle, new JsonSerializerOptions { WriteIndented = true }));
            return "エクスポートしました: " + dlg.FileName;
        }
        catch (Exception ex)
        {
            return "失敗: " + ex.Message;
        }
    }

    private string ImportBundle()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "TwinCord設定(*.twincord.json)|*.twincord.json",
            };
            if (dlg.ShowDialog() != true) return "キャンセルしました。";
            using var doc = JsonDocument.Parse(File.ReadAllText(dlg.FileName));
            var r = doc.RootElement;
            if (!r.TryGetProperty("app", out var a) || a.GetString() != "TwinCord")
                return "TwinCordの設定ファイルではありません。";
            if (r.TryGetProperty("settings", out var s))
            {
                var ns = JsonSerializer.Deserialize<AppSettings>(s.GetRawText());
                if (ns is not null)
                {
                    ns.Zoom1 = AppSettings.ClampZoom(ns.Zoom1);
                    ns.Zoom2 = AppSettings.ClampZoom(ns.Zoom2);
                    ns.NamesMigrated = true; // 旧names.txtを後から読み直さない
                    _settings = ns;
                    _settings.Save();
                }
            }
            // 旧形式のバンドルに含まれる名前も受け付ける
            if (r.TryGetProperty("names", out var nm) && nm.ValueKind == JsonValueKind.Array)
            {
                var arr = nm.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
                if (arr.Length > 0 && arr[0].Trim().Length > 0) _settings.Acc1Name = arr[0].Trim();
                if (arr.Length > 1 && arr[1].Trim().Length > 0) _settings.Acc2Name = arr[1].Trim();
                _settings.NamesMigrated = true;
                _settings.Save();
                SaveNames();
            }
            RefreshRail();
            ApplySettings();
            return "インポートしました(再起動不要)。";
        }
        catch (Exception ex)
        {
            return "失敗: " + ex.Message;
        }
    }

    private void ApplySettings()
    {
        ApplyRunKey(_settings.StartWithWindows);
        _tray.Ensure();
        SetZoom(View1, _settings.Zoom1);
        SetZoom(View2, _settings.Zoom2);
        if (!_settings.UnreadBadge)
        {
            Badge1.Visibility = Visibility.Collapsed;
            Badge2.Visibility = Visibility.Collapsed;
        }
    }

    private static void ApplyRunKey(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key is null) return;
            var exe = Environment.ProcessPath ?? "";
            if (enable)
                key.SetValue("TwinCord", "\"" + exe + "\"");
            else
                key.DeleteValue("TwinCord", false);
        }
        catch { }
    }

    // ---- トレイ常駐 ----

    internal void BringToFront() => ShowMain();

    private void ShowMain()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void OpenHistory()
    {
        if (_historyWindow?.IsLoaded == true)
        {
            _historyWindow.Activate();
            return;
        }
        var w = new HistoryWindow(new List<HistoryWindow.Item>(_history.Items), n =>
        {
            BringToFront();
            SwitchTo(n);
        });
        if (IsVisible) w.Owner = this;
        w.Closed += (_, _) => _historyWindow = null;
        _historyWindow = w;
        w.Show();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _placement?.Save();
        if (_settings.MinimizeToTray && !_exiting)
        {
            e.Cancel = true;
            TrimHostMemory();
            Hide();
            _tray.ShowBalloon("TwinCord", "トレイに格納しました", WinForms.ToolTipIcon.Info, 2000, () =>
            {
                BringToFront();
                return Task.CompletedTask;
            });
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                UnregisterHotKey(hwnd, 1);
                UnregisterHotKey(hwnd, 2);
                UnregisterHotKey(hwnd, 3);
            }
            _hotkeySource?.RemoveHook(WndProcHook);
        }
        catch { }
        _tray.Dispose();
        base.OnClosed(e);
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) return;
        TrimHostMemory();
        if (_settings.MinimizeToTray)
        {
            Hide();
            _tray.Ensure();
        }
    }

    // ホスト側のヒープ返却。Chromium側には触らない(常駐・通話に影響させないため)
    private static void TrimHostMemory()
    {
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            EmptyWorkingSet(Process.GetCurrentProcess().Handle);
        }
        catch { }
    }

    // ---- 未読バッジ・死活監視(実体は PageWatchdog) ----

    // ---- 劣化通知・自己診断 ----

    private void OnFeatureDegraded(string key)
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

    private async Task<string> RunDiagnosticsAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"TwinCord v{Diagnostics.AppVersion()} / {DateTime.Now:yyyy-MM-dd HH:mm}");
        try
        {
            long mb = Process.GetCurrentProcess().PrivateMemorySize64 / 1024 / 1024;
            sb.AppendLine($"プロセス全体: {mb}MB");
        }
        catch { }
        try
        {
            sb.AppendLine("WebView2: " + CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch
        {
            sb.AppendLine("WebView2: 取得失敗(未インストールの疑い)");
        }
        sb.AppendLine(await CheckViewAsync("Acc1", View1, _view1Ready, _settings.Zoom1));
        sb.AppendLine(await CheckViewAsync("Acc2", View2, _view2Ready, _settings.Zoom2));
        var bad = _diag.DegradedKeys();
        sb.Append("劣化中の機能: ");
        sb.AppendLine(bad.Count == 0 ? "なし" : string.Join(", ", bad.Select(Diagnostics.DisplayName)));
        var report = sb.ToString();
        Diagnostics.Log("自己診断:\n" + report);
        return report;
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
            var t = core.ExecuteScriptAsync("document.readyState");
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
                var env = label == "Acc1" ? _env1 : _env2;
                if (env is null) mem = "計測前";
                else
                {
                    long bytes = 0;
                    int cnt = 0;
                    foreach (var p in env.GetProcessInfos())
                    {
                        try { bytes += Process.GetProcessById(p.ProcessId).PrivateMemorySize64; cnt++; }
                        catch { }
                    }
                    mem = $"{bytes / 1024 / 1024}MB({cnt}プロセス)";
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
