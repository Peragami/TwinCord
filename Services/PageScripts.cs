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

            // ページからService Worker経由で出す通知も拾う
            try {
              var P = window.ServiceWorkerRegistration && ServiceWorkerRegistration.prototype;
              if (P && P.showNotification) {
                var orig = P.showNotification;
                P.showNotification = function (t, o) {
                  send(t, o);
                  return orig.apply(this, arguments);
                };
              }
            } catch (e) { }
          } catch (e) { }
        })();
        """;

    /// <summary>サーバーリストの「アプリをダウンロード」ボタンを隠す(ベストエフォート)。</summary>
    public const string HideDownload = """
        (function () {
          try { if (window.top !== window) return; } catch (e) { return; }

          try {
            var css = 'a[href="/download"],a[href^="/download?"],a[href*="discord.com/download"]{display:none!important}';
            var st = document.createElement('style');
            st.textContent = css;
            (document.head || document.documentElement).appendChild(st);
          } catch (e) { }

          // 実体はdiv系要素のため、短文テキスト一致＋属性判定で探す(祖先巻き込み防止に文字数制限)
          function isDl(el) {
            try {
              var g = function (n) {
                try { return el.getAttribute(n) || ''; } catch (x) { return ''; }
              };
              var al = g('aria-label') + '|' + g('title');
              if (/download/i.test(al) && /apps?/i.test(al)) return true;
              if (al.indexOf('ダウンロード') >= 0 && al.indexOf('アプリ') >= 0) return true;
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
              var els = document.querySelectorAll('nav div,div[role="button"],div[role="treeitem"],div[tabindex],button,a');
              for (var i = 0; i < els.length; i++) {
                if (isDl(els[i])) els[i].style.setProperty('display', 'none', 'important');
              }
            } catch (e) { }
          }

          // DOM変化があったときだけ、最大2秒に1回走らせる(無変化なら実行しない)
          function q() {
            if (q.t) return;
            q.t = setTimeout(function () { q.t = 0; kill(); }, 2000);
          }

          try {
            kill();
            new MutationObserver(q).observe(document.documentElement, { childList: true, subtree: true });
          } catch (e) { }
        })();
        """;

    /// <summary>通話中か('1'=通話中 / '0'=なし / '-1'=判定不能)。</summary>
    public static readonly string Voice = BuildVoice();

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

    /// <summary>未読チャンネル数(ベストエフォート)。</summary>
    public const string Unread = """
        (() => {
          try {
            const sels = ['[class*=unreadBar]', '[class*=unreadPill]', '[aria-label*=未読]',
                          '[aria-label*=unread]', '[aria-label*=Unread]'];
            const seen = new Set();
            let n = 0;
            for (const s of sels) {
              try {
                document.querySelectorAll(s).forEach(function (el) {
                  if (seen.has(el)) return;
                  seen.add(el);
                  n++;
                });
              } catch (_) { }
            }
            return n;
          } catch (_) { return -1; }
        })()
        """;

    /// <summary>未読・メンション数(ベストエフォート)。</summary>
    public const string Badge = """
        (() => {
          try {
            let n = 0;
            const seen = new Set();
            const sels = ['[class*=numberBadge]', '[class*=unreadMention]', '[aria-label*=メンション]',
                          '[aria-label*=mention]', '[aria-label*=Mention]'];
            for (const s of sels) {
              try {
                document.querySelectorAll(s).forEach(function (el) {
                  if (seen.has(el)) return;
                  seen.add(el);
                  const t = ((el.innerText || el.textContent) || '').trim();
                  const d = t.match(/\d+/);
                  n += d ? parseInt(d[0], 10) : 1;
                });
              } catch (_) { }
            }
            return n;
          } catch (_) { return -1; }
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

    private static string BuildVoice()
    {
        // JSONの配列リテラルはそのままJSの配列として使える(引用符の手書きエスケープを避ける)
        var labels = JsonSerializer.Serialize(DisconnectLabels, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        return $$"""
            (() => {
              try {
                const labels = {{labels}};
                for (const l of labels) {
                  if (document.querySelector('[aria-label="' + l + '"]')) return '1';
                }
                // ロケール非依存のRTCパネル要素を保険として見る
                if (document.querySelector('[aria-label*="Від"]')) return '1';
                if (document.querySelector('[class*="rtcConnectionStatus"],[class*="voicePanel"],[class*="callContainer"]')) return '1';
                return '0';
              } catch (e) { return '-1'; }
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
