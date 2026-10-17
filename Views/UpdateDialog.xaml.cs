using System.IO;
using System.Net.Http;
using System.Windows;

namespace TwinCord;

public partial class UpdateDialog : Window
{
    private readonly UpdateInfo _info;
    private CancellationTokenSource? _cts;
    private bool _applied;

    public UpdateDialog(UpdateInfo info)
    {
        InitializeComponent();
        WindowIcon.Set(this);
        _info = info;
        CurrentText.Text = "現在: v" + Diagnostics.AppVersion();
        LatestText.Text = "最新: " + info.Tag;
        NotesText.Text = string.IsNullOrWhiteSpace(info.Notes) ? "(変更内容の記載なし)" : info.Notes;
        if (Updater.IsSelfContainedBuild)
        {
            UpdateButton.IsEnabled = false;
            StatusText.Text = "このビルド(自己完結版)は自動更新の対象外です。手動で更新してください。";
        }
        else if (string.IsNullOrEmpty(info.AssetUrl))
        {
            UpdateButton.IsEnabled = false;
            StatusText.Text = "配布ファイルが見つかりません。手動で更新してください。";
        }
        else if (string.IsNullOrEmpty(info.Sha256))
        {
            // 取得元の情報が欠けている場合も入れ替えは行えるが、改ざん検証ができないことを明示する
            StatusText.Text = "配布ファイルのハッシュが取得できないため、改ざん検証なしで更新します。";
        }
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        DialogResult = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _cts?.Cancel(); } catch { }
        // 適用せずに閉じた場合は、ダウンロード済みの一時ファイルを残さない
        if (!_applied)
        {
            try { File.Delete(Updater.StagePath()); } catch { }
        }
        base.OnClosed(e);
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            StatusText.Text = "実行ファイルの場所が特定できません。手動で更新してください。";
            return;
        }
        UpdateButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        _cts = new CancellationTokenSource();
        try
        {
            StatusText.Text = "ダウンロード中…";
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TwinCord");
            var dest = Updater.StagePath();
            try { File.Delete(dest); } catch { } // 前回中断した残骸を残さない
            var progress = new Progress<double>(v => Progress.Value = v);
            await Updater.DownloadAsync(http, _info.AssetUrl, dest, progress, _cts.Token);
            StatusText.Text = "検証中…";
            if (string.IsNullOrEmpty(_info.Sha256))
                Diagnostics.Log("更新: ハッシュが無いため改ざん検証をスキップ");
            if (!Updater.VerifySha256(dest, _info.Sha256))
            {
                StatusText.Text = "配布ファイルの検証に失敗しました。手動で更新してください。";
                try { File.Delete(dest); } catch { }
                UpdateButton.IsEnabled = true;
                LaterButton.IsEnabled = true;
                Progress.Visibility = Visibility.Collapsed;
                return;
            }
            StatusText.Text = "入れ替え準備中…";
            if (!IsLoaded) return; // 閉じ→完了のレース時は適用しない
            _applied = true; // ここから先は一時ファイルを置き換えに使うため消さない
            Updater.ApplyAndRestart(Environment.ProcessId, dest, exe);
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "キャンセルしました。";
            UpdateButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            StatusText.Text = "更新に失敗しました: " + ex.Message;
            UpdateButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
        }
    }
}
