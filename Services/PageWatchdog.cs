using System.IO;
using System.Net.Http;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;

namespace TwinCord.Services;

/// <summary>
/// 非表示タブの監視タイマー群(バッジ・通話・死活・アバター)とメモリレベル管理。
/// UI更新は渡されたコントロール参照に直接行う。設定は差し替えに追従するため提供関数経由で読む。
/// </summary>
public sealed class PageWatchdog
{
    public sealed class AccountSlot
    {
        public required WpfWebView2 View { get; init; }
        public required Func<bool> IsReady { get; init; }
        public required System.Windows.Controls.Button ActionButton { get; init; }
        public required Func<string> AccountName { get; init; }
        public required System.Windows.Controls.Border Badge { get; init; }
        public required System.Windows.Controls.TextBlock BadgeText { get; init; }
        public required Func<double> Zoom { get; init; }
    }

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

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly AccountSlot _acc1;
    private readonly AccountSlot _acc2;
    private readonly Func<AppSettings> _settings;
    private readonly Diagnostics _diag;
    private readonly Func<int> _current;

    private bool _inVoice1;
    private bool _inVoice2;
    private string _avatarUrl1 = "";
    private string _avatarUrl2 = "";
    private DispatcherTimer? _badgeTimer;
    private DispatcherTimer? _healthTimer;
    private DispatcherTimer? _avatarTimer;
    private bool _badgeBusy;
    private bool _healthBusy;
    private bool _avatarBusy;

    public PageWatchdog(AccountSlot acc1, AccountSlot acc2, Func<AppSettings> settings,
        Diagnostics diag, Func<int> current)
    {
        _acc1 = acc1;
        _acc2 = acc2;
        _settings = settings;
        _diag = diag;
        _current = current;
    }

    public bool InVoice(int n) => n == 1 ? _inVoice1 : _inVoice2;

    public bool AnyVoice => _inVoice1 || _inVoice2;

    public async Task<string> ProbeBadgeAsync(int n)
    {
        try
        {
            var slot = Slot(n);
            if (slot.View.CoreWebView2 is null) return "実行失敗";
            var raw = (await slot.View.CoreWebView2.ExecuteScriptAsync(BadgeScript))?.Trim();
            if (!int.TryParse(raw, out var count)) return "判定不能";
            return count < 0 ? "スクリプト異常" : count.ToString();
        }
        catch
        {
            return "実行失敗";
        }
    }

    public bool HasAvatar(int n) => File.Exists(AvatarPath(n));

    public void RefreshAccountIcons()
    {
        SetAccountIcon(1);
        SetAccountIcon(2);
    }

    public void Start()
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

    public void RefreshMemoryLevels()
    {
        bool voice = _inVoice1 || _inVoice2;
        int current = _current();
        SetMemoryLevel(_acc1.View, !voice && current != 1);
        SetMemoryLevel(_acc2.View, !voice && current != 2);
    }

    public static void SetMemoryLevel(WpfWebView2 view, bool low)
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

    private AccountSlot Slot(int n) => n == 1 ? _acc1 : _acc2;

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

    public async Task TryUpdateAvatarAsync(int n)
    {
        try
        {
            var slot = Slot(n);
            if (!slot.IsReady() || slot.View.CoreWebView2 is null) return;
            var url = (await slot.View.CoreWebView2.ExecuteScriptAsync(AvatarScript))?.Trim().Trim('"');
            if (string.IsNullOrEmpty(url)) return;
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

    private void SetAccountIcon(int n)
    {
        var slot = Slot(n);
        var btn = slot.ActionButton;
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
        btn.Content = InitialOf(slot.AccountName());
    }

    private static string InitialOf(string name)
    {
        var t = (name ?? "").Trim();
        return t.Length == 0 ? "?" : t[..1].ToUpperInvariant();
    }

    public static string AvatarPath(int n) => Path.Combine(AppSettings.DataFolder(), "avatars", $"acc{n}.png");

    private async void BadgeTimer_Tick(object? sender, EventArgs e)
    {
        if (_badgeBusy) return;
        _badgeBusy = true;
        try
        {
            var settings = _settings();
            int current = _current();
            if (settings.UnreadBadge)
            {
                if (current != 2 && _acc2.IsReady())
                    await UpdateBadgeAsync(2);
                if (current != 1 && _acc1.IsReady())
                    await UpdateBadgeAsync(1);
            }
            else
            {
                _acc1.Badge.Visibility = System.Windows.Visibility.Collapsed;
                _acc2.Badge.Visibility = System.Windows.Visibility.Collapsed;
            }
            // バッジOFFでも通話検出とズーム同期は継続する
            if (_acc1.IsReady()) await UpdateVoiceAsync(1);
            if (_acc2.IsReady()) await UpdateVoiceAsync(2);
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
            var slot = Slot(n);
            if (!slot.IsReady()) return;
            var z = AppSettings.ClampZoom(slot.View.ZoomFactor);
            var settings = _settings();
            if (n == 1)
            {
                if (Math.Abs(settings.Zoom1 - z) > 0.01) { settings.Zoom1 = z; settings.Save(); }
            }
            else
            {
                if (Math.Abs(settings.Zoom2 - z) > 0.01) { settings.Zoom2 = z; settings.Save(); }
            }
        }
        catch { }
    }

    private async Task UpdateVoiceAsync(int n)
    {
        try
        {
            var slot = Slot(n);
            if (slot.View.CoreWebView2 is null) return;
            var raw = (await slot.View.CoreWebView2.ExecuteScriptAsync(VoiceScript))?.Trim().Trim('"');
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

    private async Task UpdateBadgeAsync(int n)
    {
        var slot = Slot(n);
        try
        {
            if (slot.View.CoreWebView2 is null) return;
            var raw = (await slot.View.CoreWebView2.ExecuteScriptAsync(BadgeScript))?.Trim();
            if (int.TryParse(raw, out var count))
            {
                if (count < 0)
                {
                    _diag.ReportFail("badge"); // スクリプト内部異常=Discord側の構造変化の疑い
                    return;
                }
                if (_settings().CountUnreadChannels)
                {
                    try
                    {
                        var uraw = (await slot.View.CoreWebView2.ExecuteScriptAsync(UnreadScript))?.Trim();
                        if (int.TryParse(uraw, out var u) && u > 0) count += u;
                    }
                    catch { }
                }
                _diag.ReportOk("badge");
                if (count > 0)
                {
                    slot.BadgeText.Text = count > 99 ? "99+" : count.ToString();
                    slot.Badge.Visibility = System.Windows.Visibility.Visible;
                }
                else
                {
                    slot.Badge.Visibility = System.Windows.Visibility.Collapsed;
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
        if (!_settings().AutoReload || _healthBusy) return;
        _healthBusy = true;
        try
        {
            int current = _current();
            // 表示中は再読み込みせず報告のみ(読書中のページを奪わない)
            if (_acc1.IsReady() && !InVoice(1)) await PingAsync(_acc1.View, current != 1);
            if (_acc2.IsReady() && !InVoice(2)) await PingAsync(_acc2.View, current != 2);
        }
        finally { _healthBusy = false; }
    }

    private async Task PingAsync(WpfWebView2 view, bool allowReload)
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
}
