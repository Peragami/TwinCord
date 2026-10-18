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
        return u.Host.Equals(Host, StringComparison.OrdinalIgnoreCase)
            || u.Host.EndsWith("." + Host, StringComparison.OrdinalIgnoreCase);
    }
}
