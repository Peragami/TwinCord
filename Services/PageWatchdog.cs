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

    private readonly AccountSlot _acc1;
    private readonly AccountSlot _acc2;
    private readonly Func<AppSettings> _settings;
    private readonly Diagnostics _diag;
    private readonly Func<int> _current;

    private bool _inVoice1;
    private bool _inVoice2;
    private bool _windowHidden;
    // 初回読み込みが終わったアカウントだけメモリ抑制(Low)の対象にする
    private bool _loaded1;
    private bool _loaded2;
    // バッジ/通話の初回確認結果をログに残す(セッション中1回)
    private readonly bool[] _probeLogged = new bool[3];
    private string _avatarUrl1 = "";
    private string _avatarUrl2 = "";
    private DispatcherTimer? _badgeTimer;
    private DispatcherTimer? _healthTimer;
    private DispatcherTimer? _avatarTimer;
    private bool _badgeBusy;
    private bool _healthBusy;
    private bool _avatarBusy;
    // 変化が無いときはポーリング間隔を伸ばす(常駐時のCPU/バッテリー節約)
    private int _unchangedPolls;
    private static readonly TimeSpan FastPoll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SlowPoll = TimeSpan.FromSeconds(10);
    private const int SlowAfterPolls = 6; // 30秒変化が無ければ10秒間隔

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
            // 常駐中の判定と同じスクリプトを使う(自己診断と実際の動作を揃える)
            var raw = await PageScripts.EvalAsync(core, PageScripts.Probe(includeUnread: false));
            if (raw is null) return "実行失敗";
            if (!PageScripts.TryParseProbe(raw, out var probe)) return "スクリプト異常";
            return probe.Badge.ToString();
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
        // 読み込み前のLowは初回表示を遅くするため、初回読み込みが済んだ側にだけ掛ける
        if (_loaded1) SetMemoryLevel(_acc1.View, !_inVoice1 && (_windowHidden || current != 1));
        if (_loaded2) SetMemoryLevel(_acc2.View, !_inVoice2 && (_windowHidden || current != 2));
    }

    /// <summary>初回読み込み完了を記録し、以降はメモリ抑制の対象にする。</summary>
    public void SetViewLoaded(int n)
    {
        if (n == 1) _loaded1 = true; else _loaded2 = true;
        RefreshMemoryLevels();
        WakeUp(); // 表示直後はバッジ/通話を早めに反映する
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

            // ページDOM由来のURLをそのまま取得しない。
            // DiscordのCDNだけを許可し、ローカルファイルや任意ホストへの誘導を防ぐ。
            if (!IsTrustedAvatarUrl(url))
            {
                Diagnostics.Log($"アバターURLを拒否(acc{n}): {url}");
                return;
            }

            var path = AvatarPath(n);
            bool same = n == 1 ? url == _avatarUrl1 : url == _avatarUrl2;
            if (same && File.Exists(path)) return;

            var bytes = await DownloadAvatarAsync(url);
            if (bytes is null || bytes.Length == 0) return;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // 直接上書きせず一時ファイル経由にする(書き込み途中の画像でアイコンが壊れない)
            var tmp = path + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(tmp, bytes);

                // 画像としてデコードできることを確認してから置換する
                var probe = new BitmapImage();
                probe.BeginInit();
                probe.CacheOption = BitmapCacheOption.OnLoad;
                probe.UriSource = new Uri(tmp, UriKind.Absolute);
                probe.DecodePixelWidth = 96;
                probe.EndInit();
                probe.Freeze();

                File.Move(tmp, path, true);
            }
            catch (Exception ex)
            {
                Diagnostics.Log($"アバター保存失敗(acc{n}): {ex.Message}");
                try { File.Delete(tmp); } catch { }
                return;
            }

            if (n == 1) _avatarUrl1 = url; else _avatarUrl2 = url;
            SetAccountIcon(n);
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"アバター更新失敗(acc{n}): {ex.Message}");
        }
    }

    /// <summary>Discordの画像CDN(https・既定ポート)だけを許可する。</summary>
    private static bool IsTrustedAvatarUrl(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort) return false;
        if (!string.IsNullOrEmpty(u.UserInfo)) return false;

        var host = u.Host;
        return host.Equals("discord.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("discordapp.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".discordapp.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("discordapp.net", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".discordapp.net", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>上限付きで画像を取得する。Content-Lengthが信用できない場合も読みながら打ち切る。</summary>
    private static async Task<byte[]?> DownloadAvatarAsync(string url)
    {
        using var cts = new CancellationTokenSource(AppConstants.HttpTimeout);
        using var res = await Http.Shared.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        res.EnsureSuccessStatusCode();
        if (res.Content.Headers.ContentLength is > AppConstants.AvatarMaxBytes) return null;

        await using var stream = await res.Content.ReadAsStreamAsync(cts.Token);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buf.AsMemory(), cts.Token)) > 0)
        {
            if (ms.Length + read > AppConstants.AvatarMaxBytes) return null;
            ms.Write(buf, 0, read);
        }
        return ms.ToArray();
    }

    // アバター画像はデコード済みのBitmapSourceとImageコントロールを使い回す
    // (ボタンをクリックするたびにImageを作り直すとGCと再描画が発生する)
    private readonly Dictionary<int, (DateTime Stamp, BitmapSource Bmp)> _iconCache = new();
    private readonly Dictionary<int, System.Windows.Controls.Image> _iconImages = new();

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
                if (!_iconImages.TryGetValue(n, out var img))
                {
                    img = new System.Windows.Controls.Image
                    {
                        Width = 40,
                        Height = 40,
                        Stretch = System.Windows.Media.Stretch.UniformToFill,
                    };
                    img.Clip = new System.Windows.Media.EllipseGeometry(new System.Windows.Point(20, 20), 20, 20);
                    _iconImages[n] = img;
                }
                if (!ReferenceEquals(img.Source, cached.Bmp)) img.Source = cached.Bmp;
                if (!ReferenceEquals(btn.Content, img)) btn.Content = img;
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
        bool changed = false;
        try
        {
            var settings = _settings();
            int current = _current();
            bool includeUnread = settings.CountUnreadChannels;
            bool showBadges = settings.UnreadBadge;

            // 非表示アカウント: バッジ(+通話)。表示アカウント: 通話のみ(バッジは見えない)
            if (current != 2 && _acc2.IsReady())
                changed |= await PollAsync(2, includeUnread, showBadges);
            if (current != 1 && _acc1.IsReady())
                changed |= await PollAsync(1, includeUnread, showBadges);
            if (current == 1 && _acc1.IsReady())
                changed |= await PollAsync(1, includeUnread, updateBadge: false);
            else if (current == 2 && _acc2.IsReady())
                changed |= await PollAsync(2, includeUnread, updateBadge: false);

            // バッジOFFへ切り替えた直後も、バッジと通話検出とズーム同期は継続する
            if (!showBadges) changed |= HideBadges();

            SyncZoom(1);
            SyncZoom(2);
        }
        catch { }
        finally
        {
            _badgeBusy = false;
            AdaptInterval(changed);
        }
    }

    /// <summary>次のポーリングをすぐ(5秒間隔)に戻す。アカウント切替や初回読み込み完了時に呼ぶ。</summary>
    public void WakeUp()
    {
        _unchangedPolls = 0;
        if (_badgeTimer is not null) _badgeTimer.Interval = FastPoll;
    }

    private void AdaptInterval(bool changed)
    {
        if (_badgeTimer is null) return;
        if (changed)
        {
            _unchangedPolls = 0;
        }
        else if (_unchangedPolls < SlowAfterPolls)
        {
            _unchangedPolls++;
        }
        var want = _unchangedPolls >= SlowAfterPolls ? SlowPoll : FastPoll;
        if (_badgeTimer.Interval != want) _badgeTimer.Interval = want;
    }

    /// <summary>バッジを両アカウント分まとめて消す(設定OFF時)。</summary>
    private bool HideBadges()
    {
        bool changed = false;
        foreach (var slot in new[] { _acc1, _acc2 })
        {
            if (slot.Badge.Visibility == System.Windows.Visibility.Collapsed) continue;
            slot.Badge.Visibility = System.Windows.Visibility.Collapsed;
            changed = true;
        }
        return changed;
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

    /// <summary>
    /// 1回のスクリプト実行でバッジ・未読・通話をまとめて取得する。UIが変わったらtrue。
    /// updateBadge=falseのときも通話判定は更新する(表示中アカウント用)。
    /// </summary>
    private async Task<bool> PollAsync(int n, bool includeUnread, bool updateBadge)
    {
        var slot = Slot(n);
        try
        {
            var core = slot.View.CoreWebView2;
            if (core is null) return false;
            var raw = await PageScripts.EvalAsync(core, PageScripts.Probe(includeUnread));
            if (!PageScripts.TryParseProbe(raw, out var probe))
            {
                _diag.ReportFail(Feature.Badge); // 応答なし/スクリプト異常=Discord側の構造変化の疑い
                return false;
            }
            _diag.ReportOk(Feature.Badge);
            // 初回だけ結果を残す(Discord側のDOM変更で判定不能になったときに気づけるように)
            if (!_probeLogged[n])
            {
                _probeLogged[n] = true;
                Diagnostics.Log($"初回確認(acc{n}): バッジ={probe.Badge} 未読={probe.Unread} 通話判定={probe.Voice}");
            }

            bool changed = ApplyVoice(n, probe);
            if (!updateBadge) return changed;

            int count = probe.Badge;
            if (probe.Unread > 0) count += probe.Unread;
            if (count > 0)
            {
                var text = count > 99 ? "99+" : count.ToString();
                if (slot.BadgeText.Text != text || slot.Badge.Visibility != System.Windows.Visibility.Visible)
                {
                    slot.BadgeText.Text = text;
                    slot.Badge.Visibility = System.Windows.Visibility.Visible;
                    changed = true;
                }
            }
            else if (slot.Badge.Visibility != System.Windows.Visibility.Collapsed)
            {
                slot.Badge.Visibility = System.Windows.Visibility.Collapsed;
                changed = true;
            }
            return changed;
        }
        catch
        {
            _diag.ReportFail(Feature.Badge);
            return false;
        }
    }

    private bool ApplyVoice(int n, in PageScripts.ProbeResult probe)
    {
        // 判定不能(ロケール未対応・DOM変更)は状態を維持して安全側に倒す(通話中に絞らない)
        if (!probe.VoiceKnown) return false;
        bool inVoice = probe.InVoice;
        if (n == 1 ? _inVoice1 == inVoice : _inVoice2 == inVoice) return false;
        if (n == 1) _inVoice1 = inVoice; else _inVoice2 = inVoice;
        Diagnostics.Log($"Acc{n} 通話{(inVoice ? "開始" : "終了")}");
        RefreshMemoryLevels();
        return true;
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
