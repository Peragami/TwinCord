using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace TwinCord.Tests;

/// <summary>UpdaterのHTTP処理を外部通信なしで検証するためのスタブ。</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly byte[] _body;
    private readonly string? _contentType;

    public StubHttpMessageHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _status = status;
        _body = Encoding.UTF8.GetBytes(body);
        _contentType = "application/json";
    }

    public StubHttpMessageHandler(byte[] body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _status = status;
        _body = body;
        _contentType = null;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var content = new ByteArrayContent(_body);
        if (_contentType is not null)
            content.Headers.ContentType = new MediaTypeHeaderValue(_contentType);
        return Task.FromResult(new HttpResponseMessage(_status)
        {
            Content = content,
            RequestMessage = request,
        });
    }
}

/// <summary>テストごとに独立した一時フォルダを作り、終了時に削除する。</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "TwinCordTests", Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { }
    }
}
