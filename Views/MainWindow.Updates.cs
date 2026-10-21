using System.Windows;
using WinForms = System.Windows.Forms;

namespace TwinCord;

public partial class MainWindow : Window
{
    private async Task<string> CheckForUpdateAsync()
    {
        try
        {
            var info = await Updater.FetchLatestAsync(Http.Shared);
            if (info is null)
                return "確認できませんでした。";
            if (!Updater.IsNewer(Diagnostics.AppVersion(), info.Version))
                return $"最新です(v{Diagnostics.AppVersion()})。";
            if (string.IsNullOrEmpty(info.AssetUrl)
                || !Updater.IsTrustedAssetUrl(info.AssetUrl)
                || !Updater.IsValidSha256(info.Sha256))
                return $"新しいバージョン {info.Tag} があります。配布ファイルを検証できないため、GitHub Releasesから手動で更新してください。";
            if (Diagnostics.IsDevBuild() || !Updater.IsFolderLayout())
                return $"新しいバージョン {info.Tag} があります。この起動形式では自動更新できません。zip版を手動で配置してください。";
            return $"新しいバージョンがあります: {info.Tag}\nトレイメニューの更新確認か、起動時の通知から更新できます。";
        }
        catch (Exception ex)
        {
            return "確認できませんでした: " + ex.GetType().Name;
        }
    }

    /// <summary>前回の更新BATが残した失敗理由を、起動時に一度だけ知らせる。</summary>
    private void ShowPendingUpdateError()
    {
        var error = Updater.TakeUpdateError();
        if (string.IsNullOrWhiteSpace(error)) return;
        var detail = error.ReplaceLineEndings(" ").Trim();
        if (detail.Length > 300) detail = detail[..300] + "…";
        Log.Write("前回の更新失敗: " + detail);
        ShowBalloon(
            "TwinCord",
            "前回の更新に失敗したため、旧バージョンで起動しました。\n" + detail +
            "\n\nGitHub Releasesから手動で更新してください。",
            WinForms.ToolTipIcon.Warning,
            15000);
    }

    // ---- 起動時更新確認・ワンクリック更新 ----

    private Task CheckUpdateOnStartupAsync() =>
        _updates.StartupCheckAsync(info =>
        {
            if (!Updater.IsTrustedAssetUrl(info.AssetUrl) || !Updater.IsValidSha256(info.Sha256))
            {
                // 検証できない配布物をワンクリック更新として案内しない
                var manual = $"新しいバージョン {info.Tag} があります。手動で更新してください。";
                return Dispatcher.InvokeAsync(() =>
                {
                    ShowBalloon("TwinCord", manual, WinForms.ToolTipIcon.Warning, 15000);
                }).Task;
            }
            var text = $"新しいバージョン {info.Tag} があります。クリックで更新";
            return Dispatcher.InvokeAsync(() =>
            {
                ShowBalloon("TwinCord", text, WinForms.ToolTipIcon.Info, 15000, () => OpenUpdateDialog(info));
            }).Task;
        });

    private Task CheckUpdateFromTrayAsync() =>
        _updates.TrayCheckAsync(
            info =>
            {
                OpenUpdateDialog(info);
                return Task.CompletedTask;
            },
            (message, warning) =>
            {
                ShowBalloon("TwinCord", message,
                    warning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info, 5000);
                return Task.CompletedTask;
            });

    private void OpenUpdateDialog(UpdateInfo info)
    {
        if (_updateDialog?.IsLoaded == true)
        {
            _updateDialog.Activate();
            return;
        }
        var w = new UpdateDialog(info) { Owner = IsVisible ? this : null };
        _updateDialog = w;
        bool applied;
        try
        {
            applied = w.ShowDialog() == true;
        }
        finally
        {
            _updateDialog = null;
        }
        if (applied)
        {
            _exiting = true;
            Close();
        }
    }

}
