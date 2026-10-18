using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace TwinCord;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public Func<Task<string>>? Diagnose { get; set; }
    public Func<Task<string>>? CheckUpdate { get; set; }
    // エクスポートは画面の編集内容(draft)を渡す。確定前の変更もバックアップできる。
    public Func<AppSettings, string?>? ExportRequested { get; set; }
    public Func<string?>? ImportRequested { get; set; }
    public Action? TestNotifyRequested { get; set; }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        WindowIcon.Set(this);
        _settings = settings;
        Loaded += SettingsWindow_Loaded;
    }

    private void SettingsWindow_Loaded(object sender, RoutedEventArgs e) => ApplyToUi();

    /// <summary>設定値(編集用コピー)を画面へ反映する。</summary>
    private void ApplyToUi()
    {
        StartBox.IsChecked = _settings.StartWithWindows;
        TrayBox.IsChecked = _settings.MinimizeToTray;
        StartMinBox.IsChecked = _settings.StartMinimized;
        BadgeBox.IsChecked = _settings.UnreadBadge;
        UnreadBox.IsChecked = _settings.CountUnreadChannels;
        ReloadBox.IsChecked = _settings.AutoReload;
        UpdateCheckBox.IsChecked = _settings.AutoUpdateCheck;
        NotifyBox.IsChecked = _settings.NotifyEnabled;
        SkipViewedBox.IsChecked = _settings.SkipViewedAccount;
        HiddenOnlyBox.IsChecked = _settings.NotifyHiddenOnly;
        HideDlBox.IsChecked = _settings.HideDownloadButton;
        Zoom1Slider.Value = _settings.Zoom1;
        Zoom2Slider.Value = _settings.Zoom2;
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
    /// インポート後の内容を編集用コピーと画面へ反映する。
    /// これをしないと、インポート後にOKを押したとき draft(インポート前)で上書きされてしまう。
    /// </summary>
    public void ReloadFrom(AppSettings settings)
    {
        _settings.CopyFrom(settings);
        ApplyToUi();
    }

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
        _settings.StartWithWindows = StartBox.IsChecked == true;
        _settings.MinimizeToTray = TrayBox.IsChecked == true;
        _settings.StartMinimized = StartMinBox.IsChecked == true;
        _settings.UnreadBadge = BadgeBox.IsChecked == true;
        _settings.CountUnreadChannels = UnreadBox.IsChecked == true;
        _settings.AutoReload = ReloadBox.IsChecked == true;
        _settings.AutoUpdateCheck = UpdateCheckBox.IsChecked == true;
        _settings.NotifyEnabled = NotifyBox.IsChecked == true;
        _settings.SkipViewedAccount = SkipViewedBox.IsChecked == true;
        _settings.NotifyHiddenOnly = HiddenOnlyBox.IsChecked == true;
        _settings.HideDownloadButton = HideDlBox.IsChecked == true;
        _settings.Zoom1 = AppSettings.ClampZoom(Zoom1Slider.Value);
        _settings.Zoom2 = AppSettings.ClampZoom(Zoom2Slider.Value);
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
        TransferResult.Text = ExportRequested(_settings) ?? "";
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        TransferResult.Text = ImportRequested is null ? "利用できません。" : (ImportRequested() ?? "");
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
