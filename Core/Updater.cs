using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TwinCord;

public record UpdateInfo(string Tag, string Version, string Notes, string AssetUrl, string Sha256);

/// <summary>
/// GitHub Releases経由の更新確認・適用。
/// 配布物はフォルダ構成をまとめたzip(TwinCord.zip)1つ。適用は別batに委譲し、
/// アプリのフォルダへミラー同期する(dataとmodsは残す)。
/// </summary>
public static class Updater
{
    private const string Repo = "Peragami/TwinCord";
    public const string RepoLatestUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
    public const string AssetName = "TwinCord.zip";

    /// <summary>
    /// 配布ファイルの取得先として妥当か。APIの応答が差し替わっても任意ホストから
    /// exeを落として実行しないよう、GitHubの当該リポジトリに限定する。
    /// (リリース自体が乗っ取られた場合までは防げないため、SHA256と併せた多層防御)
    /// </summary>
    public static bool IsTrustedAssetUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps) return false;
        if (!u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        return u.AbsolutePath.StartsWith("/" + Repo + "/releases/download/", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<UpdateInfo?> FetchLatestAsync(
        HttpClient http, CancellationToken ct = default, Action<string>? log = null)
    {
        log ??= Log.Write;
        using var req = new HttpRequestMessage(HttpMethod.Get, RepoLatestUrl);
        req.Headers.UserAgent.ParseAdd("TwinCord");
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var r = doc.RootElement;
        var tag = r.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
        var latest = tag.TrimStart('v', 'V');
        if (string.IsNullOrEmpty(latest)) return null;
        var notes = r.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";
        string assetUrl = "";
        string sha256 = "";
        if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var an) ? (an.GetString() ?? "") : "";
                if (!string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                assetUrl = a.TryGetProperty("browser_download_url", out var au) ? (au.GetString() ?? "") : "";
                var digest = a.TryGetProperty("digest", out var dg) ? (dg.GetString() ?? "") : "";
                if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha256 = digest.Substring(7);
                break;
            }
        }
        if (!IsTrustedAssetUrl(assetUrl))
        {
            if (!string.IsNullOrEmpty(assetUrl))
                log("更新: 配布ファイルの取得先が想定外のため無視: " + assetUrl);
            assetUrl = "";
            sha256 = "";
        }
        return new UpdateInfo(tag, latest, notes, assetUrl, sha256);
    }

    public static bool IsNewer(string current, string latest)
    {
        try
        {
            return new Version(current.Split('-')[0]).CompareTo(new Version(latest.Split('-')[0])) < 0;
        }
        catch
        {
            return false;
        }
    }

    public static async Task DownloadAsync(HttpClient http, string url, string destPath,
        IProgress<double> progress, CancellationToken ct)
    {
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength;
        await using var src = await res.Content.ReadAsStreamAsync(ct);
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        await using var dst = File.Create(destPath);
        var buf = new byte[81920];
        long done = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            done += n;
            if (total > 0) progress.Report((double)done / total.Value);
        }
    }

    /// <summary>
    /// ダウンロード済みファイルのSHA256を照合する。期待値が空なら検証なしでtrue。
    /// </summary>
    public static bool VerifySha256(string path, string expectedHex)
    {
        if (string.IsNullOrEmpty(expectedHex)) return true;
        try
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var fs = File.OpenRead(path);
            var actual = Convert.ToHexString(sha.ComputeHash(fs));
            return string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>ダウンロード済みzipと展開先を消す(失敗は無視)。</summary>
    public static void CleanStage()
    {
        try { if (File.Exists(StageZipPath())) File.Delete(StageZipPath()); } catch { }
        try { if (Directory.Exists(StageExtractDir())) Directory.Delete(StageExtractDir(), true); } catch { }
    }

    public static string StageDir() => Path.Combine(Path.GetTempPath(), "TwinCordUpdate");

    public static string StageZipPath() => Path.Combine(StageDir(), "TwinCord.zip");

    /// <summary>zipの展開先(ここからアプリのフォルダへ同期する)。</summary>
    public static string StageExtractDir() => Path.Combine(StageDir(), "new");

    /// <summary>
    /// 配布zipを展開する。展開先の外へ出るエントリは.NET側が拒否する
    /// (Zip Slip対策はフレームワークに任せ、ここでは前回の残骸を消してから展開する)。
    /// </summary>
    public static void ExtractZip(string zipPath, string destDir)
    {
        if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
        Directory.CreateDirectory(destDir);
        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, destDir, overwriteFiles: true);
    }

    /// <summary>
    /// フォルダ構成で配置されているか(exeの隣にアプリ本体のdllがある)。
    /// 単一exe発行や開発中のビルドでは false になり、自動更新の対象外とする。
    /// </summary>
    public static bool IsFolderLayout() => IsFolderLayoutIn(AppSettings.AppDir());

    internal static bool IsFolderLayoutIn(string appDir)
    {
        try
        {
            return File.Exists(Path.Combine(appDir, AppConstants.AppName + ".dll"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 更新を反映するbatの内容(テストから確認できるように分離)。ASCIIのみで書く。
    /// </summary>
    internal static string[] BuildUpdateBatLines() => new[]
    {
        "@echo off",
        ":wait",
        "tasklist /FI \"PID eq %TC_PID%\" 2>nul | find \"%TC_PID%\" >nul",
        "if %ERRORLEVEL%==0 (timeout /t 1 /nobreak >nul & goto wait)",
        // 除外は「送信元と送信先の両方のパス」で指定する(片方だけだと、新旧両方に存在する
        // フォルダの中身が/MIRで消される・または送信先だけにあるフォルダが削除される)
        "robocopy \"%TC_SRC%\" \"%TC_DST%\" /MIR /XD \"%TC_SRC%\\%TC_DATANAME%\" \"%TC_SRC%\\%TC_MODSNAME%\" \"%TC_DST%\\%TC_DATANAME%\" \"%TC_DST%\\%TC_MODSNAME%\" /R:3 /W:1 /NFL /NDL /NJH /NJS >nul",
        // robocopyの8以上は失敗(0-7は成功)。失敗しても起動はして、原因を残す
        "if %ERRORLEVEL% GEQ 8 (echo %DATE% %TIME% sync failed %ERRORLEVEL% \"%TC_SRC%\" to \"%TC_DST%\">> \"%~dp0update-error.log\" & start \"\" \"%TC_EXE%\" & del \"%~f0\" >nul 2>&1 & exit /b 1)",
        "start \"\" \"%TC_EXE%\"",
        "rd /s /q \"%TC_SRC%\" >nul 2>&1",
        "del \"%TC_ZIP%\" >nul 2>&1",
        "del \"%~f0\" >nul 2>&1",
    };

    /// <summary>
    /// 自プロセス終了後に展開済みフォルダをアプリのフォルダへ反映して再起動するbatを投げて即戻る。
    /// 呼び出し側で終了すること。dataとmodsは除外し、それ以外は新バージョンに揃える(/MIR)。
    /// batにはASCIIしか書かず、パスは環境変数(Unicode)で渡す。batをUTF-8で書くとcmdが
    /// OEMコードページで読むため、日本語を含むパスが化けて失敗する。
    /// </summary>
    public static void ApplyAndRestart(int currentPid, string sourceDir, string targetDir)
    {
        var bat = Path.Combine(StageDir(), "update.bat");
        Directory.CreateDirectory(StageDir());
        File.WriteAllLines(bat, BuildUpdateBatLines(), Encoding.ASCII);
        var psi = new ProcessStartInfo
        {
            FileName = bat,
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        // 値そのものはUnicodeで渡り、cmd側の展開もUnicodeなので文字コードの影響を受けない
        psi.Environment["TC_PID"] = currentPid.ToString();
        psi.Environment["TC_SRC"] = sourceDir;
        psi.Environment["TC_DST"] = targetDir;
        psi.Environment["TC_DATANAME"] = AppConstants.DataFolderName;
        psi.Environment["TC_MODSNAME"] = AppConstants.ModsFolderName;
        psi.Environment["TC_EXE"] = Path.Combine(targetDir, AppConstants.AppName + ".exe");
        psi.Environment["TC_ZIP"] = StageZipPath();
        Process.Start(psi);
    }
}
