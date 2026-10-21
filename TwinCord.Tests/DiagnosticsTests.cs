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
    public void Degraded_IsTrackedPerAccount()
    {
        var diag = new Diagnostics();
        var degraded = new List<(Feature, int)>();
        var recovered = new List<(Feature, int)>();
        diag.Degraded += (f, a) => degraded.Add((f, a));
        diag.Recovered += (f, a) => recovered.Add((f, a));

        for (int i = 0; i < 6; i++) diag.ReportFail(Feature.Badge, 1);
        Assert.Contains(Feature.Badge, diag.DegradedKeys());
        Assert.Equal(new[] { (Feature.Badge, 1) }, degraded);

        // 正常なアカウント2の成功で、アカウント1の失敗を消さない
        diag.ReportOk(Feature.Badge, 2);
        Assert.Contains(Feature.Badge, diag.DegradedKeys());

        diag.ReportOk(Feature.Badge, 1);
        Assert.DoesNotContain(Feature.Badge, diag.DegradedKeys());
        Assert.Equal(new[] { (Feature.Badge, 1) }, recovered);
    }

    [Fact]
    public void DisplayName_IncludesAccountForAccountSpecificFeatures()
    {
        Assert.Equal("アカウント1の未読バッジ", Diagnostics.DisplayName(Feature.Badge, 1));
        Assert.Equal("未読バッジ", Diagnostics.DisplayName(Feature.Badge));
        Assert.Equal("グローバル切替キー(Ctrl+Alt+1/2/M)",
            Diagnostics.DisplayName(Feature.HotkeyGlobal, 0));
    }

    [Fact]
    public void Flush_WritesQueuedLineBeforeReturning()
    {
        // Flushはキューが空でも「書き込み中」の行を待つ(終了時に最後の1行が残らない問題の防止)
        var marker = "flush-test-" + Guid.NewGuid().ToString("N");
        Log.Write(marker);
        Log.Flush();

        var dir = AppSettings.DataFolder();
        var files = Directory.Exists(dir)
            ? Directory.GetFiles(dir, AppConstants.LogBaseName + "*.log")
            : [];
        Assert.True(files.Any(ContainsMarker), "Flush後もログが書き込まれていない");

        bool ContainsMarker(string path)
        {
            try
            {
                // 他のテストが同じログへ並行して追記しているため、共有読み取りにする
                using var fs = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                return reader.ReadToEnd().Contains(marker);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
