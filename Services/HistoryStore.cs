using System.IO;
using System.Text.Json;

namespace TwinCord.Services;

/// <summary>
/// 通知履歴の保持(最新30件)と永続化。
/// </summary>
public sealed class HistoryStore
{
    private readonly List<NotificationRecord> _items = new();
    private const int MaxItems = 30;
    private readonly string _folder;

    /// <summary>folderを省略すると通常のデータフォルダを使う(テストでは一時フォルダを渡せる)。</summary>
    public HistoryStore(string? folder = null) => _folder = folder ?? AppSettings.DataFolder();

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
            // 旧バージョンや手編集でnull要素・null文字列が入っていても表示側で落ちないようにする
            var list = JsonSerializer.Deserialize<List<NotificationRecord?>>(File.ReadAllText(p));
            if (list is null) return;
            _items.Clear();
            foreach (var item in list)
            {
                if (item is null) continue;
                _items.Add(item with
                {
                    AccountName = string.IsNullOrWhiteSpace(item.AccountName)
                        ? $"Acc{item.Account}"
                        : item.AccountName,
                    Text = item.Text ?? "",
                });
                if (_items.Count >= MaxItems) break;
            }
        }
        catch { }
    }

    private void Save()
    {
        var path = FilePath();
        var tmp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(_folder);
            // 直接上書きせず、書けたら置換する(途中で落ちても履歴が壊れない)
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items.Take(MaxItems).ToList()));
            File.Move(tmp, path, true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private string FilePath() => Path.Combine(
        _folder, AppConstants.HistoryFileName);
}
