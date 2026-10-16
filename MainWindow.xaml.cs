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

    // Discordページ内でCtrl+1/2を拾ってホストに通知するJS
    private const string HotkeyScript =
        "window.addEventListener('keydown',function(e){if(e.ctrlKey&&!e.shiftKey&&!e.altKey&&(e.key==='1'||e.key==='2')){var t=e.target;if(t){var tn=(t.tagName||'').toUpperCase();if(tn==='INPUT'||tn==='TEXTAREA')return;if(t.closest&&t.closest('[contenteditable=\"true\"]'))return;}chrome.webview.postMessage('switch:'+e.key);}});";

    // Discordのデスクトップ通知を横取りしてホストに転送するJS(DM・メンション等すべて)
    // 本物のNotificationは作らない(二重通知防止のためダミーを返す)
    private const string NotifyScript =
        "(function(){try{var stub={close:function(){},addEventListener:function(){},removeEventListener:function(){}};function Hooked(t,o){try{chrome.webview.postMessage('notify:'+JSON.stringify({title:t||'',body:(o&&o.body)||''}));}catch(e){}return stub;}Hooked.permission='granted';Hooked.requestPermission=function(){return Promise.resolve('granted');};window.Notification=Hooked;}catch(e){}})();";

    // 通話中検出用JS(切断ボタンが出ていれば通話中とみなす)
    private const string VoiceScript =
        "(()=>{try{return document.querySelector('[aria-label=\"切断\"],[aria-label=\"Disconnect\"],[aria-label=\"Desconectar\"],[aria-label=\"Déconnecter\"],[aria-label=\"Trennen\"],[aria-label=\"Disconnetti\"],[aria-label=\"Desligar\"],[aria-label=\"Verbinding verbreken\"],[aria-label=\"Rozłącz\"],[aria-label=\"Отключиться\"],[aria-label=\"연결 해제\"],[aria-label=\"断开连接\"],[aria-label=\"斷開連接\"]')?'1':'0';}catch(e){return '0';}})()";
    private const string AvatarScript =
        "(()=>{try{var btn=document.querySelector('[aria-label=\"ユーザー設定\"],[aria-label=\"User Settings\"]');var scope=btn?btn.parentElement:null;var img=null;for(var i=0;i<4&&scope;i++){img=scope.querySelector('img[src*=\"avatars/\"]');if(img)break;scope=scope.parentElement;}if(!img){var all=document.querySelectorAll('[class*=\"panels\"] img[src*=\"avatars/\"]');if(all.length)img=all[0];}if(!img)return '';var src=img.src||'';if(!src)return '';return src.split('?')[0]+'?size=64';}catch(e){return '';}})()";

    // 未読チャンネル数を数えるJS(ベストエフォート)
    private const string UnreadScript =
        "(()=>{try{const sels=['[class*=unreadBar]','[class*=unreadPill]','[aria-label*=未読]','[aria-label*=unread]','[aria-label*=Unread]'];const seen=new Set();let n=0;for(const s of sels){try{document.querySelectorAll(s).forEach(function(el){if(seen.has(el))return;seen.add(el);n++;});}catch(_){}}return n;}catch(_){return -1;}})()";

    // 未読・メンション数を数えるJS(ベストエフォート)
    private const string BadgeScript =
        "(()=>{try{let n=0;const seen=new Set();const sels=['[class*=numberBadge]','[class*=unreadMention]','[aria-label*=メンション]','[aria-label*=mention]','[aria-label*=Mention]'];for(const s of sels){try{document.querySelectorAll(s).forEach(function(el){if(seen.has(el))return;seen.add(el);const t=((el.innerText||el.textContent)||'').trim();const d=t.match(/\\d+/);n+=d?parseInt(d[0],10):1;});}catch(_){}}return n;}catch(_){return -1;}})()";

    private string _acc1Name = "Acc1";
    private string _acc2Name = "Acc2";
    private int _current = 1;

    private AppSettings _settings = AppSettings.Load();

    private bool _view1Ready;
    private bool _view2Ready;
    private Task? _initTask1;
    private Task? _initTask2;
    private CoreWebView2Environment? _env1;
    private CoreWebView2Environment? _env2;
    private bool _inVoice1;
    private bool _inVoice2;

    private WinForms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private bool _exiting;
    private int _balloonAccount = 1;
    private readonly List<HistoryWindow.Item> _history = new();
    private string _avatarUrl1 = "";
    private string _avatarUrl2 = "";
    private DispatcherTimer? _avatarTimer;
    private bool _avatarBusy;
    private DispatcherTimer? _placeTimer;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private DispatcherTimer? _badgeTimer;
    private DispatcherTimer? _healthTimer;
    private bool _badgeBusy;
    private bool _healthBusy;

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
        RestorePlacement();
        if (_settings.StartMinimized) WindowState = WindowState.Minimized;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
        LocationChanged += PlaceChanged;
        SizeChanged += PlaceChanged;
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
            var hwnd = new WindowInteropHelper(this).Handle;
            _hotkeySource = HwndSource.FromHwnd(hwnd);
            _hotkeySource?.AddHook(WndProcHook);
            const uint mod = 0x0001 | 0x0002; // Alt | Ctrl
            if (!RegisterHotKey(hwnd, 1, mod, 0x31))
                Diagnostics.Log("グローバルホットキー登録失敗: Ctrl+Alt+1");
            if (!RegisterHotKey(hwnd, 2, mod, 0x32))
                Diagnostics.Log("グローバルホットキー登録失敗: Ctrl+Alt+2");
            if (!RegisterHotKey(hwnd, 3, mod, 0x4D))
                Diagnostics.Log("グローバルホットキー登録失敗: Ctrl+Alt+M");
        }
        catch { }
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

    private void RestorePlacement()
    {
        try
        {
            if (double.IsNaN(_settings.WinWidth) || double.IsNaN(_settings.WinHeight)
                || _settings.WinWidth < 200 || _settings.WinHeight < 200) return;
            Width = _settings.WinWidth;
            Height = _settings.WinHeight;
            if (!double.IsNaN(_settings.WinLeft) && !double.IsNaN(_settings.WinTop))
            {
                // モニター構成変更で完全画面外になっていたら既定位置のままにする
                var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                if (vs.IntersectsWith(new Rect(_settings.WinLeft, _settings.WinTop, _settings.WinWidth, _settings.WinHeight)))
                {
                    Left = _settings.WinLeft;
                    Top = _settings.WinTop;
                }
            }
            if (_settings.WinMaximized) WindowState = WindowState.Maximized;
        }
        catch { }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Diagnostics.Log($"TwinCord v{Diagnostics.AppVersion()} 起動");
            LoadNames();
            LoadHistory();
            ApplySettings();
            StartTimers();
            // 逐次起動(起動ピークを抑えつつ両方常駐)
            await EnsureViewAsync(1);
            RefreshRail();
            await EnsureViewAsync(2);
            SwitchTo(_current);
            _ = TryUpdateAvatarAsync(1);
            _ = TryUpdateAvatarAsync(2);
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
        bool voice = _inVoice1 || _inVoice2;
        SetMemoryLevel(View1, !voice && n != 1);
        SetMemoryLevel(View2, !voice && n != 2);
    }

    private static void SetMemoryLevel(Microsoft.Web.WebView2.Wpf.WebView2 view, bool low)
    {
        try
        {
            if (view.CoreWebView2 is null) return;
            view.CoreWebView2.MemoryUsageTargetLevel = low
                ? CoreWebView2MemoryUsageTargetLevel.Low
                : CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch { }
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
        view.CoreWebView2.ProcessFailed += (_, _) => view.Dispatcher.BeginInvoke(() =>
        {
            // 通話中は再読み込みしない(切断防止)
            if (ReferenceEquals(view, View1) ? _inVoice1 : _inVoice2) return;
            _diag.ReportFail("page");
            try { view.CoreWebView2?.Reload(); } catch { }
        });
        view.CoreWebView2.Navigate("https://discord.com/channels/@me");
    }

    // Discordページ内Ctrl+1/2・通知の受信
    private void View_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var msg = e.TryGetWebMessageAsString();
        if (msg == "switch:1") SwitchTo(1);
        else if (msg == "switch:2") SwitchTo(2);
        else if (msg is not null && msg.StartsWith("notify:", StringComparison.Ordinal))
            HandlePageNotify(sender, msg.Substring(7));
    }

    private void HandlePageNotify(object? sender, string json)
    {
        int n = ReferenceEquals(sender, View2) ? 2 : 1;
        // 見ている最中のアカウントは通知不要
        if (IsVisible && WindowState != WindowState.Minimized && _current == n) return;
        var acc = n == 1 ? _acc1Name : _acc2Name;
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
        _history.Insert(0, new HistoryWindow.Item(DateTime.Now, n, acc, text));
        while (_history.Count > 30) _history.RemoveAt(_history.Count - 1);
        SaveHistory();
        Dispatcher.BeginInvoke(() => ShowBalloon(n, acc, text));
    }

    private void ShowBalloon(int n, string acc, string text)
    {
        SetupTray();
        _balloonAccount = n;
        if (_tray is not null)
        {
            _tray.Visible = true;
            _tray.ShowBalloonTip(8000, "TwinCord - " + acc, text, WinForms.ToolTipIcon.Info);
        }
    }

    // ---- レール表示・改名 ----

    private void RefreshRail()
    {
        SetAccountIcon(1);
        Acc1Button.ToolTip = _acc1Name + (IsMuted(View1) ? " (ミュート中)" : "");
        Acc1Label.Text = _acc1Name;
        SetAccountIcon(2);
        Acc2Button.ToolTip = _acc2Name + (IsMuted(View2) ? " (ミュート中)" : "");
        Acc2Label.Text = _acc2Name;
        View1.Visibility = _current == 1 && _view1Ready ? Visibility.Visible : Visibility.Collapsed;
        View2.Visibility = _current == 2 && _view2Ready ? Visibility.Visible : Visibility.Collapsed;
        Pill1.Visibility = _current == 1 ? Visibility.Visible : Visibility.Collapsed;
        Pill2.Visibility = _current == 2 ? Visibility.Visible : Visibility.Collapsed;
        Acc1Button.Background = _current == 1 ? SelectedBrush : NormalBrush;
        Acc2Button.Background = _current == 2 ? SelectedBrush : NormalBrush;
    }

    private static string InitialOf(string name)
    {
        var t = (name ?? "").Trim();
        return t.Length == 0 ? "?" : t[..1].ToUpperInvariant();
    }

    private static string AvatarPath(int n) => Path.Combine(AppSettings.DataFolder(), "avatars", $"acc{n}.png");

    private void SetAccountIcon(int n)
    {
        var btn = n == 1 ? Acc1Button : Acc2Button;
        var path = AvatarPath(n);
        if (File.Exists(path))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelWidth = 96;
                bmp.EndInit();
                bmp.Freeze();
                var img = new System.Windows.Controls.Image
                {
                    Width = 40,
                    Height = 40,
                    Stretch = System.Windows.Media.Stretch.UniformToFill,
                    Source = bmp,
                };
                img.Clip = new System.Windows.Media.EllipseGeometry(new System.Windows.Point(20, 20), 20, 20);
                btn.Content = img;
                return;
            }
            catch { }
        }
        btn.Content = InitialOf(n == 1 ? _acc1Name : _acc2Name);
    }

    private async Task TryUpdateAvatarAsync(int n)
    {
        try
        {
            var view = n == 1 ? View1 : View2;
            if ((n == 1 && !_view1Ready) || (n == 2 && !_view2Ready)) return;
            if (view.CoreWebView2 is null) return;
            var raw = await view.CoreWebView2.ExecuteScriptAsync(AvatarScript);
            string url = "";
            try { url = JsonDocument.Parse(raw).RootElement.GetString() ?? ""; } catch { return; }
            if (string.IsNullOrEmpty(url)) return; // 未ログイン時
            if (n == 1)
            {
                if (url == _avatarUrl1) return;
            }
            else
            {
                if (url == _avatarUrl2) return;
            }
            var bytes = await _http.GetByteArrayAsync(url);
            var path = AvatarPath(n);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
            if (n == 1) _avatarUrl1 = url; else _avatarUrl2 = url;
            SetAccountIcon(n);
        }
        catch { }
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
            Diagnostics.Log($"{(n == 1 ? _acc1Name : _acc2Name)} ミュート{(c.IsMuted ? "ON" : "OFF")}");
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
        var current = n == 1 ? _acc1Name : _acc2Name;
        var next = PromptName(current);
        if (next is null) return;
        if (n == 1) _acc1Name = next; else _acc2Name = next;
        RefreshRail();
        SaveNames();
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

    private void LoadNames()
    {
        try
        {
            var file = Path.Combine(AppSettings.DataFolder(), "names.txt");
            if (!File.Exists(file)) return;
            var lines = File.ReadAllLines(file);
            if (lines.Length > 0 && lines[0].Trim().Length > 0)
                _acc1Name = lines[0].Trim();
            if (lines.Length > 1 && lines[1].Trim().Length > 0)
                _acc2Name = lines[1].Trim();
        }
        catch { }
        finally { RefreshRail(); }
    }

    private void SaveNames()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataFolder());
            File.WriteAllLines(Path.Combine(AppSettings.DataFolder(), "names.txt"), new[] { _acc1Name, _acc2Name });
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
        var draft = _settings.Clone();
        var w = new SettingsWindow(draft) { Owner = IsVisible ? this : null };
        w.Diagnose = RunDiagnosticsAsync;
        w.CheckUpdate = CheckForUpdateAsync;
        w.ExportRequested = ExportBundle;
        w.ImportRequested = ImportBundle;
        w.TestNotifyRequested = SendTestNotification;
        if (w.ShowDialog() == true)
        {
            _settings = draft;
            _settings.Save();
            ApplySettings();
        }
        UpdateGearAlert();
    }

    private async Task<string> CheckForUpdateAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://api.github.com/repos/Peragami/TwinCord/releases/latest");
            req.Headers.UserAgent.ParseAdd("TwinCord");
            using var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
                return "確認できませんでした(HTTP " + (int)res.StatusCode + ")。";
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var r = doc.RootElement;
            var tag = r.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            var url = r.TryGetProperty("html_url", out var u) ? (u.GetString() ?? "") : "";
            var latest = tag.TrimStart('v', 'V');
            if (string.IsNullOrEmpty(latest)) return "確認できませんでした。";
            if (CompareVersions(Diagnostics.AppVersion(), latest) < 0)
                return $"新しいバージョンがあります: {tag}\n{url}";
            return $"最新です(v{Diagnostics.AppVersion()})。";
        }
        catch (Exception ex)
        {
            return "確認できませんでした: " + ex.GetType().Name;
        }
    }

    private static int CompareVersions(string a, string b)
    {
        try
        {
            return new Version(a.Split('-')[0]).CompareTo(new Version(b.Split('-')[0]));
        }
        catch
        {
            return 0;
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
                names = new[] { _acc1Name, _acc2Name },
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

    private void SendTestNotification()
    {
        var n = _current;
        var acc = n == 1 ? _acc1Name : _acc2Name;
        Diagnostics.Log("テスト通知送信");
        ShowBalloon(n, acc, "テスト通知\nこれはテストです。クリックでこのアカウントを表示します。");
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
                    _settings = ns;
                    _settings.Save();
                }
            }
            if (r.TryGetProperty("names", out var nm) && nm.ValueKind == JsonValueKind.Array)
            {
                var arr = nm.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
                if (arr.Length > 0 && arr[0].Trim().Length > 0) _acc1Name = arr[0].Trim();
                if (arr.Length > 1 && arr[1].Trim().Length > 0) _acc2Name = arr[1].Trim();
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
        SetupTray();
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
            if (enable)
                key.SetValue("TwinCord", Environment.ProcessPath ?? "");
            else
                key.DeleteValue("TwinCord", false);
        }
        catch { }
    }

    // ---- トレイ常駐 ----

    private void SetupTray()
    {
        if (_tray is not null)
        {
            _tray.Visible = true;
            return;
        }
        _trayIcon ??= GetAppIcon();
        _tray = new WinForms.NotifyIcon
        {
            Icon = _trayIcon ?? System.Drawing.SystemIcons.Application,
            Text = "TwinCord",
            Visible = true,
        };
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("表示", null, (_, _) => ShowMain());
        menu.Items.Add("通知履歴", null, (_, _) => OpenHistory());
        menu.Items.Add("設定", null, (_, _) => OpenSettings());
        menu.Items.Add("終了", null, (_, _) => { _exiting = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowMain();
        _tray.BalloonTipClicked += (_, _) =>
        {
            var n = _balloonAccount;
            BringToFront();
            if (n is 1 or 2) SwitchTo(n);
        };
    }

    private static System.Drawing.Icon? GetAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return null;
            return System.Drawing.Icon.ExtractAssociatedIcon(path);
        }
        catch
        {
            return null;
        }
    }

    internal void BringToFront() => ShowMain();

    private void OpenHistory()
    {
        var w = new HistoryWindow(new List<HistoryWindow.Item>(_history), n =>
        {
            BringToFront();
            SwitchTo(n);
        });
        if (IsVisible) w.Owner = this;
        w.Show();
    }

    private static string HistoryPath() => Path.Combine(AppSettings.DataFolder(), "notifications.json");

    private void LoadHistory()
    {
        try
        {
            var p = HistoryPath();
            if (!File.Exists(p)) return;
            var list = JsonSerializer.Deserialize<List<HistoryWindow.Item>>(File.ReadAllText(p));
            if (list is null) return;
            _history.AddRange(list.Take(30));
        }
        catch { }
    }

    private void SaveHistory()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataFolder());
            File.WriteAllText(HistoryPath(), JsonSerializer.Serialize(_history.Take(30).ToList()));
        }
        catch { }
    }

    private void ShowMain()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        SavePlacement();
        try
        {
            // トースト実験時代の残骸ショートカットを掃除
            var lnk = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu\Programs\TwinCord.lnk");
            if (File.Exists(lnk)) File.Delete(lnk);
        }
        catch { }
        if (_settings.MinimizeToTray && !_exiting)
        {
            e.Cancel = true;
            Hide();
            if (_tray is not null)
            {
                _tray.Visible = true;
                _tray.ShowBalloonTip(2000, "TwinCord", "トレイに格納しました", WinForms.ToolTipIcon.Info);
            }
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
        _tray?.Dispose();
        _trayIcon?.Dispose();
        base.OnClosed(e);
    }

    // 移動・リサイズの確定2秒後に保存(ドラッグ中の大量書込を回避)
    private void PlaceChanged(object? sender, EventArgs e)
    {
        try
        {
            if (WindowState != WindowState.Normal) return;
            if (_placeTimer is null)
            {
                _placeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _placeTimer.Tick += (_, _) => { _placeTimer?.Stop(); SavePlacement(); };
            }
            _placeTimer.Stop();
            _placeTimer.Start();
        }
        catch { }
    }

    private void SavePlacement()
    {
        try
        {
            _settings.WinMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                _settings.WinLeft = Left;
                _settings.WinTop = Top;
                _settings.WinWidth = Width;
                _settings.WinHeight = Height;
            }
            _settings.Save();
        }
        catch { }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) return;
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            EmptyWorkingSet(Process.GetCurrentProcess().Handle);
        }
        catch { }
        if (_settings.MinimizeToTray)
        {
            Hide();
            if (_tray is not null) _tray.Visible = true;
        }
    }

    // ---- 未読バッジ・死活監視 ----

    private void StartTimers()
    {
        _badgeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _badgeTimer.Tick += BadgeTimer_Tick;
        _badgeTimer.Start();
        _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _healthTimer.Tick += HealthTimer_Tick;
        _healthTimer.Start();
        _avatarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _avatarTimer.Tick += AvatarTimer_Tick;
        _avatarTimer.Start();
    }

    private async void AvatarTimer_Tick(object? sender, EventArgs e)
    {
        if (_avatarBusy) return;
        _avatarBusy = true;
        try
        {
            await TryUpdateAvatarAsync(1);
            await TryUpdateAvatarAsync(2);
        }
        catch { }
        finally { _avatarBusy = false; }
    }

    private async void BadgeTimer_Tick(object? sender, EventArgs e)
    {
        if (_badgeBusy) return;
        _badgeBusy = true;
        try
        {
            if (_settings.UnreadBadge)
            {
                if (_current != 2 && _view2Ready)
                    await UpdateBadgeAsync(View2, Badge2, Badge2Text);
                if (_current != 1 && _view1Ready)
                    await UpdateBadgeAsync(View1, Badge1, Badge1Text);
            }
            else
            {
                Badge1.Visibility = Visibility.Collapsed;
                Badge2.Visibility = Visibility.Collapsed;
            }
            // バッジOFFでも通話検出とズーム同期は継続する
            if (_view1Ready) await UpdateVoiceAsync(1);
            if (_view2Ready) await UpdateVoiceAsync(2);
            SyncZoom(1);
            SyncZoom(2);
        }
        catch { }
        finally { _badgeBusy = false; }
    }

    // ページ側のCtrl+ホイール変更を取込んで保存
    private void SyncZoom(int n)
    {
        try
        {
            var view = n == 1 ? View1 : View2;
            if ((n == 1 && !_view1Ready) || (n == 2 && !_view2Ready)) return;
            var z = AppSettings.ClampZoom(view.ZoomFactor);
            if (n == 1)
            {
                if (Math.Abs(_settings.Zoom1 - z) > 0.01) { _settings.Zoom1 = z; _settings.Save(); }
            }
            else
            {
                if (Math.Abs(_settings.Zoom2 - z) > 0.01) { _settings.Zoom2 = z; _settings.Save(); }
            }
        }
        catch { }
    }

    private async Task UpdateVoiceAsync(int n)
    {
        try
        {
            var view = n == 1 ? View1 : View2;
            if (view.CoreWebView2 is null) return;
            var raw = (await view.CoreWebView2.ExecuteScriptAsync(VoiceScript))?.Trim().Trim('"');
            bool voice = raw == "1";
            if (n == 1)
            {
                if (_inVoice1 != voice)
                {
                    _inVoice1 = voice;
                    Diagnostics.Log($"Acc1 通話{(voice ? "開始" : "終了")}");
                    RefreshMemoryLevels();
                }
            }
            else
            {
                if (_inVoice2 != voice)
                {
                    _inVoice2 = voice;
                    Diagnostics.Log($"Acc2 通話{(voice ? "開始" : "終了")}");
                    RefreshMemoryLevels();
                }
            }
        }
        catch { }
    }

    private void RefreshMemoryLevels()
    {
        bool voice = _inVoice1 || _inVoice2;
        SetMemoryLevel(View1, !voice && _current != 1);
        SetMemoryLevel(View2, !voice && _current != 2);
    }

    private async Task UpdateBadgeAsync(
        Microsoft.Web.WebView2.Wpf.WebView2 view,
        System.Windows.Controls.Border badge,
        TextBlock text)
    {
        try
        {
            if (view.CoreWebView2 is null) return;
            var raw = (await view.CoreWebView2.ExecuteScriptAsync(BadgeScript))?.Trim();
            if (int.TryParse(raw, out var n))
            {
                if (n < 0)
                {
                    _diag.ReportFail("badge"); // スクリプト内部異常=Discord側の構造変化の疑い
                    return;
                }
                if (_settings.CountUnreadChannels)
                {
                    try
                    {
                        var uraw = (await view.CoreWebView2.ExecuteScriptAsync(UnreadScript))?.Trim();
                        if (int.TryParse(uraw, out var u) && u > 0) n += u;
                    }
                    catch { }
                }
                _diag.ReportOk("badge");
                if (n > 0)
                {
                    text.Text = n > 99 ? "99+" : n.ToString();
                    badge.Visibility = Visibility.Visible;
                }
                else
                {
                    badge.Visibility = Visibility.Collapsed;
                }
            }
        }
        catch
        {
            _diag.ReportFail("badge");
        }
    }

    private async void HealthTimer_Tick(object? sender, EventArgs e)
    {
        if (!_settings.AutoReload || _healthBusy) return;
        _healthBusy = true;
        try
        {
            // 表示中は再読み込みせず報告のみ(読書中のページを奪わない)
            if (_view1Ready && !_inVoice1) await PingAsync(View1, _current != 1);
            if (_view2Ready && !_inVoice2) await PingAsync(View2, _current != 2);
        }
        finally { _healthBusy = false; }
    }

    private async Task PingAsync(Microsoft.Web.WebView2.Wpf.WebView2 view, bool allowReload)
    {
        try
        {
            var core = view.CoreWebView2;
            if (core is null) return;
            var t = core.ExecuteScriptAsync("document.readyState");
            if (await Task.WhenAny(t, Task.Delay(10000)) != t)
            {
                _diag.ReportFail("page");
                if (allowReload) { try { core.Reload(); } catch { } }
                return;
            }
            await t;
            _diag.ReportOk("page");
        }
        catch
        {
            _diag.ReportFail("page");
            if (allowReload) { try { view.CoreWebView2?.Reload(); } catch { } }
        }
    }

    // ---- 劣化通知・自己診断 ----

    private void OnFeatureDegraded(string key)
    {
        Dispatcher.BeginInvoke(() =>
        {
            UpdateGearAlert();
            var name = Diagnostics.DisplayName(key);
            var msg = $"「{name}」が動作しなくなりました。Discord側の更新により TwinCord v{Diagnostics.AppVersion()} が古くなった可能性があります。設定(歯車)→アプリについて→自己診断で詳細を確認してください。";
            Diagnostics.Log("通知: " + msg.Replace(Environment.NewLine, " "));
            _balloonAccount = -1; // 警告クリックではアカ切替しない
            if (_tray is not null && _tray.Visible)
                _tray.ShowBalloonTip(8000, "TwinCord", msg, WinForms.ToolTipIcon.Warning);
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
            string badge;
            try
            {
                var raw = (await core.ExecuteScriptAsync(BadgeScript))?.Trim();
                badge = int.TryParse(raw, out var n) ? (n < 0 ? "スクリプト異常" : n.ToString()) : "判定不能";
            }
            catch
            {
                badge = "実行失敗";
            }
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
            bool voice = label == "Acc1" ? _inVoice1 : _inVoice2;
            string avatar = File.Exists(AvatarPath(label == "Acc1" ? 1 : 2)) ? "取得済" : "未取得";
            return $"{label}: readyState={state}({sw.ElapsedMilliseconds}ms) メモリ={mem} 通話={(voice ? "通話中" : "なし")} バッジテスト={badge} 拡大率={(int)(zoom * 100)}% アバター={avatar}";
        }
        catch (Exception ex)
        {
            return $"{label}: エラー({ex.GetType().Name})";
        }
    }
}
