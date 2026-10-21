namespace TwinCord;

/// <summary>
/// Discordの固定URLとホスト判定。部分一致("evil-discord.com" 等)を避けるため、
/// ホスト名の完全一致またはサブドメインだけを許可する。
/// </summary>
public static class Discord
{
    public const string Host = "discord.com";
    public const string HomeUrl = "https://discord.com/channels/@me";

    public static bool IsDiscordUri(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return false;
        // file: や独自スキームでホスト名だけ discord.com に見せるURLを許さない
        if (u.Scheme != Uri.UriSchemeHttps) return false;
        return u.Host.Equals(Host, StringComparison.OrdinalIgnoreCase)
            || u.Host.EndsWith("." + Host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// トップレベル遷移をアプリ内で許可してよいURLか。DiscordのHTTPSとabout:blankだけを許し、
    /// 外部サイトはNewWindowRequested側で既定ブラウザへ逃がす。
    /// </summary>
    public static bool IsAllowedNavigation(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return true;
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u)
            && u.Scheme == "about"
            && u.AbsolutePath.Equals("blank", StringComparison.OrdinalIgnoreCase))
            return true;
        return IsDiscordUri(uri);
    }
}
