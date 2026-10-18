using TwinCord;

namespace TwinCord.Tests;

public class DiagnosticsTests
{
    [Theory]
    [InlineData(Feature.Badge, "未読バッジ")]
    [InlineData(Feature.HotkeyGlobal, "グローバル切替キー(Ctrl+Alt+1/2/M)")]
    [InlineData(Feature.HotkeyPage, "ページ内切替キー(Ctrl+1/2)")]
    [InlineData(Feature.Page, "ページ死活監視")]
    public void DisplayName_ReturnsFeatureLabel(Feature feature, string expected)
    {
        Assert.Equal(expected, Diagnostics.DisplayName(feature));
    }

    [Fact]
    public void Feature_NoLongerHasCombinedHotkey()
    {
        var names = Enum.GetNames<Feature>();
        Assert.DoesNotContain("Hotkey", names);
        Assert.Contains("HotkeyGlobal", names);
        Assert.Contains("HotkeyPage", names);
    }
}
