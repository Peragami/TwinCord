using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfButton = System.Windows.Controls.Button;
using WpfImage = System.Windows.Controls.Image;
using WpfPoint = System.Windows.Point;

namespace TwinCord.Services;

/// <summary>
/// 1アカウント分のアバター画像。ページから得たURLの検証・取得・保存と、
/// レールのボタンへの表示(画像または頭文字)を担当する。
/// デコード済みのBitmapSourceとImageコントロールは使い回す
/// (切り替えのたびに作り直すとGCと再描画が発生する)。
/// </summary>
public sealed class AccountAvatar
{
    private readonly int _account;
    private readonly WpfButton _button;
    private readonly Func<string> _accountName;
    private string _url = "";
    private DateTime _stamp;
    private BitmapSource? _bmp;
    private WpfImage? _image;

    public AccountAvatar(int account, WpfButton button, Func<string> accountName)
    {
        _account = account;
        _button = button;
        _accountName = accountName;
    }

    public bool HasFile => File.Exists(PathOf(_account));

    public static string PathOf(int n) => Path.Combine(
        AppSettings.DataFolder(), AppConstants.AvatarFolder, $"acc{n}.png");

    /// <summary>
    /// ページから取得したURLが前回と違えば画像を取り直して表示を更新する。
    /// URLが不正・取得失敗のときは何もしない(表示は現状維持)。
    /// </summary>
    public async Task UpdateAsync(string rawUrl)
    {
        var url = rawUrl.Trim('"');
        if (string.IsNullOrEmpty(url)) return;

        // ページDOM由来のURLをそのまま取得しない。
        // DiscordのCDNだけを許可し、ローカルファイルや任意ホストへの誘導を防ぐ。
        if (!IsTrustedUrl(url))
        {
            Log.Write($"アバターURLを拒否(acc{_account}): {url}");
            return;
        }

        var path = PathOf(_account);
        if (url == _url && File.Exists(path)) return;

        var bytes = await DownloadAsync(url);
        if (bytes is null || bytes.Length == 0) return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // 直接上書きせず一時ファイル経由にする(書き込み途中の画像でアイコンが壊れない)
        var tmp = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tmp, bytes);

            // 画像としてデコードできることを確認してから置換する
            _ = LoadBitmap(tmp);

            File.Move(tmp, path, true);
        }
        catch (Exception ex)
        {
            Log.Write($"アバター保存失敗(acc{_account}): {ex.Message}");
            try { File.Delete(tmp); } catch { }
            return;
        }

        _url = url;
        Apply();
    }

    /// <summary>保存済み画像(なければ頭文字)をボタンへ表示する。</summary>
    public void Apply()
    {
        var path = PathOf(_account);
        if (File.Exists(path))
        {
            try
            {
                var stamp = File.GetLastWriteTimeUtc(path);
                if (_bmp is null || _stamp != stamp)
                {
                    _bmp = LoadBitmap(path);
                    _stamp = stamp;
                }
                _image ??= CreateImage();
                if (!ReferenceEquals(_image.Source, _bmp)) _image.Source = _bmp;
                if (!ReferenceEquals(_button.Content, _image)) _button.Content = _image;
                return;
            }
            catch { }
        }
        _button.Content = InitialOf(_accountName());
    }

    /// <summary>Discordの画像CDN(https・既定ポート)だけを許可する。</summary>
    private static bool IsTrustedUrl(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort) return false;
        if (!string.IsNullOrEmpty(u.UserInfo)) return false;

        var host = u.Host;
        return host.Equals("discord.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("discordapp.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".discordapp.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("discordapp.net", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".discordapp.net", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>上限付きで画像を取得する。Content-Lengthが信用できない場合も読みながら打ち切る。</summary>
    private static async Task<byte[]?> DownloadAsync(string url)
    {
        using var cts = new CancellationTokenSource(AppConstants.HttpTimeout);
        using var res = await Http.Shared.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        res.EnsureSuccessStatusCode();
        if (res.Content.Headers.ContentLength is > AppConstants.AvatarMaxBytes) return null;

        await using var stream = await res.Content.ReadAsStreamAsync(cts.Token);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buf.AsMemory(), cts.Token)) > 0)
        {
            if (ms.Length + read > AppConstants.AvatarMaxBytes) return null;
            ms.Write(buf, 0, read);
        }
        return ms.ToArray();
    }

    private static BitmapSource LoadBitmap(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path);
        bmp.DecodePixelWidth = 96;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static WpfImage CreateImage()
    {
        var img = new WpfImage
        {
            Width = 40,
            Height = 40,
            Stretch = Stretch.UniformToFill,
        };
        img.Clip = new EllipseGeometry(new WpfPoint(20, 20), 20, 20);
        return img;
    }

    private static string InitialOf(string name)
    {
        var t = (name ?? "").Trim();
        return t.Length == 0 ? "?" : t[..1].ToUpperInvariant();
    }
}
