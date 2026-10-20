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
            if (Updater.IsNewer(Diagnostics.AppVersion(), info.Version))
                return $"新しいバージョンがあります: {info.Tag}\nトレイメニューの更新確認か、起動時の通知から更新できます。";
            return $"最新です(v{Diagnostics.AppVersion()})。";
        }
        catch (Exception ex)
        {
            return "確認できませんでした: " + ex.GetType().Name;
        }
    }

    // ---- 起動時更新確認・ワンクリック更新 ----

    private Task CheckUpdateOnStartupAsync() =>
        _updates.StartupCheckAsync(info =>
        {
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
