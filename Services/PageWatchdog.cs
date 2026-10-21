using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;

namespace TwinCord.Services;

/// <summary>
/// 非表示タブの監視タイマー群(バッジ・通話・死活・アバター)とメモリレベル管理。
/// UI更新は渡されたコントロール参照に直接行う。設定は差し替えに追従するため提供関数経由で読む。
/// アバター画像の取得・保存・表示はAccountAvatarに委譲する。
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

    private readonly AccountSlot[] _slots;   // 1始まり(0は未使用)
    private readonly AccountAvatar[] _avatars;
    private readonly Func<AppSettings> _settings;
    private readonly Diagnostics _diag;
    private readonly Func<int> _current;

    private readonly bool[] _inVoice = new bool[3];
    private readonly bool[] _loaded = new bool[3];
    // バッジ/通話の初回確認結果をログに残す(セッション中1回)
    private readonly bool[] _probeLogged = new bool[3];
    private readonly bool[] _avatarFailureLogged = new bool[3];
    private bool _windowHidden;
    private DispatcherTimer? _badgeTimer;
    private DispatcherTimer? _healthTimer;
    private DispatcherTimer? _avatarTimer;
    private bool _badgeBusy;
    private bool _healthBusy;
    private bool _avatarBusy;
    private bool _stopped;
    // 変化が無いときはポーリング間隔を伸ばす(常駐時のCPU/バッテリー節約)
    private int _unchangedPolls;
    private static readonly TimeSpan FastPoll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SlowPoll = TimeSpan.FromSeconds(10);
    private const int SlowAfterPolls = 6; // 30秒変化が無ければ10秒間隔

    public PageWatchdog(AccountSlot acc1, AccountSlot acc2, Func<AppSettings> settings,
        Diagnostics diag, Func<int> current)
    {
        _slots = new AccountSlot[3];
        _slots[1] = acc1;
        _slots[2] = acc2;
        _avatars = new AccountAvatar[3];
        _avatars[1] = new AccountAvatar(1, acc1.ActionButton, acc1.AccountName);
        _avatars[2] = new AccountAvatar(2, acc2.ActionButton, acc2.AccountName);
        _settings = settings;
        _diag = diag;
        _current = current;
    }

    private AccountSlot Slot(int n) => _slots[n];

    public bool InVoice(int n) => _inVoice[n];

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

    public bool HasAvatar(int n) => _avatars[n].HasFile;

    public void RefreshAccountIcons()
    {
        _avatars[1].Apply();
        _avatars[2].Apply();
    }

    public void Start()
    {
        if (_badgeTimer is not null) return;
        _stopped = false;
        _badgeTimer = new DispatcherTimer { Interval = FastPoll };
        _badgeTimer.Tick += BadgeTimer_Tick;
        _badgeTimer.Start();
        _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _healthTimer.Tick += HealthTimer_Tick;
        _healthTimer.Start();
        _avatarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _avatarTimer.Tick += AvatarTimer_Tick;
        _avatarTimer.Start();
    }

    /// <summary>終了時にタイマーを止める(終了処理中の新規発火を防ぐ)。</summary>
    public void Stop()
    {
        _stopped = true;
        _badgeTimer?.Stop();
        _healthTimer?.Stop();
        _avatarTimer?.Stop();
        _badgeTimer = null;
        _healthTimer = null;
        _avatarTimer = null;
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
        for (int n = 1; n <= 2; n++)
        {
            if (!_loaded[n]) continue;
            SetMemoryLevel(Slot(n).View, !_inVoice[n] && (_windowHidden || current != n));
        }
    }

    /// <summary>初回読み込み完了を記録し、以降はメモリ抑制の対象にする。</summary>
    public void SetViewLoaded(int n)
    {
        _loaded[n] = true;
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

    private async void AvatarTimer_Tick(object? sender, EventArgs e)
    {
        if (_stopped || _avatarBusy) return;
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
            await _avatars[n].UpdateAsync(raw);
            _avatarFailureLogged[n] = false;
        }
        catch (Exception ex)
        {
            // オフライン中は30秒ごとに同じ失敗が起きるため、復旧するまで1回だけ記録する
            if (!_avatarFailureLogged[n])
            {
                _avatarFailureLogged[n] = true;
                Log.Write($"アバター更新失敗(acc{n}): {ex.Message}");
            }
        }
    }

    private async void BadgeTimer_Tick(object? sender, EventArgs e)
    {
        if (_stopped || _badgeBusy) return;
        _badgeBusy = true;
        bool changed = false;
        try
        {
            var settings = _settings();
            int current = _current();
            bool includeUnread = settings.CountUnreadChannels;
            bool showBadges = settings.UnreadBadge;

            // 非表示アカウント: バッジ(+通話)。表示アカウント: 通話のみ(バッジは見えない)
            int other = current == 1 ? 2 : 1;
            if (Slot(other).IsReady()) changed |= await PollAsync(other, includeUnread, showBadges);
            if (Slot(current).IsReady()) changed |= await PollAsync(current, includeUnread, updateBadge: false);

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
        for (int n = 1; n <= 2; n++)
        {
            var badge = Slot(n).Badge;
            if (badge.Visibility == Visibility.Collapsed) continue;
            badge.Visibility = Visibility.Collapsed;
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
                _diag.ReportFail(Feature.Badge, n); // 応答なし/スクリプト異常=Discord側の構造変化の疑い
                return false;
            }
            _diag.ReportOk(Feature.Badge, n);
            // 初回だけ結果を残す(Discord側のDOM変更で判定不能になったときに気づけるように)
            if (!_probeLogged[n])
            {
                _probeLogged[n] = true;
                Log.Write($"初回確認(acc{n}): バッジ={probe.Badge} 未読={probe.Unread} 通話判定={probe.Voice}");
            }

            bool changed = ApplyVoice(n, probe);
            if (!updateBadge) return changed;

            int count = probe.Badge;
            if (probe.Unread > 0) count += probe.Unread;
            if (count > 0)
            {
                var text = count > 99 ? "99+" : count.ToString();
                if (slot.BadgeText.Text != text || slot.Badge.Visibility != Visibility.Visible)
                {
                    slot.BadgeText.Text = text;
                    slot.Badge.Visibility = Visibility.Visible;
                    changed = true;
                }
            }
            else if (slot.Badge.Visibility != Visibility.Collapsed)
            {
                slot.Badge.Visibility = Visibility.Collapsed;
                changed = true;
            }
            return changed;
        }
        catch
        {
            _diag.ReportFail(Feature.Badge, n);
            return false;
        }
    }

    private bool ApplyVoice(int n, in PageScripts.ProbeResult probe)
    {
        // 判定不能(ロケール未対応・DOM変更)は状態を維持して安全側に倒す(通話中に絞らない)
        if (!probe.VoiceKnown) return false;
        bool inVoice = probe.InVoice;
        if (_inVoice[n] == inVoice) return false;
        _inVoice[n] = inVoice;
        Log.Write($"Acc{n} 通話{(inVoice ? "開始" : "終了")}");
        RefreshMemoryLevels();
        return true;
    }

    private async void HealthTimer_Tick(object? sender, EventArgs e)
    {
        if (_stopped || _healthBusy) return;
        _healthBusy = true;
        try
        {
            if (!_settings().AutoReload) return;
            // 表示中は再読み込みしない。表示状態はawaitの後に再確認する(待機中に切り替わるため)
            for (int n = 1; n <= 2; n++)
            {
                if (Slot(n).IsReady() && !InVoice(n)) await PingAsync(n, Slot(n).View);
            }
        }
        catch (Exception ex)
        {
            // async voidの例外はアプリ全体へ抜けるため、ここで必ず止める
            Log.Write("ページ死活監視の処理に失敗: " + ex.Message);
        }
        finally { _healthBusy = false; }
    }

    private async Task PingAsync(int n, WpfWebView2 view)
    {
        try
        {
            var core = view.CoreWebView2;
            if (core is null) return;
            var state = await PageScripts.EvalAsync(core, PageScripts.ReadyState);
            if (state is null)
            {
                _diag.ReportFail(Feature.Page, n);
                ReloadIfHidden(n, core);
                return;
            }
            _diag.ReportOk(Feature.Page, n);
        }
        catch
        {
            _diag.ReportFail(Feature.Page, n);
            try { ReloadIfHidden(n, view.CoreWebView2); } catch { }
        }
    }

    /// <summary>
    /// 非表示のときだけ再読み込みする。await中にユーザーが切り替えた場合は、
    /// 読んでいるページを奪わないよう何もしない。
    /// </summary>
    private void ReloadIfHidden(int n, CoreWebView2? core)
    {
        if (core is null) return;
        if (!ShouldReloadHidden(n, _current(), _windowHidden, InVoice(n))) return;
        try { core.Reload(); } catch { }
    }

    /// <summary>
    /// そのページを再読み込みしてよいか。ウィンドウ自体が非表示(トレイ格納・最小化)なら
    /// 表示中アカウントも見えていないため対象に含める。通話中は音声を守るため除外する。
    /// </summary>
    internal static bool ShouldReloadHidden(int account, int current, bool windowHidden, bool inVoice)
    {
        if (account is not (1 or 2) || inVoice) return false;
        return windowHidden || account != current;
    }
}
