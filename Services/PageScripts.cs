using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace TwinCord.Services;

/// <summary>
/// Discordページへ注入するスクリプトと、その実行ラッパ。
/// 文字列はraw string literalで整形し、セレクタや分岐を1行ずつ読めるようにしている。
/// </summary>
public static class PageScripts
{
    /// <summary>ページ内Ctrl+1/2をホストへ転送する(トップフレームのみ)。</summary>
    public const string Hotkey = """
        (function () {
          try { if (window.top !== window) return; } catch (e) { return; }
          window.addEventListener('keydown', function (e) {
            if (e.ctrlKey && !e.shiftKey && !e.altKey && (e.key === '1' || e.key === '2')) {
              var t = e.target;
              if (t) {
                var tn = (t.tagName || '').toUpperCase();
                if (tn === 'INPUT' || tn === 'TEXTAREA') return;
                if (t.closest && t.closest('[contenteditable="true"]')) return;
              }
              chrome.webview.postMessage('switch:' + e.key);
            }
          });
        })();
        """;

    /// <summary>
    /// デスクトップ通知を横取りしてホストへ転送する(トップフレームのみ)。
    /// 本物のNotificationは作らない(二重通知防止のためダミーを返す)。
    /// </summary>
    public const string Notify = """
        (function () {
          try {
            if (window.top !== window) return;
            window.__tcNotifyHook = 1;

            function send(t, o) {
              try {
                chrome.webview.postMessage('notify:' + JSON.stringify({
                  title: t || '',
                  body: (o && o.body) || ''
                }));
              } catch (e) { }
            }

            // Permissions API でも granted に見せる(Discordが権限判定で止まらないように)
            try {
              var oq = navigator.permissions.query.bind(navigator.permissions);
              navigator.permissions.query = function (d) {
                if (d && d.name === 'notifications') {
                  return Promise.resolve({
                    state: 'granted', name: 'notifications', onchange: null,
                    addEventListener: function () { }, removeEventListener: function () { }
                  });
                }
                return oq(d);
              };
            } catch (e) { }

            var stub = {
              close: function () { },
              addEventListener: function () { },
              removeEventListener: function () { }
            };
            function Hooked(t, o) { send(t, o); return stub; }
            Hooked.permission = 'granted';
            Hooked.requestPermission = function () { return Promise.resolve('granted'); };
            window.Notification = Hooked;

            // ページからService Worker経由で出す通知も拾う。
            // ここで本物のshowNotificationを呼ぶと、ネイティブ側のNotificationReceivedと
            // 二重に表示されるため、ホストへ転送したら表示済みのPromiseを返す。
            try {
              var P = window.ServiceWorkerRegistration && ServiceWorkerRegistration.prototype;
              if (P && P.showNotification) {
                P.showNotification = function (t, o) {
                  send(t, o);
                  return Promise.resolve();
                };
              }
            } catch (e) { }
          } catch (e) { }
        })();
        """;

