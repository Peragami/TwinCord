using TwinCord.Services;

namespace TwinCord.Tests;

public class AccountAvatarTests
{
    [Theory]
    [InlineData(null, "?")]
    [InlineData("", "?")]
    [InlineData("  ", "?")]
    [InlineData("alice", "A")]
    [InlineData("  Bob  ", "B")]
    public void InitialOf_UsesFirstVisibleCharacter(string? name, string expected)
    {
        Assert.Equal(expected, AccountAvatar.InitialOf(name));
    }

    [Fact]
    public void InitialOf_DoesNotSplitEmojiOrCombiningCharacter()
    {
        // UTF-16の1コード単位で切ると、絵文字が壊れた文字になる
        Assert.Equal("😀", AccountAvatar.InitialOf("😀Alice"));
        Assert.Equal("E\u0301", AccountAvatar.InitialOf("e\u0301clair"));
    }
}
