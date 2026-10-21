using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TwinCord;

namespace TwinCord.Tests;

public class UpdaterTests
{
    private const string DefaultAssetUrl =
        "https://github.com/Peragami/TwinCord/releases/download/v1.11.0/TwinCord.zip";
    private const string ValidHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void AssetName_IsTheReleaseZipName()
    {
        // リリースに添付する名前。変えると自動更新が動かなくなる
        Assert.Equal("TwinCord.zip", Updater.AssetName);
    }

    [Theory]
    [InlineData(DefaultAssetUrl, true)]
    [InlineData("https://github.com/peragami/twincord/releases/download/v1.11.0/TwinCord.zip", true)]
    [InlineData("http://github.com/Peragami/TwinCord/releases/download/v1.11.0/TwinCord.zip", false)]
    [InlineData("https://evil.example/Peragami/TwinCord/releases/download/v1.11.0/TwinCord.zip", false)]
    [InlineData("https://github.com/Other/TwinCord/releases/download/v1.11.0/TwinCord.zip", false)]
    [InlineData("https://github.com/Peragami/TwinCord/releases/tag/v1.11.0", false)]
    [InlineData("https://github.com/Peragami/TwinCord/releases/download-evil/v1.11.0/TwinCord.zip", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTrustedAssetUrl_ReturnsExpected(string? url, bool expected)
    {
        Assert.Equal(expected, Updater.IsTrustedAssetUrl(url!));
    }

    [Theory]
    [InlineData("1.9.2", "1.10.0", true)]
    [InlineData("1.9.9", "1.10", true)]
    [InlineData("1.10.0", "1.10.0", false)]
    [InlineData("1.10.1", "1.10.0", false)]
    [InlineData("1.10.0", "1.10.0-beta", false)]
    [InlineData("1.10.0", "2.0", true)]
    [InlineData("1.11.2", "1.11.2.1", true)]
    [InlineData("1.11.2.1", "1.11.2", false)]
    [InlineData("1.11.2", "1.11.2.0", false)]
    [InlineData("1.11.2.0", "1.11.2", false)]
    [InlineData("bad", "1.0.0", false)]
    [InlineData("1.0.0", "bad", false)]
    public void IsNewer_ReturnsExpected(string current, string latest, bool expected)
    {
        Assert.Equal(expected, Updater.IsNewer(current, latest));
    }

    [Fact]
    public async Task FetchLatestAsync_ParsesTagNotesAssetAndDigest()
    {
        var json = BuildReleaseJson(DefaultAssetUrl, digest: "sha256:" + ValidHash);
        using var http = new HttpClient(new StubHttpMessageHandler(json));

        var info = await Updater.FetchLatestAsync(http);

        Assert.NotNull(info);
        Assert.Equal("v1.11.0", info!.Tag);
        Assert.Equal("1.11.0", info.Version);
        Assert.Equal("release notes", info.Notes);
        Assert.Equal(DefaultAssetUrl, info.AssetUrl);
        Assert.Equal(ValidHash, info.Sha256);
    }

    [Fact]
    public async Task FetchLatestAsync_MalformedDigestIsNotUsed()
    {
        var json = BuildReleaseJson(DefaultAssetUrl, digest: "sha256:ABC123");
        using var http = new HttpClient(new StubHttpMessageHandler(json));
        var logs = new List<string>();

        var info = await Updater.FetchLatestAsync(http, log: logs.Add);

        Assert.NotNull(info);
        Assert.Equal("", info!.Sha256);
        Assert.Contains(logs, m => m.Contains("形式が不正", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchLatestAsync_MissingDigestYieldsEmptyHash()
    {
        var json = BuildReleaseJson(DefaultAssetUrl, digest: null);
        using var http = new HttpClient(new StubHttpMessageHandler(json));

        var info = await Updater.FetchLatestAsync(http);

        Assert.NotNull(info);
        Assert.Equal("", info!.Sha256);
    }

    [Fact]
    public async Task FetchLatestAsync_IgnoresUntrustedAssetHost()
    {
        var json = BuildReleaseJson("https://evil.example/TwinCord.exe", digest: "sha256:ABC123");
        using var http = new HttpClient(new StubHttpMessageHandler(json));
        var logs = new List<string>();

        var info = await Updater.FetchLatestAsync(http, log: logs.Add);

        Assert.NotNull(info);
        Assert.Equal("", info!.AssetUrl);
        Assert.Equal("", info.Sha256);
        Assert.Contains(logs, m => m.Contains("evil.example", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchLatestAsync_WithoutMatchingAssetLeavesUrlEmpty()
    {
        var json = BuildReleaseJson(DefaultAssetUrl, assetName: "other.exe");
        using var http = new HttpClient(new StubHttpMessageHandler(json));

        var info = await Updater.FetchLatestAsync(http);

        Assert.NotNull(info);
        Assert.Equal("", info!.AssetUrl);
        Assert.Equal("", info.Sha256);
    }

    [Fact]
    public async Task FetchLatestAsync_DigestWithoutSha256PrefixIsNotUsed()
    {
        var json = BuildReleaseJson(DefaultAssetUrl, digest: "ABC123");
        using var http = new HttpClient(new StubHttpMessageHandler(json));

        var info = await Updater.FetchLatestAsync(http);

        Assert.NotNull(info);
        Assert.Equal("", info!.Sha256);
    }

    [Fact]
    public async Task FetchLatestAsync_ReturnsNullForHttpError()
    {
        using var http = new HttpClient(
            new StubHttpMessageHandler("{}", HttpStatusCode.InternalServerError));

        Assert.Null(await Updater.FetchLatestAsync(http));
    }

    [Fact]
    public async Task FetchLatestAsync_ReturnsNullWithoutTag()
    {
        using var http = new HttpClient(
            new StubHttpMessageHandler("""{"body":"no tag"}"""));

        Assert.Null(await Updater.FetchLatestAsync(http));
    }

    [Fact]
    public void VerifySha256_MatchesKnownHashCaseInsensitively()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "data.bin");
        File.WriteAllText(path, "hello");

        Assert.True(Updater.VerifySha256(
            path, "2CF24DBA5FB0A30E26E83B2AC5B9E29E1B161E5C1FA7425E73043362938B9824"));
        Assert.False(Updater.VerifySha256(path, new string('0', 64)));
    }

    [Fact]
    public void VerifySha256_EmptyOrMalformedExpectedIsRejected()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "data.bin");
        File.WriteAllText(path, "hello");

        // 期待値が無い・短い・16進数でない場合は「検証成功」にしない
        Assert.False(Updater.VerifySha256(path, ""));
        Assert.False(Updater.VerifySha256(path, "ABC123"));
        Assert.False(Updater.VerifySha256(path, new string('Z', 64)));
    }

    [Theory]
    [InlineData(ValidHash, true)]
    [InlineData("abc123", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidSha256_ReturnsExpected(string? value, bool expected)
    {
        Assert.Equal(expected, Updater.IsValidSha256(value));
    }

    [Fact]
    public void VerifySha256_MissingFileIsRejected()
    {
        using var temp = new TempFolder();
        Assert.False(Updater.VerifySha256(
            Path.Combine(temp.Path, "missing.bin"),
            "2CF24DBA5FB0A30E26E83B2AC5B9E29E1B161E5C1FA7425E73043362938B9824"));
    }

    [Fact]
    public async Task DownloadAsync_WritesFileToNestedDirectory()
    {
        var payload = Encoding.UTF8.GetBytes("abcdef");
        using var http = new HttpClient(new StubHttpMessageHandler(payload));
        using var temp = new TempFolder();
        var dest = Path.Combine(temp.Path, "nested", "TwinCord.zip");

        await Updater.DownloadAsync(http, "https://example.test/TwinCord.zip", dest,
            new NullProgress(), CancellationToken.None);

        Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
    }

    [Fact]
    public void ExtractZip_ExtractsAndReplacesPreviousContent()
    {
        using var temp = new TempFolder();
        var zip = Path.Combine(temp.Path, "test.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "TwinCord.exe", "exe");
            WriteEntry(archive, "data/README.txt", "readme");
        }
        var dest = Path.Combine(temp.Path, "out");
        Updater.ExtractZip(zip, dest);
        Assert.Equal("exe", File.ReadAllText(Path.Combine(dest, "TwinCord.exe")));
        Assert.Equal("readme", File.ReadAllText(Path.Combine(dest, "data", "README.txt")));

        // 前回の展開先は作り直す(古いファイルを同期対象に残さない)
        File.WriteAllText(Path.Combine(dest, "old.txt"), "old");
        Updater.ExtractZip(zip, dest);
        Assert.False(File.Exists(Path.Combine(dest, "old.txt")));
    }

    [Fact]
    public void ExtractZip_RejectsEntryEscapingDestination()
    {
        using var temp = new TempFolder();
        var zip = Path.Combine(temp.Path, "evil.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            WriteEntry(archive, "../evil.txt", "x");

        Assert.ThrowsAny<IOException>(() => Updater.ExtractZip(zip, Path.Combine(temp.Path, "out")));
        Assert.False(File.Exists(Path.Combine(temp.Path, "evil.txt")));
    }

    [Fact]
    public void ValidatePayload_RequiresExecutableLibraryAndRuntimeFiles()
    {
        using var temp = new TempFolder();

        Assert.Contains("TwinCord.exe", Updater.ValidatePayload(temp.Path));
        File.WriteAllText(Path.Combine(temp.Path, "TwinCord.exe"), "exe");
        Assert.Contains("TwinCord.dll", Updater.ValidatePayload(temp.Path));
        File.WriteAllText(Path.Combine(temp.Path, "TwinCord.dll"), "dll");
        Assert.Contains("TwinCord.deps.json", Updater.ValidatePayload(temp.Path));
        File.WriteAllText(Path.Combine(temp.Path, "TwinCord.deps.json"), "{}");
        Assert.Contains("runtimeconfig.json", Updater.ValidatePayload(temp.Path));
        File.WriteAllText(Path.Combine(temp.Path, "TwinCord.runtimeconfig.json"), "{}");
        Assert.Null(Updater.ValidatePayload(temp.Path));
    }

    [Fact]
    public void ValidatePayload_RejectsEmptyFiles()
    {
        using var temp = new TempFolder();
        File.WriteAllBytes(Path.Combine(temp.Path, "TwinCord.exe"), []);
        File.WriteAllText(Path.Combine(temp.Path, "TwinCord.dll"), "dll");
        File.WriteAllText(Path.Combine(temp.Path, "TwinCord.deps.json"), "{}");
        File.WriteAllText(Path.Combine(temp.Path, "TwinCord.runtimeconfig.json"), "{}");

        Assert.Contains("空", Updater.ValidatePayload(temp.Path));
    }

    [Fact]
    public void TakeUpdateErrorFrom_ReadsAndDeletesTheLog()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "update-error.log");
        File.WriteAllText(path, new string('x', 2500) + "\r\nsync failed 8");

        var text = Updater.TakeUpdateErrorFrom(path);

        Assert.NotNull(text);
        Assert.Contains("sync failed 8", text);
        Assert.True(text!.Length <= 2000);
        Assert.False(File.Exists(path));
        Assert.Null(Updater.TakeUpdateErrorFrom(path));
    }

    [Fact]
    public void TakeUpdateErrorFrom_MissingFileReturnsNull()
    {
        using var temp = new TempFolder();
        Assert.Null(Updater.TakeUpdateErrorFrom(Path.Combine(temp.Path, "missing.log")));
    }

    [Fact]
    public void UpdateBat_HasPayloadGuardAndBackupRestore()
    {
        var text = string.Join("\n", Updater.BuildUpdateBatLines());

        Assert.Contains("payload missing TwinCord.exe", text);
        Assert.Contains("payload missing TwinCord.dll", text);
        Assert.Contains("payload missing TwinCord.deps.json", text);
        Assert.Contains("payload missing TwinCord.runtimeconfig.json", text);
        Assert.Contains("ping -n 2 127.0.0.1", text);
        Assert.Contains("TC_BACKUP", text);
        Assert.Contains("robocopy \"%TC_BACKUP%\" \"%TC_DST%\" /MIR", text);

        // 復元の/MIRで消されないよう、失敗理由は復元後に書く
        var syncFailure = Assert.Single(
            Updater.BuildUpdateBatLines(), l => l.Contains("sync failed"));
        Assert.True(syncFailure.IndexOf("robocopy", StringComparison.Ordinal)
            < syncFailure.IndexOf("update-error.log", StringComparison.Ordinal));
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }

    private static string BuildReleaseJson(
        string assetUrl, string assetName = "TwinCord.zip",
        string? digest = null, string tag = "v1.11.0")
    {
        return JsonSerializer.Serialize(new
        {
            tag_name = tag,
            body = "release notes",
            assets = new[]
            {
                new
                {
                    name = assetName,
                    browser_download_url = assetUrl,
                    digest,
                },
            },
        });
    }

    private sealed class NullProgress : IProgress<double>
    {
        public void Report(double value) { }
    }
}
