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
        using var req = new HttpRequestMessage(HttpMethod.Get, RepoLatestUrl);
        req.Headers.UserAgent.ParseAdd("TwinCord");
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;
        return ParseLatest(await res.Content.ReadAsStringAsync(ct), log);
    }

    /// <summary>Releases APIの応答(JSON)を解釈する。HTTP取得と分けて扱う。</summary>
    internal static UpdateInfo? ParseLatest(string json, Action<string>? log = null)
    {
        log ??= Log.Write;
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var tag = r.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
        var latest = tag.Trim().TrimStart('v', 'V');
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
                {
                    var candidate = digest[7..].Trim();
                    // 長さ・文字種が違う値を「検証済み」と扱わない。空のままなら自動更新は中止する
                    if (IsValidSha256(candidate))
                        sha256 = candidate;
                    else
                        log("更新: SHA256の形式が不正なため無視: " + digest);
                }
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
        if (!TryParseVersion(current, out var c) || !TryParseVersion(latest, out var l))
            return false;
        return c < l;
    }

    /// <summary>
    /// 比較用にバージョンを4要素へ正規化する。1.11.2 と 1.11.2.0 を同じものとして扱い、
    /// 1.11.2.1 のような4要素タグでも更新ループにならないようにする。
    /// </summary>
    internal static bool TryParseVersion(string? raw, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        var text = (raw ?? "").Trim();
        // プレリリース表記(-beta等)は比較対象外。latestはGitHubのlatestなので通常は付かない
        var dash = text.IndexOf('-');
        if (dash >= 0) text = text[..dash];
        if (!Version.TryParse(text, out var parsed)) return false;
        version = new Version(
            Math.Max(parsed.Major, 0),
            Math.Max(parsed.Minor, 0),
            parsed.Build >= 0 ? parsed.Build : 0,
            parsed.Revision >= 0 ? parsed.Revision : 0);
        return true;
    }

    /// <summary>SHA256の期待値として妥当か(64桁の16進数)。</summary>
    public static bool IsValidSha256(string? hex) =>
        hex is { Length: 64 } && hex.All(Uri.IsHexDigit);

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
    /// ダウンロード済みファイルのSHA256を照合する。期待値が空・不正なら検証失敗とする
    /// (ハッシュが無い配布物を「検証済み」として受け入れない)。
    /// </summary>
    public static bool VerifySha256(string path, string expectedHex)
    {
        if (!IsValidSha256(expectedHex)) return false;
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

    /// <summary>ダウンロード済みzip・展開先・更新BAT・バックアップを消す(失敗は無視)。</summary>
    public static void CleanStage()
    {
        try { if (File.Exists(StageZipPath())) File.Delete(StageZipPath()); } catch { }
        try { if (Directory.Exists(StageExtractDir())) Directory.Delete(StageExtractDir(), true); } catch { }
        try { if (Directory.Exists(StageBackupDir())) Directory.Delete(StageBackupDir(), true); } catch { }
        try { if (File.Exists(UpdateBatPath())) File.Delete(UpdateBatPath()); } catch { }
    }

    public static string StageDir() => Path.Combine(Path.GetTempPath(), "TwinCordUpdate");

    public static string StageZipPath() => Path.Combine(StageDir(), "TwinCord.zip");

    /// <summary>zipの展開先(ここからアプリのフォルダへ同期する)。</summary>
    public static string StageExtractDir() => Path.Combine(StageDir(), "new");

    /// <summary>更新前にアプリ本体を退避する場所(失敗時に戻す)。</summary>
    public static string StageBackupDir() => Path.Combine(StageDir(), "backup");

    public static string UpdateBatPath() => Path.Combine(StageDir(), "update.bat");

    /// <summary>更新BATが失敗理由を残す場所(アプリ本体のフォルダ)。</summary>
    public static string UpdateErrorLogPath() => Path.Combine(
        AppSettings.AppDir(), "update-error.log");

    /// <summary>
    /// 前回の更新失敗記録を読み取って削除する(起動時に一度だけ知らせるため)。
    /// 読めない/消せない場合はnullを返し、次回起動で再試行する。
    /// </summary>
    public static string? TakeUpdateError() => TakeUpdateErrorFrom(UpdateErrorLogPath());

    internal static string? TakeUpdateErrorFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path, Encoding.UTF8).Trim();
            try { File.Delete(path); } catch { }
            if (text.Length > 2000) text = text[^2000..];
            return text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 展開内容がアプリとして起動できる最低限の構成か。空zipやビルドミスのまま
    /// /MIRするとアプリ本体を消してしまうため、適用前に必ず確認する。
    /// </summary>
    internal static string? ValidatePayload(string sourceDir)
    {
        var exe = Path.Combine(sourceDir, AppConstants.AppName + ".exe");
        var dll = Path.Combine(sourceDir, AppConstants.AppName + ".dll");
        var deps = Path.Combine(sourceDir, AppConstants.AppName + ".deps.json");
        var runtimeConfig = Path.Combine(sourceDir, AppConstants.AppName + ".runtimeconfig.json");
        if (!File.Exists(exe)) return AppConstants.AppName + ".exe が含まれていません";
        if (!File.Exists(dll)) return AppConstants.AppName + ".dll が含まれていません";
        if (!File.Exists(deps)) return AppConstants.AppName + ".deps.json が含まれていません";
        if (!File.Exists(runtimeConfig)) return AppConstants.AppName + ".runtimeconfig.json が含まれていません";
        try
        {
            if (new FileInfo(exe).Length == 0) return AppConstants.AppName + ".exe が空です";
            if (new FileInfo(dll).Length == 0) return AppConstants.AppName + ".dll が空です";
            if (new FileInfo(deps).Length == 0) return AppConstants.AppName + ".deps.json が空です";
            if (new FileInfo(runtimeConfig).Length == 0) return AppConstants.AppName + ".runtimeconfig.json が空です";
            // ファイルバージョンが読める場合は、現在より新しいことも確認する
            var fileVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileVersion;
            if (!string.IsNullOrWhiteSpace(fileVersion)
                && TryParseVersion(fileVersion, out var parsed)
                && !IsNewer(Diagnostics.AppVersion(), parsed.ToString()))
            {
                return $"配布ファイルのバージョン({parsed})が現在({Diagnostics.AppVersion()})以下です";
            }
        }
        catch
        {
            // バージョン情報が読めないだけなら、存在とサイズの確認で続行する
        }
        return null;
    }

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
        // timeout.exeは入力リダイレクト時に待機しないことがあるため、pingで1秒待つ
        "if %ERRORLEVEL%==0 (ping -n 2 127.0.0.1 >nul & goto wait)",
        // 展開内容の最終確認。壊れたzipのまま/MIRするとアプリ本体が消えるため、必須ファイルが無ければ中止する
        "if not exist \"%TC_SRC%\\TwinCord.exe\" (echo %DATE% %TIME% payload missing TwinCord.exe>> \"%TC_DST%\\update-error.log\" & del \"%TC_ZIP%\" >nul 2>&1 & start \"\" \"%TC_EXE%\" & del \"%~f0\" >nul 2>&1 & exit /b 1)",
        "if not exist \"%TC_SRC%\\TwinCord.dll\" (echo %DATE% %TIME% payload missing TwinCord.dll>> \"%TC_DST%\\update-error.log\" & del \"%TC_ZIP%\" >nul 2>&1 & start \"\" \"%TC_EXE%\" & del \"%~f0\" >nul 2>&1 & exit /b 1)",
        "if not exist \"%TC_SRC%\\TwinCord.deps.json\" (echo %DATE% %TIME% payload missing TwinCord.deps.json>> \"%TC_DST%\\update-error.log\" & del \"%TC_ZIP%\" >nul 2>&1 & start \"\" \"%TC_EXE%\" & del \"%~f0\" >nul 2>&1 & exit /b 1)",
        "if not exist \"%TC_SRC%\\TwinCord.runtimeconfig.json\" (echo %DATE% %TIME% payload missing TwinCord.runtimeconfig.json>> \"%TC_DST%\\update-error.log\" & del \"%TC_ZIP%\" >nul 2>&1 & start \"\" \"%TC_EXE%\" & del \"%~f0\" >nul 2>&1 & exit /b 1)",
        // 置換前のアプリ本体を退避する(data/modsは対象外)。失敗時はここから戻す
        "if exist \"%TC_BACKUP%\" rd /s /q \"%TC_BACKUP%\"",
        "robocopy \"%TC_DST%\" \"%TC_BACKUP%\" /MIR /XD \"%TC_SRC%\\%TC_DATANAME%\" \"%TC_SRC%\\%TC_MODSNAME%\" \"%TC_DST%\\%TC_DATANAME%\" \"%TC_DST%\\%TC_MODSNAME%\" \"%TC_BACKUP%\\%TC_DATANAME%\" \"%TC_BACKUP%\\%TC_MODSNAME%\" /R:3 /W:1 /NFL /NDL /NJH /NJS >nul",
        "if %ERRORLEVEL% GEQ 8 (echo %DATE% %TIME% backup failed %ERRORLEVEL% \"%TC_DST%\" to \"%TC_BACKUP%\">> \"%TC_DST%\\update-error.log\" & del \"%TC_ZIP%\" >nul 2>&1 & start \"\" \"%TC_EXE%\" & del \"%~f0\" >nul 2>&1 & exit /b 1)",
        // 除外は「送信元と送信先の両方のパス」で指定する(片方だけだと、新旧両方に存在する
        // フォルダの中身が/MIRで消される・または送信先だけにあるフォルダが削除される)
        "robocopy \"%TC_SRC%\" \"%TC_DST%\" /MIR /XD \"%TC_SRC%\\%TC_DATANAME%\" \"%TC_SRC%\\%TC_MODSNAME%\" \"%TC_DST%\\%TC_DATANAME%\" \"%TC_DST%\\%TC_MODSNAME%\" /R:3 /W:1 /NFL /NDL /NJH /NJS >nul",
        // robocopyの8以上は失敗(0-7は成功)。失敗したら退避から戻して旧バージョンで起動する
        // 失敗理由は復元後に書く(復元の/MIRでupdate-error.logが消えるため)
        "if %ERRORLEVEL% GEQ 8 (del \"%TC_ZIP%\" >nul 2>&1 & robocopy \"%TC_BACKUP%\" \"%TC_DST%\" /MIR /XD \"%TC_BACKUP%\\%TC_DATANAME%\" \"%TC_BACKUP%\\%TC_MODSNAME%\" \"%TC_DST%\\%TC_DATANAME%\" \"%TC_DST%\\%TC_MODSNAME%\" /R:3 /W:1 /NFL /NDL /NJH /NJS >nul & echo %DATE% %TIME% sync failed %ERRORLEVEL% \"%TC_SRC%\" to \"%TC_DST%\">> \"%TC_DST%\\update-error.log\" & start \"\" \"%TC_EXE%\" & del \"%~f0\" >nul 2>&1 & exit /b 1)",
        "start \"\" \"%TC_EXE%\"",
        "rd /s /q \"%TC_SRC%\" >nul 2>&1",
        "rd /s /q \"%TC_BACKUP%\" >nul 2>&1",
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
        var bat = UpdateBatPath();
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
        psi.Environment["TC_BACKUP"] = StageBackupDir();
        psi.Environment["TC_DATANAME"] = AppConstants.DataFolderName;
        psi.Environment["TC_MODSNAME"] = AppConstants.ModsFolderName;
        psi.Environment["TC_EXE"] = Path.Combine(targetDir, AppConstants.AppName + ".exe");
        psi.Environment["TC_ZIP"] = StageZipPath();
        Process.Start(psi);
    }
}
