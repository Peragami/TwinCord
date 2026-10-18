using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using TwinCord.Services;
using WinForms = System.Windows.Forms;

namespace TwinCord;

public partial class MainWindow : Window
{

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        // 二重に開いて後から保存した方だけが反映されるのを防ぐ
        if (_settingsWindow?.IsLoaded == true)
        {
            _settingsWindow.Activate();
            return;
        }
        var draft = _settings.Clone();
        var w = new SettingsWindow(draft) { Owner = IsVisible ? this : null };
        w.Diagnose = RunDiagnosticsAsync;
        w.CheckUpdate = CheckForUpdateAsync;
        w.ExportRequested = ExportBundle;
        w.ImportRequested = ImportBundle;
        w.TestNotifyRequested = SendTestNotification;
        _settingsWindow = w;
        try
        {
            if (w.ShowDialog() == true)
            {
                _settings = draft;
                _settings.Save();
                ApplySettings();
            }
        }
        finally
        {
            _settingsWindow = null;
        }
        UpdateGearAlert();
    }


    private string ExportBundle(AppSettings settings)
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "TwinCord設定(*.twincord.json)|*.twincord.json",
                FileName = "twincord-backup.twincord.json",
            };
            if (dlg.ShowDialog() != true) return "キャンセルしました。";
            var bundle = new
            {
                app = "TwinCord",
                version = 1,
                settings,
                names = new[] { settings.Acc1Name, settings.Acc2Name },
            };
            File.WriteAllText(dlg.FileName,
                JsonSerializer.Serialize(bundle, new JsonSerializerOptions { WriteIndented = true }));
            return "エクスポートしました: " + dlg.FileName;
        }
        catch (Exception ex)
        {
            return "失敗: " + ex.Message;
        }
    }

    private string ImportBundle()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "TwinCord設定(*.twincord.json)|*.twincord.json",
            };
            if (dlg.ShowDialog() != true) return "キャンセルしました。";
            using var doc = JsonDocument.Parse(File.ReadAllText(dlg.FileName));
            var r = doc.RootElement;
            if (!r.TryGetProperty("app", out var a) || a.GetString() != "TwinCord")
                return "TwinCordの設定ファイルではありません。";
            bool changed = false;
            if (r.TryGetProperty("settings", out var s))
            {
                var ns = JsonSerializer.Deserialize<AppSettings>(s.GetRawText());
                if (ns is not null)
                {
                    ns.Zoom1 = AppSettings.ClampZoom(ns.Zoom1);
                    ns.Zoom2 = AppSettings.ClampZoom(ns.Zoom2);
                    ns.Acc1Name = AppSettings.CleanName(ns.Acc1Name);
                    ns.Acc2Name = AppSettings.CleanName(ns.Acc2Name);
                    if (ns.Acc1Name.Length == 0) ns.Acc1Name = "Acc1";
                    if (ns.Acc2Name.Length == 0) ns.Acc2Name = "Acc2";
                    ns.NamesMigrated = true; // 旧names.txtを後から読み直さない
                    _settings = ns;
                    _settings.Save();
                    changed = true;
                }
            }
            // 旧形式のバンドルに含まれる名前も受け付ける
            if (r.TryGetProperty("names", out var nm) && nm.ValueKind == JsonValueKind.Array)
            {
                var arr = nm.EnumerateArray().Select(e => AppSettings.CleanName(e.GetString())).ToArray();
                if (arr.Length > 0 && arr[0].Length > 0) _settings.Acc1Name = arr[0];
                if (arr.Length > 1 && arr[1].Length > 0) _settings.Acc2Name = arr[1];
                _settings.NamesMigrated = true;
                _settings.Save();
                SaveNames();
                changed = true;
            }
            if (!changed) return "インポートできる設定がありませんでした。";
            // 開いている設定画面の編集用コピーも更新する(OKでインポート内容が巻き戻るのを防ぐ)
            _settingsWindow?.ReloadFrom(_settings);
            RefreshRail();
            ApplySettings();
            return "インポートしました(再起動不要)。";
        }
        catch (Exception ex)
        {
            return "失敗: " + ex.Message;
        }
    }

    private void ApplySettings()
    {
        ApplyRunKey(_settings.StartWithWindows);
        _tray.Ensure();
        SetZoom(View1, _settings.Zoom1);
        SetZoom(View2, _settings.Zoom2);
        if (!_settings.UnreadBadge)
        {
            Badge1.Visibility = Visibility.Collapsed;
            Badge2.Visibility = Visibility.Collapsed;
        }
    }

    private static void ApplyRunKey(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                AppConstants.RunKeyPath, true);
            if (key is null) return;
            var exe = Environment.ProcessPath ?? "";
            if (enable)
                key.SetValue(AppConstants.RunKeyName, "\"" + exe + "\"");
            else
                key.DeleteValue(AppConstants.RunKeyName, false);
        }
        catch { }
    }

}
