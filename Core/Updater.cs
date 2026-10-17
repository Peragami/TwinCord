using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TwinCord;

public record UpdateInfo(string Tag, string Version, string Notes, string AssetUrl, string Sha256);

/// <summary>
/// GitHub Releases経由の更新確認・適用。適用は別batに委譲する。
/// </summary>
public static class Updater
{
    private const string Repo = "Peragami/TwinCord";
    public const string RepoLatestUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
    public const string AssetName = "TwinCord.exe";

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

    public static async Task<UpdateInfo?> FetchLatestAsync(HttpClient http, CancellationToken ct = default)
    {
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
                Diagnostics.Log("更新: 配布ファイルの取得先が想定外のため無視: " + assetUrl);
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

    public static string StageDir() => Path.Combine(Path.GetTempPath(), "TwinCordUpdate");

    public static string StagePath() => Path.Combine(StageDir(), "TwinCord.new.exe");

    /// <summary>
    /// 自プロセス終了後に新exeへ差し替えて再起動するbatを投げて即戻る。呼び出し側で終了すること。
    /// </summary>
    public static void ApplyAndRestart(int currentPid, string newExe, string targetExe)
    {
        var bat = Path.Combine(StageDir(), "update.bat");
        Directory.CreateDirectory(StageDir());
        File.WriteAllLines(bat, new[]
        {
            "@echo off",
            $"set PID={currentPid}",
            $"set DST={Quote(targetExe)}",
            $"set SRC={Quote(newExe)}",
            ":wait",
            "tasklist /FI \"PID eq %PID%\" 2>nul | find \"%PID%\" >nul",
            "if %ERRORLEVEL%==0 (timeout /t 1 /nobreak >nul & goto wait)",
            "copy /Y %SRC% %DST% >nul",
            "if %ERRORLEVEL% NEQ 0 (echo %DATE% %TIME% copy failed %SRC% to %DST%>> \"%~dp0update-error.log\" & start \"\" %DST% & del \"%~f0\" >nul 2>&1 & exit /b 1)",
            "start \"\" %DST%",
            "del %SRC% >nul 2>&1",
            "del \"%~f0\" >nul 2>&1",
        }, new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo
        {
            FileName = bat,
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }

    private static string Quote(string path) => "\"" + path + "\"";
}
