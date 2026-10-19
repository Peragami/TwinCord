using System.IO;
using System.Text.Json;
using TwinCord;

namespace TwinCord.Tests;

public class SettingsTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  Alice  ", "Alice")]
    [InlineData("A\nB\tC", "ABC")]
    [InlineData("A\0B", "AB")]
    [InlineData("  ", "")]
    public void CleanName_RemovesControlCharsAndTrims(string? raw, string expected)
    {
        Assert.Equal(expected, AppSettings.CleanName(raw));
    }

    [Fact]
    public void CleanName_TruncatesToMaxLength()
    {
        var raw = "  " + new string('x', 30) + "  ";
        Assert.Equal(new string('x', AppSettings.NameMaxLength), AppSettings.CleanName(raw));
    }

    [Theory]
    [InlineData(0.0, 0.5)]
    [InlineData(0.49, 0.5)]
    [InlineData(0.5, 0.5)]
    [InlineData(1.0, 1.0)]
    [InlineData(2.0, 2.0)]
    [InlineData(2.01, 2.0)]
    public void ClampZoom_ClampsToRange(double input, double expected)
    {
        Assert.Equal(expected, AppSettings.ClampZoom(input), 10);
    }

    [Fact]
    public void ClampZoom_NonFiniteValuesFallBackToDefault()
    {
        Assert.Equal(1.0, AppSettings.ClampZoom(double.NaN));
        Assert.Equal(1.0, AppSettings.ClampZoom(double.PositiveInfinity));
        Assert.Equal(1.0, AppSettings.ClampZoom(double.NegativeInfinity));
    }

    [Fact]
    public void Defaults_MatchDocumentedBehavior()
    {
        var s = new AppSettings();
        Assert.True(s.MinimizeToTray);
        Assert.True(s.UnreadBadge);
        Assert.True(s.AutoReload);
        Assert.True(s.AutoUpdateCheck);
        Assert.True(s.NotifyEnabled);
        Assert.True(s.SkipViewedAccount);
        Assert.True(s.HideDownloadButton);
        Assert.False(s.StartWithWindows);
        Assert.False(s.CountUnreadChannels);
        Assert.False(s.StartMinimized);
        Assert.False(s.NotifyHiddenOnly);
        Assert.Equal("Acc1", s.Acc1Name);
        Assert.Equal("Acc2", s.Acc2Name);
        Assert.Equal(1.0, s.Zoom1);
        Assert.Equal(1.0, s.Zoom2);
        Assert.Equal(AppSettings.CurrentSettingsVersion, s.SettingsVersion);
    }

    [Fact]
    public void LoadFrom_MovesCorruptFileToBackupAndUsesDefaults()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, AppConstants.SettingsFileName);
        // JSONとして壊れている(かつ読めない)設定を用意する
        File.WriteAllText(path, "{ 壊れている ");

        var s = AppSettings.LoadFrom(path);

        // 既定値で起動し、元のファイルは .bak に残る(次の保存で上書きされる前に退避)
        Assert.True(s.MinimizeToTray);
        Assert.Equal(AppSettings.CurrentSettingsVersion, s.SettingsVersion);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".bak"));
        Assert.Contains("壊れている", File.ReadAllText(path + ".bak"));
    }

    [Fact]
    public void LoadFrom_TreatsMissingVersionAsCurrent()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, AppConstants.SettingsFileName);
        // 形式版が無い(旧形式の)設定ファイル
        File.WriteAllText(path, """{"Acc1Name":"Old"}""");

        var s = AppSettings.LoadFrom(path);

        Assert.Equal("Old", s.Acc1Name);
        Assert.Equal(AppSettings.CurrentSettingsVersion, s.SettingsVersion);
        // 壊れていないので退避しない
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        var original = new AppSettings { Acc1Name = "A", Zoom1 = 1.5, WinWidth = 800 };
        var clone = original.Clone();

        clone.Acc1Name = "B";
        clone.Zoom1 = 2.0;
        clone.WinWidth = 1000;

        Assert.NotSame(original, clone);
        Assert.Equal("A", original.Acc1Name);
        Assert.Equal(1.5, original.Zoom1);
        Assert.Equal(800, original.WinWidth);
        Assert.Equal("B", clone.Acc1Name);
        Assert.Equal(2.0, clone.Zoom1);
        Assert.Equal(1000, clone.WinWidth);
    }

    [Fact]
    public void CopyFrom_CopiesAllProperties()
    {
        var source = new AppSettings
        {
            StartWithWindows = true,
            MinimizeToTray = false,
            UnreadBadge = false,
            AutoReload = false,
            CountUnreadChannels = true,
            StartMinimized = true,
            AutoUpdateCheck = false,
            NotifyEnabled = false,
            SkipViewedAccount = false,
            NotifyHiddenOnly = true,
            HideDownloadButton = false,
            Acc1Name = "Alice",
            Acc2Name = "Bob",
            NamesMigrated = true,
            SettingsVersion = 2,
            Zoom1 = 1.25,
            Zoom2 = 0.75,
            WinLeft = 10,
            WinTop = 20,
            WinWidth = 800,
            WinHeight = 600,
            WinMaximized = true,
        };
        var target = new AppSettings();

        target.CopyFrom(source);

        Assert.Equal(JsonSerializer.Serialize(source), JsonSerializer.Serialize(target));
    }
}
