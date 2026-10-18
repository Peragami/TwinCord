using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TwinCord;

namespace TwinCord.Tests;

public class UpdaterTests
{
    private const string DefaultAssetUrl =
        "https://github.com/Peragami/TwinCord/releases/download/v1.10.0/TwinCord.exe";

    [Theory]
    [InlineData(DefaultAssetUrl, true)]
    [InlineData("https://github.com/peragami/twincord/releases/download/v1.10.0/TwinCord.exe", true)]
    [InlineData("http://github.com/Peragami/TwinCord/releases/download/v1.10.0/TwinCord.exe", false)]
    [InlineData("https://evil.example/Peragami/TwinCord/releases/download/v1.10.0/TwinCord.exe", false)]
    [InlineData("https://github.com/Other/TwinCord/releases/download/v1.10.0/TwinCord.exe", false)]
    [InlineData("https://github.com/Peragami/TwinCord/releases/tag/v1.10.0", false)]
    [InlineData("https://github.com/Peragami/TwinCord/releases/download-evil/v1.10.0/TwinCord.exe", false)]
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
    [InlineData("bad", "1.0.0", false)]
    [InlineData("1.0.0", "bad", false)]
    public void IsNewer_ReturnsExpected(string current, string latest, bool expected)
    {
        Assert.Equal(expected, Updater.IsNewer(current, latest));
    }

    [Fact]
    public async Task FetchLatestAsync_ParsesTagNotesAssetAndDigest()
    {
        var json = BuildReleaseJson(DefaultAssetUrl, digest: "sha256:ABC123");
        using var http = new HttpClient(new StubHttpMessageHandler(json));

        var info = await Updater.FetchLatestAsync(http);

        Assert.NotNull(info);
        Assert.Equal("v1.10.0", info!.Tag);
        Assert.Equal("1.10.0", info.Version);
        Assert.Equal("release notes", info.Notes);
        Assert.Equal(DefaultAssetUrl, info.AssetUrl);
        Assert.Equal("ABC123", info.Sha256);
    }

    [Fact]
    public async Task FetchLatestAsync_IgnoresUntrustedAssetHost()
    {
        var json = BuildReleaseJson("https://evil.example/TwinCord.exe", digest: "sha256:ABC123");
        using var http = new HttpClient(new StubHttpMessageHandler(json));

        var info = await Updater.FetchLatestAsync(http);

        Assert.NotNull(info);
        Assert.Equal("", info!.AssetUrl);
        Assert.Equal("", info.Sha256);
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
    public void VerifySha256_EmptyExpectedSkipsVerification()
    {
        using var temp = new TempFolder();
        Assert.True(Updater.VerifySha256(Path.Combine(temp.Path, "missing.bin"), ""));
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
        var dest = Path.Combine(temp.Path, "nested", "TwinCord.new.exe");

        await Updater.DownloadAsync(http, "https://example.test/TwinCord.exe", dest,
            new NullProgress(), CancellationToken.None);

        Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
    }

    private static string BuildReleaseJson(
        string assetUrl, string assetName = "TwinCord.exe",
        string? digest = "sha256:ABC123", string tag = "v1.10.0")
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
