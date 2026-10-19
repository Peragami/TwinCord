using System.Text.Json;
using TwinCord.Services;

namespace TwinCord.Tests;

public class PageScriptsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Probe_IsSelfContainedJsonReturningScript(bool includeUnread)
    {
        var script = PageScripts.Probe(includeUnread);
        Assert.Contains("JSON.stringify", script);
        Assert.Contains("document.querySelector(", script);
        // 未読チャンネルの走査は設定に応じて埋め込む(既定はOFF)
        Assert.Equal(includeUnread, script.Contains("unreadPill"));
        // 以前は最大29回DOM走査していたのを、1つのセレクタにまとめている
        Assert.Contains("rtcConnectionStatus", script);
    }

    [Fact]
    public void Probe_VoiceSelectorCoversMajorLanguages()
    {
        var parts = VoiceSelector().Split(',');
        Assert.Contains("[aria-label=\"切断\"]", parts);
        Assert.Contains("[aria-label=\"Disconnect\"]", parts);
        Assert.Contains("[aria-label=\"Déconnecter\"]", parts);
        Assert.Contains("[aria-label=\"Отключиться\"]", parts);
        // ロケール非依存のRTC要素も保険として残す
        Assert.Contains("[class*=\"rtcConnectionStatus\"]", parts);
    }

    [Fact]
    public void Probe_VoiceSelectorIsAValidSelectorList()
    {
        // 括弧・引用符が混ざるとquerySelectorが例外になるため、組み立て結果を検証する
        var parts = VoiceSelector().Split(',');
        Assert.True(parts.Length > 25, $"セレクタ数が足りない: {parts.Length}");
        foreach (var part in parts)
            Assert.Matches("""^\[[a-z-]+[*^$~|]?="[^"\[\]]+"\]$""", part);
    }

    /// <summary>生成されたスクリプトから通話判定セレクタを取り出す(JSON文字列として埋め込まれている)。</summary>
    private static string VoiceSelector()
    {
        var script = PageScripts.Probe(includeUnread: false);
        var m = System.Text.RegularExpressions.Regex.Match(
            script, "document\\.querySelector\\((?<sel>\".*?\")\\) \\? '1' : '0'",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(m.Success, "セレクタがスクリプトから見つからない");
        var selector = JsonSerializer.Deserialize<string>(m.Groups["sel"].Value);
        Assert.NotNull(selector);
        return selector!;
    }

    [Fact]
    public void HideDownload_DefinesToggleableApplier()
    {
        // 設定のON/OFFをホストから渡して、同じ文書で適用/解除できるようにする
        Assert.Contains("__tcHideDlApply", PageScripts.HideDownload);
        Assert.Contains("MutationObserver", PageScripts.HideDownload);
        Assert.Contains("disconnect", PageScripts.HideDownload);     // 解除時は監視を止める
        Assert.Contains("removeProperty", PageScripts.HideDownload); // 隠した要素を元へ戻す
        Assert.Contains("style.remove()", PageScripts.HideDownload); // 隠すCSSも外す
    }

    [Fact]
    public void HideDownload_PrunesLargeContainersBeforeReadingText()
    {
        // DOM全体の走査負荷対策: 子要素が多い入れ物はtextContentを読む前に除外する
        Assert.Contains("childElementCount", PageScripts.HideDownload);
    }

    [Fact]
    public void HideDownloadApply_PassesBooleanToPageFunction()
    {
        Assert.Contains("__tcHideDlApply(true)", PageScripts.HideDownloadApply(true));
        Assert.Contains("__tcHideDlApply(false)", PageScripts.HideDownloadApply(false));
        // 未注入なら式全体がundefinedのままなのでホスト側へnullが返り「効いていない」ことを検知できる
        Assert.Contains("window.__tcHideDlApply &&", PageScripts.HideDownloadApply(true));
        Assert.Contains("return true", PageScripts.HideDownload);
    }

    [Fact]
    public void Notify_HooksTopFrameOnly()
    {
        Assert.Contains("window.top !== window", PageScripts.Notify);
        Assert.Contains("__tcNotifyHook", PageScripts.Notify);
        Assert.Contains("postMessage", PageScripts.Notify);
    }

    [Fact]
    public void Hotkey_FiltersTextInput()
    {
        Assert.Contains("window.top !== window", PageScripts.Hotkey);
        Assert.Contains("contenteditable", PageScripts.Hotkey);
        Assert.Contains("switch:", PageScripts.Hotkey);
    }

    [Fact]
    public void TryParseProbe_AcceptsExecuteScriptResult()
    {
        // ExecuteScriptAsyncの戻り値は「JSON文字列」なので、中身がさらにエンコードされている
        var raw = JsonSerializer.Serialize("""{"b":3,"u":2,"v":"1"}""");
        Assert.True(PageScripts.TryParseProbe(raw, out var r));
        Assert.Equal(3, r.Badge);
        Assert.Equal(2, r.Unread);
        Assert.True(r.VoiceKnown);
        Assert.True(r.InVoice);
    }

    [Fact]
    public void TryParseProbe_AllowsUnknownVoice()
    {
        var raw = JsonSerializer.Serialize("""{"b":0,"u":0,"v":"?"}""");
        Assert.True(PageScripts.TryParseProbe(raw, out var r));
        Assert.False(r.VoiceKnown);
        Assert.False(r.InVoice);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\"\"")]
    [InlineData("not json")]
    [InlineData("\"{broken")]
    [InlineData("\"ERR\"")]          // スクリプト内部エラー
    [InlineData("\"{\\\"b\\\":-1,\\\"u\\\":0,\\\"v\\\":\\\"0\\\"}\"")] // 負値は異常
    [InlineData("\"{\\\"u\\\":0,\\\"v\\\":\\\"0\\\"}\"")]              // バッジ欠落
    public void TryParseProbe_RejectsBrokenPayload(string? raw)
    {
        Assert.False(PageScripts.TryParseProbe(raw, out _));
    }
}
