using System.Net.Http;

namespace TwinCord.Services;

/// <summary>
/// 更新確認のUI前段。表示(バルーン・ダイアログ)は呼び出し側が行う。
/// </summary>
public sealed class UpdateService
{
    private readonly HttpClient _http;
    private readonly Func<string> _currentVersion;
    private readonly Func<bool> _isExiting;

    public UpdateService(HttpClient http, Func<string> currentVersion, Func<bool> isExiting)
    {
        _http = http;
        _currentVersion = currentVersion;
        _isExiting = isExiting;
    }

    /// <summary>新版があれば情報を返す。なければnull。</summary>
    public async Task<UpdateInfo?> GetNewerAsync()
    {
        var info = await Updater.FetchLatestAsync(_http);
        if (info is null || string.IsNullOrEmpty(info.AssetUrl)) return null;
        if (!Updater.IsNewer(_currentVersion(), info.Version)) return null;
        return info;
    }

    public async Task StartupCheckAsync(Func<UpdateInfo, Task> onNewer)
    {
        try
        {
            // 起動直後の混雑(Discord読み込み)を避けてから確認
            await Task.Delay(TimeSpan.FromSeconds(15));
            if (_isExiting()) return;
            var info = await GetNewerAsync();
            if (info is null) return;
            await onNewer(info);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("起動時の更新確認に失敗: " + ex.Message);
        }
    }

    public async Task TrayCheckAsync(Func<UpdateInfo, Task> onNewer, Func<string, bool, Task> onMessage)
    {
        try
        {
            var info = await Updater.FetchLatestAsync(_http);
            if (info is null)
            {
                await onMessage("確認できませんでした。", true);
                return;
            }
            if (!Updater.IsNewer(_currentVersion(), info.Version))
            {
                await onMessage($"最新です(v{_currentVersion()})。", false);
                return;
            }
            if (string.IsNullOrEmpty(info.AssetUrl))
            {
                // 配布ファイルが無い、または取得先が想定外(手動更新が必要)
                await onMessage($"新しいバージョン {info.Tag} があります。手動で更新してください。", true);
                return;
            }
            await onNewer(info);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("更新確認に失敗: " + ex.Message);
            // 通信エラーでも無反応にしない(メニューを押したのに何も起きない状態を避ける)
            try { await onMessage("確認できませんでした(通信エラー)。", true); } catch { }
        }
    }
}
