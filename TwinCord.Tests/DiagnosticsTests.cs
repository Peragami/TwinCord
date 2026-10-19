using System.IO;
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

    [Fact]
    public void Flush_WritesQueuedLineBeforeReturning()
    {
        // Flushはキューが空でも「書き込み中」の行を待つ(終了時に最後の1行が残らない問題の防止)
        var marker = "flush-test-" + Guid.NewGuid().ToString("N");
        Diagnostics.Log(marker);
        Diagnostics.Flush();

        var dir = AppSettings.DataFolder();
        var files = Directory.Exists(dir)
            ? Directory.GetFiles(dir, AppConstants.LogBaseName + "*.log")
            : [];
        Assert.True(files.Any(f => File.ReadAllText(f).Contains(marker)),
            "Flush後もログが書き込まれていない");
    }
}
