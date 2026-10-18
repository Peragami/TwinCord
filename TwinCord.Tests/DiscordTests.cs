namespace TwinCord.Tests;

public class DiscordTests
{
    [Theory]
    [InlineData("https://discord.com", true)]
    [InlineData("https://discord.com/channels/@me", true)]
    [InlineData("https://canary.discord.com/channels/@me", true)]
    [InlineData("https://discord.com:443/channels/@me", true)]
    [InlineData("http://discord.com/channels/@me", false)]
    [InlineData("ftp://discord.com/channels/@me", false)]
    [InlineData("file://discord.com/channels/@me", false)]
    [InlineData("https://evil-discord.com/channels/@me", false)]
    [InlineData("https://discord.com.evil.com/channels/@me", false)]
    [InlineData("https://evil.com/discord.com", false)]
    [InlineData("https://discordapp.com/channels/@me", false)]
    [InlineData("discord.com/channels/@me", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsDiscordUri_ReturnsExpected(string? uri, bool expected)
    {
        Assert.Equal(expected, TwinCord.Discord.IsDiscordUri(uri));
    }
}
