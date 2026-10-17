using System.IO;
using System.Reflection;
using System.Text;

namespace TwinCord;

/// <summary>
/// 各機能の連続失敗を数え、閾値を超えたら「ソフトが古くなった可能性」を通知する。
/// </summary>
public class Diagnostics
{
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _failures = new();
    private readonly Dictionary<string, int> _thresholds = new()
    {
        ["badge"] = 6,   // 5秒間隔 x6 = 約30秒応答なしで劣化判定
        ["hotkey"] = 1,  // 注入失敗は即劣化
        ["page"] = 3,    // 死活3連続失敗で劣化判定
    };
    private readonly HashSet<string> _notified = new();

    /// <summary>初めて閾値に到達したときに発火する(セッション中1回のみ)。</summary>
    public event Action<string>? Degraded;

    public static string DisplayName(string key) => key switch
    {
        "badge" => "未読バッジ",
        "hotkey" => "切替キー(Ctrl+1/2)",
        "page" => "ページ死活監視",
        _ => key,
    };

    public static string AppVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "不明";

    /// <summary>
    /// 実行中のexeのパス。単一ファイル発行でも実体のexeを返す
    /// (Assembly.Locationは単一ファイルでは空になるため使わない)。
    /// </summary>
    public static string ExePath()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            try { path = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName; }
            catch { path = null; }
        }
        return string.IsNullOrEmpty(path) ? "不明" : path;
    }

    public void ReportOk(string key)
    {
        lock (_lock) { _failures[key] = 0; }
    }

    public void ReportFail(string key)
    {
        bool notify = false;
        lock (_lock)
        {
            _failures.TryGetValue(key, out var c);
            c++;
            _failures[key] = c;
            var th = _thresholds.TryGetValue(key, out var t) ? t : 3;
            if (c >= th && _notified.Add(key))
                notify = true;
        }
        if (notify)
        {
            Log($"{DisplayName(key)} が劣化と判定");
            try { Degraded?.Invoke(key); } catch { }
        }
    }

    public IReadOnlyList<string> DegradedKeys()
    {
        lock (_lock)
        {
            return _failures
                .Where(kv => kv.Value >= (_thresholds.TryGetValue(kv.Key, out var t) ? t : 3))
                .Select(kv => kv.Key)
                .ToList();
        }
    }

    public static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataFolder());
            var dir = AppSettings.DataFolder();
            var path = Path.Combine(dir, "diagnostics.log");
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > 256 * 1024)
            {
                for (int i = 2; i >= 0; i--)
                {
                    var src = i == 0 ? path : Path.Combine(dir, $"diagnostics.{i}.log");
                    var dst = Path.Combine(dir, $"diagnostics.{i + 1}.log");
                    if (File.Exists(src))
                    {
                        if (File.Exists(dst)) File.Delete(dst);
                        File.Move(src, dst);
                    }
                }
            }
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                File.WriteAllText(path, "", new UTF8Encoding(true)); // 先頭にBOM
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}", new UTF8Encoding(false));
        }
        catch { }
    }
}
