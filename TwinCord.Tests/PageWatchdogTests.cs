using TwinCord.Services;

namespace TwinCord.Tests;

public class PageWatchdogTests
{
    [Theory]
    [InlineData(2, 1, false, false, true)]  // 非表示アカウント
    [InlineData(1, 1, false, false, false)] // 表示中のアカウントは奪わない
    [InlineData(1, 1, true, false, true)]   // ウィンドウごと非表示なら表示中アカウントも対象
    [InlineData(1, 1, true, true, false)]   // 通話中は再読み込みしない
    [InlineData(2, 1, false, true, false)]
    [InlineData(0, 1, true, false, false)]
    [InlineData(3, 1, true, false, false)]
    public void ShouldReloadHidden_ReturnsExpected(
        int account, int current, bool windowHidden, bool inVoice, bool expected)
    {
        Assert.Equal(expected,
            PageWatchdog.ShouldReloadHidden(account, current, windowHidden, inVoice));
    }
}
