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
        catch { }
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
            if (!Updater.IsNewer(_currentVersion(), info.Version) || string.IsNullOrEmpty(info.AssetUrl))
            {
                await onMessage($"最新です(v{_currentVersion()})。", false);
                return;
            }
            await onNewer(info);
        }
        catch { }
    }

    public async Task<string> ManualCheckMessageAsync()
    {
        try
        {
            var info = await Updater.FetchLatestAsync(_http);
            if (info is null)
                return "確認できませんでした。";
            if (Updater.IsNewer(_currentVersion(), info.Version))
                return $"新しいバージョンがあります: {info.Tag}\nトレイメニューの更新確認か、起動時の通知から更新できます。";
            return $"最新です(v{_currentVersion()})。";
        }
        catch (Exception ex)
        {
            return "確認できませんでした: " + ex.GetType().Name;
        }
    }
}
