using System.IO;
using TwinCord;

namespace TwinCord.Tests;

public class BundleCleanupTests
{
    [Fact]
    public void ActiveFolderUnder_ReturnsFolderDirectlyUnderRoot()
    {
        using var tmp = new TempFolder();
        var root = Path.Combine(tmp.Path, ".net", "TwinCord");
        var current = Path.Combine(root, "0123456789abcdef");
        Directory.CreateDirectory(current);

        var found = BundleCleanup.ActiveFolderUnder(root, new[] { current });

        Assert.Equal(current, found);
    }

    [Fact]
    public void ActiveFolderUnder_IgnoresAnythingButRootChildren()
    {
        using var tmp = new TempFolder();
        var root = Path.Combine(tmp.Path, ".net", "TwinCord");
        var dev = Path.Combine(tmp.Path, "bin", "Release");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(dev);

        // 開発ビルドの出力先(展開方式ではない)
        Assert.Null(BundleCleanup.ActiveFolderUnder(root, new[] { dev }));
        // rootそのもの
        Assert.Null(BundleCleanup.ActiveFolderUnder(root, new[] { root }));
        // 空・null
        Assert.Null(BundleCleanup.ActiveFolderUnder(root, new string?[] { null, "" }));
        // 実在しないフォルダ
        Assert.Null(BundleCleanup.ActiveFolderUnder(root, new[] { Path.Combine(root, "missing") }));
        // 別名のアプリの展開先
        Assert.Null(BundleCleanup.ActiveFolderUnder(root, new[] { Path.Combine(tmp.Path, ".net", "Other", "abc") }));
    }

    [Fact]
    public void DeleteOthers_RemovesStaleFoldersOnly()
    {
        using var tmp = new TempFolder();
        var root = Path.Combine(tmp.Path, "TwinCord");
        var current = Path.Combine(root, "current");
        var stale = Path.Combine(root, "stale");
        Directory.CreateDirectory(Path.Combine(current, "sub"));
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "a.txt"), "x");

        var removed = BundleCleanup.DeleteOthers(root, current);

        Assert.True(Directory.Exists(current));
        Assert.True(Directory.Exists(Path.Combine(current, "sub")));
        Assert.False(Directory.Exists(stale));
        Assert.Equal(1, removed);
    }
}
