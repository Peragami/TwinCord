using System.IO;

namespace TwinCord;

/// <summary>
/// 単一exe発行のときに作られる一時展開フォルダの掃除。
///
/// 配布用の単一exeは同梱物をexe内に保持せず(IncludeAllContentForSelfExtract)、
/// %TEMP%\.net\TwinCord\&lt;ハッシュ&gt;\ へ展開してそこから読み込む。展開先はバージョンごとに
/// 変わるため、放置すると更新のたびに古いフォルダ(約180MB)が残り続ける。
/// ここでは「今実行中のフォルダ」以外を削除する。
///
/// 開発ビルド(bin\Releaseやbin\Debug)や展開方式でない配布物では展開フォルダ自体が
/// 存在しないため、想定した階層(root直下)を確認できない限り何もしない。
/// 使用中・権限などの理由で削除できない場合は無視する(次回起動で再挑戦される)。
/// </summary>
public static class BundleCleanup
{
    /// <summary>掃除を別スレッドで開始する(起動を待たせない)。</summary>
    public static void Start()
    {
        Task.Run(() =>
        {
            try { Sweep(); } catch { }
        });
    }

    /// <summary>実際の展開先(%TEMP%\.net\TwinCord)を対象に掃除する。</summary>
    internal static void Sweep()
    {
        var root = Path.Combine(Path.GetTempPath(), ".net", AppConstants.AppName);
        var current = ActiveFolderUnder(root, CandidateDirs());
        if (current is null) return; // 展開方式で動いていない → 何もしない
        var deleted = DeleteOthers(root, current);
        if (deleted > 0)
            Log.Write($"展開フォルダの掃除: {deleted}件削除 (残りは実行中の {Path.GetFileName(current)})");
    }

    /// <summary>
    /// root直下のフォルダのうち、今実行中のcurrent以外を削除し、削除できた件数を返す。
    /// root自体は.NETが展開用に作る場所なので、直下は展開フォルダ以外が入らない。
    /// </summary>
    internal static int DeleteOthers(string root, string current)
    {
        if (!Directory.Exists(root)) return 0;
        var keep = Normalize(current);
        if (keep is null) return 0;
        var deleted = 0;
        foreach (var dir in Directory.GetDirectories(root))
        {
            if (string.Equals(Normalize(dir), keep, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                Directory.Delete(dir, true);
                deleted++;
            }
            catch { }
        }
        return deleted;
    }

    /// <summary>
    /// 実行中のプロセスがファイルを読み込んでいる場所から、今使っている展開フォルダを求める。
    /// 想定した階層(root直下の実在フォルダ)でなければnullを返す。
    /// これがnullになる限り削除は一切行わないため、開発ビルドが何かを消すことはない。
    /// </summary>
    internal static string? ActiveFolderUnder(string root, IEnumerable<string?> chooseFrom)
    {
        var normalizedRoot = Normalize(root);
        if (normalizedRoot is null) return null;
        foreach (var candidate in chooseFrom)
        {
            var dir = Normalize(candidate);
            if (dir is null || dir.Length == 0) continue;
            if (Path.GetFileName(dir).Length == 0) continue; // ルート直下は対象外
            var parent = Normalize(Path.GetDirectoryName(dir));
            if (parent is null || !string.Equals(parent, normalizedRoot, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Directory.Exists(dir)) continue;
            return dir;
        }
        return null;
    }

    /// <summary>展開方式で読み込まれているファイルの置き場所(通常配置では見つからない)。</summary>
    private static IEnumerable<string?> CandidateDirs()
    {
        // 展開方式では中核アセンブリが展開先に置かれる。バンドル内に保持する方式では空文字。
        string? coreLib = null;
        try { coreLib = typeof(object).Assembly.Location; } catch { }
        yield return Path.GetDirectoryName(coreLib ?? "");
        yield return AppContext.BaseDirectory;
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return null; }
    }
}
