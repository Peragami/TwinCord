namespace TwinCord.Services;

/// <summary>
/// 同じ通知が複数経路(ページのJSフックとWebView2のネイティブ通知)から届いたときに
/// 二重表示しないための判定。
/// 同じ経路から続けて届いた同じ内容は「別の通知」として扱う(本文が同じ別メッセージを
/// 落とさないため)。UIスレッドからのみ呼ぶ。
/// </summary>
public sealed class NotificationDedup
{
    public enum Origin
    {
        /// <summary>注入スクリプト(Notificationのフック)経由。</summary>
        Page,

        /// <summary>WebView2のNotificationReceived経由。</summary>
        Native,
    }

    private const int MaxEntries = 64;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _window;
    private readonly Dictionary<string, (DateTime At, Origin From)> _recent = new();

    public NotificationDedup(TimeSpan window) => _window = window;

    /// <summary>保持している判定履歴の件数(テスト用)。</summary>
    public int Count => _recent.Count;

    /// <summary>重複(表示しない)と判定したらtrue。</summary>
    public bool IsDuplicate(int account, string title, string body, Origin origin, DateTime now)
    {
        var key = Key(account, title, body);
        if (_recent.TryGetValue(key, out var last)
            && last.From != origin
            && now - last.At < _window)
            return true;

        _recent[key] = (now, origin);
        if (_recent.Count > MaxEntries) Prune(now);
        return false;
    }

    private static string Key(int account, string title, string body)
        => account + "\u001f" + title + "\u001f" + body;

    private void Prune(DateTime now)
    {
        foreach (var stale in _recent
            .Where(kv => now - kv.Value.At > StaleAfter)
            .Select(kv => kv.Key).ToList())
            _recent.Remove(stale);
        if (_recent.Count <= MaxEntries) return;

        // 通知が大量に来ても肥大しないよう、古い順に半分まで落とす
        foreach (var old in _recent.OrderBy(kv => kv.Value.At)
            .Take(_recent.Count - MaxEntries / 2)
            .Select(kv => kv.Key).ToList())
            _recent.Remove(old);
    }
}
