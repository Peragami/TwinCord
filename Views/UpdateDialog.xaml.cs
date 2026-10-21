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
        // 更新は配布zip(TwinCord.zip)を1つダウンロードし、フォルダへミラー同期する方式。
        // dataとmods(将来)は同期の対象外なので、設定・ログイン情報はそのまま残る。
        if (Diagnostics.IsDevBuild() || !Updater.IsFolderLayout())
        {
            // 開発ビルド・単一exeでは入れ替えを行わない(作業中のファイルを壊さないため)
            UpdateButton.IsEnabled = false;
            StatusText.Text = "この起動形式では自動更新できません。zip版を手動で配置してください。";
        }
        else if (string.IsNullOrEmpty(info.AssetUrl) || !Updater.IsTrustedAssetUrl(info.AssetUrl))
        {
            UpdateButton.IsEnabled = false;
            StatusText.Text = "配布ファイルの取得先を確認できないため、自動更新を中止しました。手動で更新してください。";
        }
        else if (!Updater.IsValidSha256(info.Sha256))
        {
            // ハッシュが無い・不正な配布物は検証できないため自動更新しない(手動更新へ誘導する)
            UpdateButton.IsEnabled = false;
            StatusText.Text = "配布ファイルのハッシュを確認できないため、自動更新を中止しました。GitHub Releasesから手動で更新してください。";
        }
    }

    /// <summary>更新中はボタンを止めて進捗を出し、終わったら元へ戻す。</summary>
    private void SetBusy(bool busy)
    {
        UpdateButton.IsEnabled = !busy;
        LaterButton.IsEnabled = !busy;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        DialogResult = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _cts?.Cancel(); } catch { }
        // 適用せずに閉じた場合は、ダウンロード済みのzipと展開先を残さない
        if (!_applied) Updater.CleanStage();
        base.OnClosed(e);
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        var appDir = AppSettings.AppDir();
        if (string.IsNullOrEmpty(appDir))
        {
            StatusText.Text = "実行ファイルの場所が特定できません。手動で更新してください。";
            return;
        }
        SetBusy(true);
        _cts = new CancellationTokenSource();
        try
        {
            StatusText.Text = "ダウンロード中…";
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TwinCord");
            var zip = Updater.StageZipPath();
            Updater.CleanStage(); // 前回中断した残骸を残さない
            var progress = new Progress<double>(v => Progress.Value = v);
            await Updater.DownloadAsync(http, _info.AssetUrl, zip, progress, _cts.Token);
            StatusText.Text = "検証中…";
            if (!Updater.VerifySha256(zip, _info.Sha256))
            {
                StatusText.Text = "配布ファイルの検証に失敗しました。手動で更新してください。";
                Updater.CleanStage();
                SetBusy(false);
                return;
            }
            StatusText.Text = "展開中…";
            var extractDir = Updater.StageExtractDir();
            await Task.Run(() => Updater.ExtractZip(zip, extractDir), _cts.Token);
            if (_cts.IsCancellationRequested || !IsLoaded)
            {
                // 閉じた後に展開が完了した場合、OnClosedの掃除より後に残ることがある
                Updater.CleanStage();
                return;
            }
            StatusText.Text = "入れ替え準備中…";
            var payloadError = Updater.ValidatePayload(extractDir);
            if (payloadError is not null)
            {
                StatusText.Text = "配布ファイルの内容が不正なため更新を中止しました: " + payloadError;
                Updater.CleanStage();
                SetBusy(false);
                return;
            }
            if (!IsLoaded) return; // 閉じ→完了のレース時は適用しない
            try
            {
                Updater.ApplyAndRestart(Environment.ProcessId, extractDir, appDir);
                // BATの起動に成功したら、以降はBATがステージを片付けるため消さない
                _applied = true;
            }
            catch
            {
                _applied = false;
                throw;
            }
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "キャンセルしました。";
            SetBusy(false);
        }
        catch (Exception ex)
        {
            if (!_applied) Updater.CleanStage();
            StatusText.Text = "更新に失敗しました: " + ex.Message;
            SetBusy(false);
        }
    }
}
