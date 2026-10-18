using System.Net.Http;

namespace TwinCord;

/// <summary>
/// アプリ全体で共有するHttpClient。更新確認とアバター取得で同じ設定を使う
/// (ソケットの使い回しとタイムアウトの一元化のため)。
/// </summary>
public static class Http
{
    public static readonly HttpClient Shared = Create();

    private static HttpClient Create()
    {
        var client = new HttpClient { Timeout = AppConstants.HttpTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppConstants.AppName);
        return client;
    }
}
