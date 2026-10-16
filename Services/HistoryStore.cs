using System.IO;

namespace TwinCord.Services;

/// <summary>
/// 通知履歴の保持(最新30件)と永続化。
/// </summary>
public sealed class HistoryStore
{
    private readonly List<HistoryWindow.Item> _items = new();
    private const int MaxItems = 30;

    public IReadOnlyList<HistoryWindow.Item> Items => _items;

    public void Add(HistoryWindow.Item item)
    {
        _items.Insert(0, item);
        while (_items.Count > MaxItems) _items.RemoveAt(_items.Count - 1);
        Save();
    }

    public void Load()
    {
        try
        {
            var p = Path();
            if (!File.Exists(p)) return;
            var list = System.Text.Json.JsonSerializer.Deserialize<List<HistoryWindow.Item>>(File.ReadAllText(p));
            if (list is null) return;
            _items.AddRange(list.Take(MaxItems));
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppSettings.DataFolder());
            File.WriteAllText(Path(), System.Text.Json.JsonSerializer.Serialize(_items.Take(MaxItems).ToList()));
        }
        catch { }
    }

    private static string Path() => System.IO.Path.Combine(AppSettings.DataFolder(), "notifications.json");
}
