using System.IO;
using TwinCord;

namespace TwinCord.Tests;

/// <summary>
/// 自動更新とデータ引き継ぎの安全弁(開発ビルド・単一exeでは行わない)の判定を確認する。
/// </summary>
public class EnvironmentGuardTests
{
    [Fact]
    public void IsDevBuildIn_DetectsBuildOutputFolder()
    {
        using var temp = new TempFolder();
        var exe = Path.Combine(temp.Path, "TwinCord.exe");

        Assert.False(Diagnostics.IsDevBuildIn(exe));

        File.WriteAllText(Path.Combine(temp.Path, "TwinCord.runtimeconfig.dev.json"), "{}");
        Assert.True(Diagnostics.IsDevBuildIn(exe));
    }

    [Fact]
    public void IsDevBuildIn_IgnoresEmptyOrNullPath()
    {
        Assert.False(Diagnostics.IsDevBuildIn(null));
        Assert.False(Diagnostics.IsDevBuildIn(""));
    }

    [Fact]
    public void IsFolderLayoutIn_RequiresAppDllNextToExe()
    {
        using var temp = new TempFolder();
        Assert.False(Updater.IsFolderLayoutIn(temp.Path)); // 単一exe発行のイメージ

        File.WriteAllText(Path.Combine(temp.Path, "TwinCord.dll"), "");
        Assert.True(Updater.IsFolderLayoutIn(temp.Path));
    }

    [Fact]
    public void IsFolderLayoutIn_IgnoresMissingFolder()
    {
        using var temp = new TempFolder();
        Assert.False(Updater.IsFolderLayoutIn(Path.Combine(temp.Path, "missing")));
    }

    [Fact]
    public void BuildUpdateBatLines_ExcludesDataAndModsOnBothSides()
    {
        var lines = Updater.BuildUpdateBatLines();
        var robocopy = Assert.Single(lines, l => l.StartsWith("robocopy", StringComparison.Ordinal));

        // /XDは送信元と送信先の両方のパスを指定する(片方だけだとdataの中身や新規フォルダが消える)
        Assert.Contains("\"%TC_SRC%\\%TC_DATANAME%\"", robocopy);
        Assert.Contains("\"%TC_SRC%\\%TC_MODSNAME%\"", robocopy);
        Assert.Contains("\"%TC_DST%\\%TC_DATANAME%\"", robocopy);
        Assert.Contains("\"%TC_DST%\\%TC_MODSNAME%\"", robocopy);
        Assert.Contains("/MIR", robocopy);
        // 失敗時(8以上)は原因を残して、起動だけはする
        var failure = Assert.Single(lines, l => l.Contains("GEQ 8", StringComparison.Ordinal));
        Assert.Contains("update-error.log", failure);
        Assert.Contains("start \"\" \"%TC_EXE%\"", failure);
        // 反映前にプロセスの終了を待つ
        Assert.Contains(lines, l => l.StartsWith(":wait", StringComparison.Ordinal));
        // batはASCIIのみ(cmdがOEMコードページで読むため)
        Assert.All(lines, l => Assert.All(l, c => Assert.True(c < 128)));
    }
}