    /// <summary>
    /// サーバーリストの「アプリをダウンロード」ボタンを隠す(ベストエフォート)。
    /// 文書ごとに「適用/解除」できる関数を用意し、今の設定はホストが HideDownloadApply で渡す。
    /// (設定を切り替えたときも、再読み込み後の新しい文書にも同じ関数で反映できる)
    /// </summary>
    public const string HideDownload = """
        (function () {
          try { if (window.top !== window) return; } catch (e) { return; }

          // 文書ごとに1回だけ用意する(再実行してもstyleタグ・MutationObserverは二重にならない)
          if (window.__tcHideDlApply) return;

          var on = false;
          var style = null;
          var observer = null;
          var hidden = [];

          // 実体はdiv系要素のため、短文テキスト一致＋属性判定で探す(祖先巻き込み防止に文字数制限)
          function isDl(el) {
            try {
              var g = function (n) {
                try { return el.getAttribute(n) || ''; } catch (x) { return ''; }
              };
              var al = g('aria-label') + '|' + g('title');
              if (/download/i.test(al) && /apps?/i.test(al)) return true;
              if (al.indexOf('ダウンロード') >= 0 && al.indexOf('アプリ') >= 0) return true;
              // 全DOM走査の負荷を下げるため、大きい入れ物はテキストを読まずに除外する
              if (el.childElementCount > 4) return false;
              var t = '';
              try { t = (el.textContent || '').trim(); } catch (x) { }
              if (t.length > 0 && t.length <= 30) {
                if (t === 'アプリをダウンロード') return true;
                if (/download apps?/i.test(t)) return true;
              }
              return false;
            } catch (e) { return false; }
          }

          function kill() {
            try {
              var next = [];
              var els = document.querySelectorAll('nav div,div[role="button"],div[role="treeitem"],div[tabindex],button,a');
              for (var i = 0; i < els.length; i++) {
                if (isDl(els[i])) {
                  els[i].style.setProperty('display', 'none', 'important');
                  next.push(els[i]);
                }
              }
              // 解除時に元へ戻せるよう、隠した要素だけを持つ(再描画で消えた要素は残さない)
              hidden = next;
            } catch (e) { }
          }

          // DOM変化があったときだけ、最大2秒に1回走らせる(無変化なら実行しない)
          function q() {
            if (q.t) return;
            q.t = setTimeout(function () { q.t = 0; kill(); }, 2000);
          }

          function enable() {
            if (on) return;
            on = true;
            try {
              if (!style) {
                style = document.createElement('style');
                style.textContent = 'a[href="/download"],a[href^="/download?"],a[href*="discord.com/download"]{display:none!important}';
                (document.head || document.documentElement).appendChild(style);
              }
            } catch (e) { }
            try {
              kill();
              if (!observer) observer = new MutationObserver(q);
              observer.observe(document.documentElement, { childList: true, subtree: true });
            } catch (e) { }
          }

          function disable() {
            if (!on) return;
            on = false;
            try { if (observer) observer.disconnect(); } catch (e) { }
            try { if (style) style.remove(); style = null; } catch (e) { }
            try {
              for (var i = 0; i < hidden.length; i++) {
                var el = hidden[i];
                if (el && el.style) el.style.removeProperty('display');
              }
            } catch (e) { }
            hidden = [];
          }

          window.__tcHideDlApply = function (want) {
            try { if (want) enable(); else disable(); return true; } catch (e) { return false; }
          };
        })();
        """;

    /// <summary>HideDownloadが用意した関数へ今の設定を渡す(ホスト側から実行する)。</summary>
    public static string HideDownloadApply(bool hide) =>
        $"(window.__tcHideDlApply && window.__tcHideDlApply({(hide ? "true" : "false")}))";

    /// <summary>
    /// バッジ・未読・通話を1回のDOM走査でまとめて取得する(バックグラウンドのポーリング負荷を下げる)。
    /// 戻り値はJSON文字列 {'b':バッジ数,'u':未読数,'v':'1'(通話中)/'0'(なし)/その他(判定不能)}。
    /// </summary>
    public static string Probe(bool includeUnread) => includeUnread ? _probeWithUnread : _probe;

    /// <summary>ポーリング結果。Voiceは'1'(通話中)/'0'(なし)/それ以外(判定不能)。</summary>
    public readonly record struct ProbeResult(int Badge, int Unread, string Voice)
    {
        public bool VoiceKnown => Voice is "0" or "1";
        public bool InVoice => Voice == "1";
    }

