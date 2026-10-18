using System.Windows;
using System.Windows.Input;

namespace TwinCord;

public partial class HistoryWindow : Window
{
    private readonly List<NotificationRecord> _items;
    private readonly Action<int> _jump;

    public HistoryWindow(List<NotificationRecord> items, Action<int> jump)
    {
        InitializeComponent();
        WindowIcon.Set(this);
        _items = items;
        _jump = jump;
        Loaded += HistoryWindow_Loaded;
    }

    private void HistoryWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0)
        {
            HistoryList.Items.Add("通知はまだありません。");
            return;
        }
        foreach (var it in _items)
        {
            // 古い履歴ファイルのnull要素・null文字列でも落ちないようにする
            if (it is null)
            {
                HistoryList.Items.Add("(不明な通知)");
                continue;
            }
            var stamp = it.Time.Date == DateTime.Today
                ? it.Time.ToString("HH:mm")
                : it.Time.Year == DateTime.Today.Year
                    ? it.Time.ToString("MM/dd HH:mm")
                    : it.Time.ToString("yyyy/MM/dd HH:mm");
            var name = string.IsNullOrWhiteSpace(it.AccountName) ? $"Acc{it.Account}" : it.AccountName;
            HistoryList.Items.Add($"{stamp} [{name}] {(it.Text ?? "").ReplaceLineEndings(" ")}");
        }
    }

    private void HistoryList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var i = HistoryList.SelectedIndex;
        if (i < 0 || i >= _items.Count) return;
        _jump(_items[i].Account);
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
