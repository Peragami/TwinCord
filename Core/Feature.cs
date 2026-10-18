namespace TwinCord;

/// <summary>劣化監視の対象機能。文字列キーのタイポを防ぐ。</summary>
public enum Feature
{
    Badge,
    /// <summary>グローバルホットキー(Ctrl+Alt+1/2/M)の登録。</summary>
    HotkeyGlobal,
    /// <summary>Discordページ内Ctrl+1/2を拾う注入スクリプト。</summary>
    HotkeyPage,
    Page,
}