    /// <summary>ExecuteScriptAsyncの戻り値(JSON文字列)を解釈する。解釈できない/異常値ならfalse。</summary>
    public static bool TryParseProbe(string? raw, out ProbeResult result)
    {
        result = default;
        if (string.IsNullOrEmpty(raw)) return false;
        try
        {
            // 戻り値はJSONエンコードされた文字列で、その中身がさらにJSON
            var inner = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrEmpty(inner)) return false;
            using var doc = JsonDocument.Parse(inner);
            var root = doc.RootElement;
            if (!root.TryGetProperty("b", out var b) || !root.TryGetProperty("v", out var v)) return false;
            if (!b.TryGetInt32(out var badge) || badge < 0) return false; // 負値はスクリプト内部異常
            int unread = 0;
            if (root.TryGetProperty("u", out var u)) u.TryGetInt32(out unread);
            result = new ProbeResult(badge, unread, v.GetString() ?? "");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>マイページのアバターURL(size=64)。</summary>
    public const string Avatar = """
        (() => {
          try {
            var btn = document.querySelector('[aria-label="ユーザー設定"],[aria-label="User Settings"]');
            var scope = btn ? btn.parentElement : null;
            var img = null;
            for (var i = 0; i < 4 && scope; i++) {
              img = scope.querySelector('img[src*="avatars/"]');
              if (img) break;
              scope = scope.parentElement;
            }
            if (!img) {
              var all = document.querySelectorAll('[class*="panels"] img[src*="avatars/"]');
              if (all.length) img = all[0];
            }
            if (!img) return '';
            var src = img.src || '';
            if (!src) return '';
            return src.split('?')[0] + '?size=64';
          } catch (e) { return ''; }
        })()
        """;

    /// <summary>通知フックの診断(JSON文字列)。</summary>
    public const string NotificationDiagnostic = """
        (function () {
          try {
            var p = (typeof Notification !== 'undefined') ? Notification.permission : 'none';
            return JSON.stringify({ hook: !!window.__tcNotifyHook, perm: p });
          } catch (e) { return 'ERR'; }
        })()
        """;

    public const string ReadyState = "document.readyState";

    // 切断ボタンのaria-labelはロケール依存のため主要言語を列挙する
    private static readonly string[] DisconnectLabels =
    [
        "切断", "Disconnect", "Desconectar", "Déconnecter", "Trennen", "Disconnetti",
        "Desligar", "Verbinding verbreken", "Rozłącz", "Отключиться", "연결 해제",
        "断开连接", "斷開連接", "Bağlantıyı Kes", "Ngắt", "ตัดการเชื่อมต่อ",
        "Putuskan", "Deconectare", "Αποσύνδεση", "Leválasztás", "Odpojit",
        "Katkaise", "Koble fra", "Afbryd", "Koppla från",
    ];

    private static string BuildVoiceSelector()
    {
        var parts = new List<string>
        {
            // ロケール非依存のRTCパネル要素を保険として見る
            "[class*=\"rtcConnectionStatus\"]",
            "[class*=\"voicePanel\"]",
            "[class*=\"callContainer\"]",
            "[aria-label*=\"Від\"]",
        };
        foreach (var label in DisconnectLabels)
        {
            // 引用符と改行だけ最小限にエスケープして属性セレクタへ埋め込む
            parts.Add($"[aria-label=\"{label.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"]");
        }
        return string.Join(",", parts);
    }

    // Probeは起動時に一度だけ組み立てる(DisconnectLabelsの初期化より後に置く必要がある)
    private static readonly string _probe = BuildProbe(includeUnread: false);
    private static readonly string _probeWithUnread = BuildProbe(includeUnread: true);

    // 未読チャンネル数の走査(Probeへ埋め込む)
    private const string UnreadScan = """
        const seenU = new Set();
        const us = ['[class*=unreadBar]', '[class*=unreadPill]', '[aria-label*=未読]',
                    '[aria-label*=unread]', '[aria-label*=Unread]'];
        for (const s of us) {
          try {
            document.querySelectorAll(s).forEach(function (el) {
              if (seenU.has(el)) return;
              seenU.add(el);
              unread++;
            });
          } catch (_) { }
        }
        """;

    private static string BuildProbe(bool includeUnread)
    {
        var voice = JsonSerializer.Serialize(BuildVoiceSelector(), new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        var unread = includeUnread ? UnreadScan : "";
        return $$"""
            (() => {
              try {
                let badge = 0;
                const seen = new Set();
                const bs = ['[class*=numberBadge]', '[class*=unreadMention]', '[aria-label*=メンション]',
                            '[aria-label*=mention]', '[aria-label*=Mention]'];
                for (const s of bs) {
                  try {
                    document.querySelectorAll(s).forEach(function (el) {
                      if (seen.has(el)) return;
                      seen.add(el);
                      const t = ((el.innerText || el.textContent) || '').trim();
                      const d = t.match(/\d+/);
                      badge += d ? parseInt(d[0], 10) : 1;
                    });
                  } catch (_) { }
                }
                let unread = 0;
                {{unread}}
                // 通話中なら「切断」ボタン(またはロケール非依存のRTC要素)がDOMにある。
                // セレクタ異常時は判定不能('?')にして状態を維持する(バッジ側を巻き込まない)
                let voice = '?';
                try { voice = document.querySelector({{voice}}) ? '1' : '0'; } catch (_) { voice = '?'; }
                return JSON.stringify({ b: badge, u: unread, v: voice });
              } catch (e) { return 'ERR'; }
            })()
            """;
    }

    /// <summary>
    /// スクリプト実行の共通ラッパ。タイムアウト/例外時はnullを返す。
    /// awaitしっぱなしになると監視が永久に止まるため、必ず時間で打ち切る。
    /// </summary>
    public static async Task<string?> EvalAsync(CoreWebView2 core, string script,
        int timeoutMs = AppConstants.EvalTimeoutMs)
    {
        try
        {
            var task = core.ExecuteScriptAsync(script);
            if (await Task.WhenAny(task, Task.Delay(timeoutMs)) != task)
            {
                // 打ち切った後もCOM側の処理は続くため、例外だけは観測しておく(蓄積・未処理例外の防止)
                _ = task.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
                return null;
            }
            return (await task)?.Trim();
        }
        catch
        {
            return null;
        }
    }
}
