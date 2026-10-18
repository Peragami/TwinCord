using System.IO;
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

    private readonly AccountSlot _acc1;
    private readonly AccountSlot _acc2;
    private readonly Func<AppSettings> _settings;
    private readonly Diagnostics _diag;
    private readonly Func<int> _current;

    private bool _inVoice1;
    private bool _inVoice2;
    private bool _windowHidden;
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

    public async Task<string> ProbeBadgeAsync(int n)
    {
        try
        {
            var slot = Slot(n);
            var core = slot.View.CoreWebView2;
            if (core is null) return "実行失敗";
            var raw = await PageScripts.EvalAsync(core, PageScripts.Badge);
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

    /// <summary>
    /// ウィンドウが非表示(トレイ格納)/最小化のときは、表示中アカウントもLowにする。
    /// 通話中のアカウントだけは音声優先でNormalのままにする。
    /// </summary>
    public void SetWindowHidden(bool hidden)
    {
        if (_windowHidden == hidden) return;
        _windowHidden = hidden;
        RefreshMemoryLevels();
    }

    public void RefreshMemoryLevels()
    {
        int current = _current();
        SetMemoryLevel(_acc1.View, !_inVoice1 && (_windowHidden || current != 1));
        SetMemoryLevel(_acc2.View, !_inVoice2 && (_windowHidden || current != 2));
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
            if (!slot.IsReady()) return;
            var core = slot.View.CoreWebView2;
            if (core is null) return;
            var raw = await PageScripts.EvalAsync(core, PageScripts.Avatar);
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
            var bytes = await Http.Shared.GetByteArrayAsync(url);
            var path = AvatarPath(n);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
            if (n == 1) _avatarUrl1 = url; else _avatarUrl2 = url;
            SetAccountIcon(n);
        }
        catch { }
    }

    // アバター画像はデコード済みのBitmapSourceを使い回す(毎回のデコードを避ける)
    private readonly Dictionary<int, (DateTime Stamp, BitmapSource Bmp)> _iconCache = new();

    private void SetAccountIcon(int n)
    {
        var slot = Slot(n);
        var btn = slot.ActionButton;
        var path = AvatarPath(n);
        if (File.Exists(path))
        {
            try
            {
                var stamp = File.GetLastWriteTimeUtc(path);
                if (!_iconCache.TryGetValue(n, out var cached) || cached.Stamp != stamp)
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(path);
                    bmp.DecodePixelWidth = 96;
                    bmp.EndInit();
                    bmp.Freeze();
                    cached = (stamp, bmp);
                    _iconCache[n] = cached;
                }
                var img = new System.Windows.Controls.Image
                {
                    Width = 40,
                    Height = 40,
                    Stretch = System.Windows.Media.Stretch.UniformToFill,
                    Source = cached.Bmp,
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

    public static string AvatarPath(int n) => Path.Combine(
        AppSettings.DataFolder(), AppConstants.AvatarFolder, $"acc{n}.png");

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
            var raw = await PageScripts.EvalAsync(core, PageScripts.Voice);
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
            var raw = await PageScripts.EvalAsync(core, PageScripts.Badge);
            if (raw is null)
            {
                _diag.ReportFail(Feature.Badge); // 応答なし(ハング/タイムアウト)
                return;
            }
            if (int.TryParse(raw, out var count))
            {
                if (count < 0)
                {
                    _diag.ReportFail(Feature.Badge); // スクリプト内部異常=Discord側の構造変化の疑い
                    return;
                }
                if (_settings().CountUnreadChannels)
                {
                    try
                    {
                        var uraw = await PageScripts.EvalAsync(core, PageScripts.Unread);
                        if (uraw is not null && int.TryParse(uraw, out var u) && u > 0) count += u;
                    }
                    catch { }
                }
                _diag.ReportOk(Feature.Badge);
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
            _diag.ReportFail(Feature.Badge);
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
            var state = await PageScripts.EvalAsync(core, PageScripts.ReadyState);
            if (state is null)
            {
                _diag.ReportFail(Feature.Page);
                if (allowReload) { try { core.Reload(); } catch { } }
                return;
            }
            _diag.ReportOk(Feature.Page);
        }
        catch
        {
            _diag.ReportFail(Feature.Page);
            if (allowReload) { try { view.CoreWebView2?.Reload(); } catch { } }
        }
    }
}
