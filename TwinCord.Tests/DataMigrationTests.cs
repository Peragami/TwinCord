using System.IO;

namespace TwinCord.Tests;

public class DataMigrationTests
{
    [Fact]
    public void ShouldOffer_OnlyWhenLegacyHasSettingsAndDataHasNone()
    {
        using var temp = new TempFolder();
        var data = Path.Combine(temp.Path, "data");
        var legacy = Path.Combine(temp.Path, "legacy");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(legacy);

        Assert.False(DataMigration.ShouldOffer(data, legacy)); // 旧データ無し

        File.WriteAllText(Path.Combine(legacy, AppConstants.SettingsFileName), "{}");
        Assert.True(DataMigration.ShouldOffer(data, legacy));

        // 今の場所に設定がある(既に使っている)なら聞かない
        File.WriteAllText(Path.Combine(data, AppConstants.SettingsFileName), "{}");
        Assert.False(DataMigration.ShouldOffer(data, legacy));
    }

    [Fact]
    public void ShouldOffer_IsSkippedAfterMarkedDone()
    {
        using var temp = new TempFolder();
        var data = Path.Combine(temp.Path, "data");
        var legacy = Path.Combine(temp.Path, "legacy");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, AppConstants.SettingsFileName), "{}");

        DataMigration.MarkDone(data, "テスト");

        Assert.True(File.Exists(Path.Combine(data, ".migrated")));
        Assert.False(DataMigration.ShouldOffer(data, legacy));
    }

    [Fact]
    public void Migrate_MovesEverythingAndRemovesEmptiedLegacyFolder()
    {
        using var temp = new TempFolder();
        var data = Path.Combine(temp.Path, "data");
        var legacy = Path.Combine(temp.Path, "legacy");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(Path.Combine(legacy, "avatars"));
        File.WriteAllText(Path.Combine(legacy, AppConstants.SettingsFileName), "{}");
        File.WriteAllText(Path.Combine(legacy, "avatars", "acc1.png"), "img");

        var result = DataMigration.Migrate(data, legacy);

        Assert.True(File.Exists(Path.Combine(data, AppConstants.SettingsFileName)));
        Assert.True(File.Exists(Path.Combine(data, "avatars", "acc1.png")));
        Assert.False(Directory.Exists(legacy)); // 空になった旧フォルダは片付ける
        Assert.Contains("移動 2件", result);
    }

    [Fact]
    public void Migrate_KeepsFilesAlreadyInDataFolder()
    {
        using var temp = new TempFolder();
        var data = Path.Combine(temp.Path, "data");
        var legacy = Path.Combine(temp.Path, "legacy");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(data, AppConstants.SettingsFileName), "new");
        File.WriteAllText(Path.Combine(legacy, AppConstants.SettingsFileName), "old");
        File.WriteAllText(Path.Combine(legacy, "diagnostics.log"), "log");

        var result = DataMigration.Migrate(data, legacy);

        Assert.Equal("new", File.ReadAllText(Path.Combine(data, AppConstants.SettingsFileName)));
        Assert.Equal("log", File.ReadAllText(Path.Combine(data, "diagnostics.log")));
        Assert.True(Directory.Exists(legacy)); // 残したものがあるので旧フォルダは残る
        Assert.Contains("移動 1件", result);
    }

    [Fact]
    public void DataFolder_IsUnderTheAppFolder()
    {
        Assert.Equal(
            Path.Combine(AppSettings.AppDir(), AppConstants.DataFolderName),
            AppSettings.DataFolder());
        Assert.Equal(
            Path.Combine(AppSettings.DataFolder(), AppConstants.SettingsFileName),
            AppSettings.FilePath());
    }
}
