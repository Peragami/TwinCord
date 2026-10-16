using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace TwinCord;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public Func<Task<string>>? Diagnose { get; set; }
    public Func<string?>? ExportRequested { get; set; }
    public Func<string?>? ImportRequested { get; set; }
    public Action? TestNotifyRequested { get; set; }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        WindowIcon.Set(this);
        _settings = settings;
        Loaded += SettingsWindow_Loaded;
    }

    private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        StartBox.IsChecked = _settings.StartWithWindows;
        TrayBox.IsChecked = _settings.MinimizeToTray;
        BadgeBox.IsChecked = _settings.UnreadBadge;
        UnreadBox.IsChecked = _settings.CountUnreadChannels;
        ReloadBox.IsChecked = _settings.AutoReload;
        Zoom1Slider.Value = _settings.Zoom1;
        Zoom2Slider.Value = _settings.Zoom2;
        UpdateZoomLabels();

        VersionText.Text = "バージョン: " + Diagnostics.AppVersion();
        DataText.Text = AppSettings.DataFolder();
        try
        {
            RuntimeText.Text = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch
        {
            RuntimeText.Text = "不明";
        }
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

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _settings.StartWithWindows = StartBox.IsChecked == true;
        _settings.MinimizeToTray = TrayBox.IsChecked == true;
        _settings.UnreadBadge = BadgeBox.IsChecked == true;
        _settings.CountUnreadChannels = UnreadBox.IsChecked == true;
        _settings.AutoReload = ReloadBox.IsChecked == true;
        _settings.Zoom1 = AppSettings.ClampZoom(Zoom1Slider.Value);
        _settings.Zoom2 = AppSettings.ClampZoom(Zoom2Slider.Value);
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
        }
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        TransferResult.Text = ExportRequested is null ? "利用できません。" : (ExportRequested() ?? "");
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
}
