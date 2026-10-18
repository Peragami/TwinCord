using System.IO;

namespace TwinCord.Services;

/// <summary>
/// 通知履歴の保持(最新30件)と永続化。
/// </summary>
public sealed class HistoryStore
{
    private readonly List<NotificationRecord> _items = new();
    private const int MaxItems = 30;

    public IReadOnlyList<NotificationRecord> Items => _items;

    public void Add(NotificationRecord item)
    {
        _items.Insert(0, item);
        while (_items.Count > MaxItems) _items.RemoveAt(_items.Count - 1);
        Save();
    }

    public void Load()
    {
        try
        {
            var p = FilePath();
            if (!File.Exists(p)) return;
            var list = System.Text.Json.JsonSerializer.Deserialize<List<NotificationRecord>>(File.ReadAllText(p));
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
            File.WriteAllText(FilePath(), System.Text.Json.JsonSerializer.Serialize(_items.Take(MaxItems).ToList()));
        }
        catch { }
    }

    private static string FilePath() => System.IO.Path.Combine(
        AppSettings.DataFolder(), AppConstants.HistoryFileName);
}
