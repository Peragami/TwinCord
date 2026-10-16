using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TwinCord;

public record UpdateInfo(string Tag, string Version, string Notes, string AssetUrl);

/// <summary>
/// GitHub Releases経由の更新確認・適用。適用は別batに委譲する。
/// </summary>
public static class Updater
{
    public const string RepoLatestUrl = "https://api.github.com/repos/Peragami/TwinCord/releases/latest";
    public const string AssetName = "TwinCord.exe";

#if SELF_CONTAINED_BUILD
    public static bool IsSelfContainedBuild => true;
#else
    public static bool IsSelfContainedBuild => false;
#endif

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
        if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var an) ? (an.GetString() ?? "") : "";
                if (!string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                assetUrl = a.TryGetProperty("browser_download_url", out var au) ? (au.GetString() ?? "") : "";
                break;
            }
        }
        return new UpdateInfo(tag, latest, notes, assetUrl);
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
            "start \"\" %DST%",
            "del %SRC% >nul 2>&1",
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
