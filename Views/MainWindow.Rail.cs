using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace TwinCord;

public partial class MainWindow : Window
{
    // ---- アカウント切替 ----

    private string AccName(int n) => n == 1 ? _settings.Acc1Name : _settings.Acc2Name;

    private void Acc1Button_Click(object sender, RoutedEventArgs e) => SwitchTo(1);
    private void Acc2Button_Click(object sender, RoutedEventArgs e) => SwitchTo(2);
    private void Acc1Label_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => SwitchTo(1);
    private void Acc2Label_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => SwitchTo(2);

    private async void SwitchTo(int n)
    {
        if (n is not (1 or 2)) return;
        _current = n;
        if (n == 1) Badge1.Visibility = Visibility.Collapsed;
        else Badge2.Visibility = Visibility.Collapsed;
        RefreshRail();
        try
        {
            await EnsureViewAsync(n);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "TwinCord init error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (_current != n) return;
        RefreshRail();
        // 通話中は両方とも抑制しない(音声優先)。通常時は非表示側のヒープを絞る
        _watchdog?.RefreshMemoryLevels();
        // 切り替えた直後はバッジ/通話の確認を早める(変化が無くて間隔が伸びていても戻す)
        _watchdog?.WakeUp();
    }

    // ---- レール表示・改名 ----

    private void RefreshRail()
    {
        _watchdog?.RefreshAccountIcons();
        Acc1Button.ToolTip = _settings.Acc1Name + (IsMuted(View1) ? " (ミュート中)" : "");
        Acc1Label.Text = _settings.Acc1Name;
        Acc2Button.ToolTip = _settings.Acc2Name + (IsMuted(View2) ? " (ミュート中)" : "");
        Acc2Label.Text = _settings.Acc2Name;
        View1.Visibility = _current == 1 && _viewReady[1] ? Visibility.Visible : Visibility.Collapsed;
        View2.Visibility = _current == 2 && _viewReady[2] ? Visibility.Visible : Visibility.Collapsed;
        Pill1.Visibility = _current == 1 ? Visibility.Visible : Visibility.Collapsed;
        Pill2.Visibility = _current == 2 ? Visibility.Visible : Visibility.Collapsed;
        Acc1Button.Background = _current == 1 ? SelectedBrush : NormalBrush;
        Acc2Button.Background = _current == 2 ? SelectedBrush : NormalBrush;
    }

    private void RenameAcc1_Click(object sender, RoutedEventArgs e) => RenameAcc(1);

    private void RenameAcc2_Click(object sender, RoutedEventArgs e) => RenameAcc(2);

    private void MuteAcc1_Click(object sender, RoutedEventArgs e) => ToggleMute(1);

    private void MuteAcc2_Click(object sender, RoutedEventArgs e) => ToggleMute(2);

    private void ToggleMute(int n)
    {
        try
        {
            var view = n == 1 ? View1 : View2;
            var c = view.CoreWebView2;
            if (c is null) return;
            c.IsMuted = !c.IsMuted;
            if (n == 1) _settings.Mute1 = c.IsMuted; else _settings.Mute2 = c.IsMuted;
            _settings.Save();
            Log.Write($"{AccName(n)} ミュート{(c.IsMuted ? "ON" : "OFF")}");
            RefreshRail();
        }
        catch { }
    }

    private static bool IsMuted(Microsoft.Web.WebView2.Wpf.WebView2 view)
    {
        try { return view.CoreWebView2?.IsMuted == true; }
        catch { return false; }
    }

    private void Acc_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender == Acc1Button) RenameAcc(1);
        else if (sender == Acc2Button) RenameAcc(2);
    }

    private void RenameAcc(int n)
    {
        var next = PromptName(AccName(n));
        if (next is null) return;
        if (n == 1) _settings.Acc1Name = next; else _settings.Acc2Name = next;
        _settings.Save();
        SaveNames();
        RefreshRail();
    }

    private string? PromptName(string current)
    {
        var dlg = new Window
        {
            Title = "名前の変更",
            Width = 300,
            Height = 160,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x31, 0x33, 0x38)),
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xDB, 0xDE, 0xE1)),
        };
        WindowIcon.Set(dlg);
        var panel = new StackPanel { Margin = new Thickness(12) };
        var box = new System.Windows.Controls.TextBox
        {
            Text = current,
            MaxLength = AppSettings.NameMaxLength,
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(8, 6, 8, 6),
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1E, 0x1F, 0x22)),
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xDB, 0xDE, 0xE1)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1E, 0x1F, 0x22)),
            CaretBrush = System.Windows.Media.Brushes.White,
        };
        var btns = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new System.Windows.Controls.Button { Content = "OK", Width = 75, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "キャンセル", Width = 75, IsCancel = true };
        try
        {
            var styles = new ResourceDictionary
            {
                Source = new Uri("Views/DiscordStyles.xaml", UriKind.Relative),
            };
            dlg.Resources.MergedDictionaries.Add(styles);
            ok.Style = (Style)dlg.FindResource("BtnPrimary");
            cancel.Style = (Style)dlg.FindResource("BtnSecondary");
        }
        catch { }
        ok.Click += (_, _) => dlg.DialogResult = true;
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = $"アカウント名({AppSettings.NameMaxLength}文字まで):" });
        panel.Children.Add(box);
        panel.Children.Add(btns);
        dlg.Content = panel;
        // ShowDialogの後のFocusは効かないため、表示直後(Loaded)に合わせる
        dlg.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        if (dlg.ShowDialog() != true) return null;
        var name = AppSettings.CleanName(box.Text); // 空白のみ・制御文字は弾く
        return name.Length > 0 ? name : null;
    }

    /// <summary>旧バージョン(と設定リセット時)の互換用に names.txt も更新しておく。</summary>
    private void SaveNames()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataFolder());
            File.WriteAllLines(Path.Combine(AppSettings.DataFolder(), AppConstants.LegacyNamesFileName), new[] { _settings.Acc1Name, _settings.Acc2Name });
        }
        catch { }
    }

    // ---- 再読み込み・設定 ----

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            (_current == 1 ? View1 : View2).CoreWebView2?.Reload();
        }
        catch { }
    }

}
