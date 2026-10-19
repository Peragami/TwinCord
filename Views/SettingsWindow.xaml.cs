using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace TwinCord;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _draft;

    public Func<Task<string>>? Diagnose { get; set; }
    public Func<Task<string>>? CheckUpdate { get; set; }
    // エクスポートは画面の編集内容(draft)を渡す。確定前の変更もバックアップできる。
    public Func<AppSettings, string>? ExportRequested { get; set; }
    public Func<string>? ImportRequested { get; set; }
    public Action? TestNotifyRequested { get; set; }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        WindowIcon.Set(this);
        _draft = settings;
        Loaded += SettingsWindow_Loaded;
    }

    private void SettingsWindow_Loaded(object sender, RoutedEventArgs e) => ApplyToUi();

    /// <summary>設定値(編集用コピー)を画面へ反映する。</summary>
    private void ApplyToUi()
    {
        StartBox.IsChecked = _draft.StartWithWindows;
        TrayBox.IsChecked = _draft.MinimizeToTray;
        StartMinBox.IsChecked = _draft.StartMinimized;
        BadgeBox.IsChecked = _draft.UnreadBadge;
        UnreadBox.IsChecked = _draft.CountUnreadChannels;
        ReloadBox.IsChecked = _draft.AutoReload;
        UpdateCheckBox.IsChecked = _draft.AutoUpdateCheck;
        NotifyBox.IsChecked = _draft.NotifyEnabled;
        SkipViewedBox.IsChecked = _draft.SkipViewedAccount;
        HiddenOnlyBox.IsChecked = _draft.NotifyHiddenOnly;
        HideDlBox.IsChecked = _draft.HideDownloadButton;
        Zoom1Slider.Value = _draft.Zoom1;
        Zoom2Slider.Value = _draft.Zoom2;
        UpdateZoomLabels();

        VersionText.Text = "バージョン: " + Diagnostics.AppVersion();
        // 変更履歴はREADMEの「バージョン履歴」節(埋め込み)をそのまま表示する
        var history = VersionHistory.Text;
        HistoryText.Text = history.Length > 0
            ? history
            : "変更履歴を読み込めませんでした。READMEの「バージョン履歴」を参照してください。";
        DataText.Text = AppSettings.DataFolder();
        ExeText.Text = Diagnostics.ExePath();
        try
        {
            RuntimeText.Text = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch
        {
            RuntimeText.Text = "不明";
        }
    }

    /// <summary>
    /// 編集中の設定(「保存」で確定する)。インポート先として使う。
    /// ここへ書いておけば、キャンセルされた場合は元の設定が残る。
    /// </summary>
    public AppSettings Draft => _draft;

    /// <summary>編集中の内容を画面へ反映し直す(インポート後に使う)。</summary>
    public void RefreshUi() => ApplyToUi();

    private void ZoomSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateZoomLabels();
    }

    private void UpdateZoomLabels()
    {
        if (Zoom1Value is null || Zoom2Value is null) return;
        Zoom1Value.Text = (int)(Zoom1Slider.Value * 100) + "%";
        Zoom2Value.Text = (int)(Zoom2Slider.Value * 100) + "%";
    }

    /// <summary>画面の現在値を編集用コピーへ反映する(OKを押す前のエクスポートでも最新値を出す)。</summary>
    private void ApplyUiToDraft()
    {
        _draft.StartWithWindows = StartBox.IsChecked == true;
        _draft.MinimizeToTray = TrayBox.IsChecked == true;
        _draft.StartMinimized = StartMinBox.IsChecked == true;
        _draft.UnreadBadge = BadgeBox.IsChecked == true;
        _draft.CountUnreadChannels = UnreadBox.IsChecked == true;
        _draft.AutoReload = ReloadBox.IsChecked == true;
        _draft.AutoUpdateCheck = UpdateCheckBox.IsChecked == true;
        _draft.NotifyEnabled = NotifyBox.IsChecked == true;
        _draft.SkipViewedAccount = SkipViewedBox.IsChecked == true;
        _draft.NotifyHiddenOnly = HiddenOnlyBox.IsChecked == true;
        _draft.HideDownloadButton = HideDlBox.IsChecked == true;
        _draft.Zoom1 = AppSettings.ClampZoom(Zoom1Slider.Value);
        _draft.Zoom2 = AppSettings.ClampZoom(Zoom2Slider.Value);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ApplyUiToDraft();
        DialogResult = true;
    }

    private async void DiagButton_Click(object sender, RoutedEventArgs e)
    {
        if (Diagnose is null)
        {
            DiagResult.Text = "診断機能が利用できません。";
            return;
        }
        DiagButton.IsEnabled = false;
        DiagResult.Text = "診断中…";
        try
        {
            DiagResult.Text = await Diagnose();
        }
        catch (Exception ex)
        {
            DiagResult.Text = "診断エラー: " + ex.Message;
        }
        finally
        {
            DiagButton.IsEnabled = true;
            DiagCopyButton.IsEnabled = DiagResult.Text.Length > 0;
        }
    }

    private void DiagCopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DiagResult.Text.Length > 0)
                System.Windows.Clipboard.SetText(DiagResult.Text);
        }
        catch { }
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (ExportRequested is null)
        {
            TransferResult.Text = "利用できません。";
            return;
        }
        // まだOKしていない変更もバックアップに含める
        ApplyUiToDraft();
        TransferResult.Text = ExportRequested(_draft);
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        TransferResult.Text = ImportRequested is null ? "利用できません。" : ImportRequested();
    }

    private void TestNotifyButton_Click(object sender, RoutedEventArgs e)
    {
        TestNotifyRequested?.Invoke();
        TransferResult.Text = "テスト通知を送信しました。表示されなければログを確認してください。";
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (CheckUpdate is null)
        {
            UpdateResult.Text = "確認機能が利用できません。";
            return;
        }
        UpdateButton.IsEnabled = false;
        UpdateResult.Text = "確認中…";
        try
        {
            UpdateResult.Text = await CheckUpdate();
        }
        catch (Exception ex)
        {
            UpdateResult.Text = "確認エラー: " + ex.Message;
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }
}
