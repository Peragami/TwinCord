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
    }

    // 通話中検出用JS。切断ボタンのaria-labelはロケール依存のため主要言語を列挙し、
    // さらにロケール非依存のRTCパネル要素を保険として見る。取りこぼすと通話中に
    // メモリを絞ってしまうため、判断がつかないときは '-1'(判定不能)を返し、
    // 呼び出し側は状態を維持して安全側に倒す。
    private const string VoiceScript =
        "(()=>{try{" +
        "var l=['切断','Disconnect','Desconectar','Déconnecter','Trennen','Disconnetti','Desligar','Verbinding verbreken','Rozłącz','Отключиться','연결 해제','断开连接','斷開連接','Bağlantıyı Kes','Ngắt','ตัดการเชื่อมต่อ','Putuskan','Deconectare','Αποσύνδεση','Leválasztás','Odpojit','Katkaise','Koble fra','Afbryd','Koppla från'];" +
        "for(var i=0;i<l.length;i++){if(document.querySelector('[aria-label=\"'+l[i]+'\"]'))return '1';}" +
        "if(document.querySelector('[aria-label*=\"Від\"]'))return '1';" +
        "if(document.querySelector('[class*=\"rtcConnectionStatus\"],[class*=\"voicePanel\"],[class*=\"callContainer\"]'))return '1';" +
        "return '0';}catch(e){return '-1';}})()";
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
            var core = slot.View.CoreWebView2;
            if (core is null) return "実行失敗";
            var raw = await EvalAsync(core, BadgeScript);
            if (raw is null) return "実行失敗";
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

    /// <summary>
    /// スクリプト実行の共通ラッパ。タイムアウト/例外時はnullを返す。
    /// awaitしっぱなしになると _busy フラグが立ったまま監視が永久に止まるため、必ず時間で打ち切る。
    /// </summary>
    private static async Task<string?> EvalAsync(CoreWebView2 core, string script, int timeoutMs = 10000)
    {
        try
        {
            var task = core.ExecuteScriptAsync(script);
            if (await Task.WhenAny(task, Task.Delay(timeoutMs)) != task)
                return null;
            return (await task)?.Trim();
        }
        catch
        {
            return null;
        }
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

    public async Task TryUpdateAvatarAsync(int n)
    {
        try
        {
            var slot = Slot(n);
            if (!slot.IsReady()) return;
            var core = slot.View.CoreWebView2;
            if (core is null) return;
            var raw = await EvalAsync(core, AvatarScript);
            if (raw is null) return;
            var url = raw.Trim('"');
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
            var core = slot.View.CoreWebView2;
            if (core is null) return;
            var raw = await EvalAsync(core, VoiceScript);
            if (raw is null) return; // タイムアウト等は状態を維持
            var v = raw.Trim('"');
            bool? voice = v == "1" ? true : v == "0" ? false : null;
            // 判定不能(ロケール未対応・DOM変更)は状態を維持して安全側に倒す(通話中に絞らない)
            if (voice is null) return;
            bool inVoice = voice.Value;
            if (n == 1 ? _inVoice1 == inVoice : _inVoice2 == inVoice) return;
            if (n == 1) _inVoice1 = inVoice; else _inVoice2 = inVoice;
            Diagnostics.Log($"Acc{n} 通話{(inVoice ? "開始" : "終了")}");
            RefreshMemoryLevels();
        }
        catch { }
    }

    private async Task UpdateBadgeAsync(int n)
    {
        var slot = Slot(n);
        try
        {
            var core = slot.View.CoreWebView2;
            if (core is null) return;
            var raw = await EvalAsync(core, BadgeScript);
            if (raw is null)
            {
                _diag.ReportFail("badge"); // 応答なし(ハング/タイムアウト)
                return;
            }
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
                        var uraw = await EvalAsync(core, UnreadScript);
                        if (uraw is not null && int.TryParse(uraw, out var u) && u > 0) count += u;
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
            var state = await EvalAsync(core, "document.readyState");
            if (state is null)
            {
                _diag.ReportFail("page");
                if (allowReload) { try { core.Reload(); } catch { } }
                return;
            }
            _diag.ReportOk("page");
        }
        catch
        {
            _diag.ReportFail("page");
            if (allowReload) { try { view.CoreWebView2?.Reload(); } catch { } }
        }
    }
}
