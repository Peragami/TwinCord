using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace TwinCord.Services;

/// <summary>
/// 通知履歴の保持(最新30件)と永続化。
/// 保存は専用のバックグラウンド処理にまとめて行う(通知のたびにUIスレッドで
/// ファイルI/Oをすると操作が引っかかるため)。書き込みの順序は1本の処理で保証し、
/// 連続追加は最新の内容1回にまとめる。
/// </summary>
public sealed class HistoryStore
{
    private readonly object _gate = new();
    private readonly List<NotificationRecord> _items = new();
    private const int MaxItems = 30;
    private readonly string _folder;
    private bool _dirty;
    private bool _writerRunning;

    /// <summary>folderを省略すると通常のデータフォルダを使う(テストでは一時フォルダを渡せる)。</summary>
    public HistoryStore(string? folder = null) => _folder = folder ?? AppSettings.DataFolder();

    /// <summary>現在の履歴(コピー)。呼び出し側で自由に使える。</summary>
    public IReadOnlyList<NotificationRecord> Items
    {
        get
        {
            lock (_gate) return new List<NotificationRecord>(_items);
        }
    }

    public void Add(NotificationRecord item)
    {
        // 表示側がアカウント番号でビューを選ぶため、1/2以外は保存しない
        if (item.Account is not (1 or 2)) return;
        lock (_gate)
        {
            _items.Insert(0, item);
            while (_items.Count > MaxItems) _items.RemoveAt(_items.Count - 1);
        }
        RequestSave();
    }

    public void Load()
    {
        List<NotificationRecord> loaded = new();
        try
        {
            var p = FilePath();
            if (!File.Exists(p)) return;
            // 旧バージョンや手編集でnull要素・null文字列が入っていても表示側で落ちないようにする
            var list = JsonSerializer.Deserialize<List<NotificationRecord?>>(File.ReadAllText(p));
            if (list is null) return;
            foreach (var item in list)
            {
                if (item is null) continue;
                // 手編集・破損で1/2以外が入っていても、履歴のダブルクリックで
                // 存在しないビューを開かないようにする
                if (item.Account is not (1 or 2)) continue;
                loaded.Add(item with
                {
                    AccountName = string.IsNullOrWhiteSpace(item.AccountName)
                        ? $"Acc{item.Account}"
                        : item.AccountName,
                    Text = item.Text ?? "",
                });
                if (loaded.Count >= MaxItems) break;
            }
        }
        catch
        {
            return; // 壊れたファイルでは現在の内容を維持する
        }
        lock (_gate)
        {
            _items.Clear();
            _items.AddRange(loaded);
        }
    }

    /// <summary>書き込み待ちの分が終わるまで待つ(終了時に呼ぶ)。</summary>
    public void Flush(int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            lock (_gate)
            {
                if (!_dirty && !_writerRunning) return;
            }
            if (sw.ElapsedMilliseconds >= timeoutMs) return;
            Thread.Sleep(20);
        }
    }

    private void RequestSave()
    {
        lock (_gate)
        {
            _dirty = true;
            if (_writerRunning) return; // 動いている書き込みが最新の内容を拾う
            _writerRunning = true;
        }
        _ = Task.Run(WriteLoop);
    }

    private void WriteLoop()
    {
        while (true)
        {
            List<NotificationRecord> snapshot;
            lock (_gate)
            {
                _dirty = false;
                snapshot = new List<NotificationRecord>(_items);
            }
            SaveSnapshot(snapshot);
            lock (_gate)
            {
                // 書いている間に追加されていれば、最新の内容でもう一度書く
                if (!_dirty)
                {
                    _writerRunning = false;
                    return;
                }
            }
        }
    }

    private void SaveSnapshot(List<NotificationRecord> items)
    {
        var path = FilePath();
        var tmp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(_folder);
            // 直接上書きせず、書けたら置換する(途中で落ちても履歴が壊れない)
            File.WriteAllText(tmp, JsonSerializer.Serialize(items.Take(MaxItems).ToList()));
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
