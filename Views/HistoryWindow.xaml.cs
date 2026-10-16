using System.Windows;
using System.Windows.Input;

namespace TwinCord;

public partial class HistoryWindow : Window
{
    public record Item(DateTime Time, int Account, string AccountName, string Text);

    private readonly List<Item> _items;
    private readonly Action<int> _jump;

    public HistoryWindow(List<Item> items, Action<int> jump)
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
            HistoryList.Items.Add($"{it.Time:HH:mm} [{it.AccountName}] {it.Text.ReplaceLineEndings(" ")}");
    }

    private void HistoryList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var i = HistoryList.SelectedIndex;
        if (i < 0 || i >= _items.Count) return;
        _jump(_items[i].Account);
        Close();
    }
}
