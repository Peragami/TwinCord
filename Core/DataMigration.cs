using System.IO;

namespace TwinCord;

/// <summary>
/// 旧バージョン(1.10.xまで)のデータを、ポータブル形式のdataフォルダへ引き継ぐ。
///
/// 1.10.xまでは %LOCALAPPDATA%\TwinCord に保存していた。zip配布(ポータブル形式)へ
/// 移行したあと初回起動時に一度だけ確認し、結果を data\.migrated に残す(毎回は聞かない)。
/// 移動できない場合(別ドライブなど)はコピーし、元は消さずに残す。
/// </summary>
public static class DataMigration
{
    private const string MarkerName = ".migrated";

    /// <summary>旧バージョンのデータフォルダ。</summary>
    public static string LegacyFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppConstants.AppName);

    /// <summary>
    /// 引き継ぎを持ちかけるべきか。既に引き継ぎ済み、または今の場所に設定があるなら何もしない。
    /// </summary>
    internal static bool ShouldOffer(string dataFolder, string legacyFolder)
    {
        if (File.Exists(Path.Combine(dataFolder, MarkerName))) return false;
        if (File.Exists(Path.Combine(dataFolder, AppConstants.SettingsFileName))) return false;
        if (SameFolder(dataFolder, legacyFolder)) return false;
        return File.Exists(Path.Combine(legacyFolder, AppConstants.SettingsFileName));
    }

    /// <summary>
    /// 旧データの中身を今のデータフォルダへ移す(同一ドライブなら移動、無理ならコピー)。
    /// 今のデータフォルダに同名があるものは触らない。
    /// </summary>
    internal static string Migrate(string dataFolder, string legacyFolder)
    {
        var moved = 0;
        var copied = 0;
        var failed = 0;
        foreach (var entry in Directory.GetFileSystemEntries(legacyFolder))
        {
            var target = Path.Combine(dataFolder, Path.GetFileName(entry));
            if (File.Exists(target) || Directory.Exists(target)) continue;
            if (TryMove(entry, target)) { moved++; continue; }
            if (TryCopy(entry, target)) { copied++; continue; }
            failed++;
        }
        // 移動で中身が空になったら、旧フォルダ自体も片付ける(コピーが残っている場合は残す)
        var emptied = false;
        if (copied == 0 && failed == 0 && !HasEntries(legacyFolder))
        {
            try { Directory.Delete(legacyFolder); emptied = true; } catch { }
        }
        var text = $"移動 {moved}件";
        if (copied > 0) text += $", コピー {copied}件";
        if (failed > 0) text += $", 失敗 {failed}件";
        if (emptied) text += " (旧フォルダは空になったため削除)";
        else if (copied > 0) text += " (旧フォルダは残しています。確認後に削除してください)";
        return text;
    }

    /// <summary>引き継ぎの結果を記録する(次回から確認しない)。</summary>
    public static void MarkDone(string dataFolder, string result)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(dataFolder, MarkerName),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {result}{Environment.NewLine}");
        }
        catch { }
    }

    private static bool HasEntries(string folder)
    {
        try { return Directory.EnumerateFileSystemEntries(folder).Any(); }
        catch { return true; }
    }

    private static bool TryMove(string source, string target)
    {
        try
        {
            if (Directory.Exists(source)) Directory.Move(source, target);
            else File.Move(source, target);
            return true;
        }
        catch { return false; }
    }

    private static bool TryCopy(string source, string target)
    {
        try
        {
            if (Directory.Exists(source)) CopyFolder(source, target);
            else File.Copy(source, target, false);
            return true;
        }
        catch { return false; }
    }

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), false);
        foreach (var dir in Directory.GetDirectories(source))
            CopyFolder(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    private static bool SameFolder(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
