namespace TwinCord.Tests;

/// <summary>アプリ内の変更履歴が埋め込みリソース(docs/changelog.md)から読めることを確認する。</summary>
public class VersionHistoryTests
{
    [Fact]
    public void EmbeddedHistory_ContainsCurrentVersion()
    {
        var history = VersionHistory.Text;

        Assert.False(string.IsNullOrWhiteSpace(history));
        Assert.Contains(Diagnostics.AppVersion(), history);
        Assert.DoesNotContain("バージョニング方針", history);
    }

    [Fact]
    public void EmbeddedResource_IsChangelogNotReadme()
    {
        var names = typeof(VersionHistory).Assembly.GetManifestResourceNames();

        Assert.Contains("TwinCord.CHANGELOG.md", names);
        Assert.DoesNotContain("TwinCord.README.md", names);
    }
}
