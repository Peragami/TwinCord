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
    private readonly Dictionary<Feature, int> _failures = new();
    private readonly Dictionary<Feature, int> _thresholds = new()
    {
        [Feature.Badge] = 6,   // 5秒間隔 x6 = 約30秒応答なしで劣化判定
        [Feature.Hotkey] = 1,  // 注入失敗は即劣化
        [Feature.Page] = 3,    // 死活3連続失敗で劣化判定
    };
    private readonly HashSet<Feature> _notified = new();

    /// <summary>初めて閾値に到達したときに発火する(セッション中1回のみ)。</summary>
    public event Action<Feature>? Degraded;

    public static string DisplayName(Feature key) => key switch
    {
        Feature.Badge => "未読バッジ",
        Feature.Hotkey => "切替キー(Ctrl+1/2)",
        Feature.Page => "ページ死活監視",
        _ => key.ToString(),
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

    public void ReportOk(Feature key)
    {
        lock (_lock) { _failures[key] = 0; }
    }

    public void ReportFail(Feature key)
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

    public IReadOnlyList<Feature> DegradedKeys()
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
            var dir = AppSettings.DataFolder();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, AppConstants.LogBaseName + ".log");
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > 256 * 1024)
            {
                for (int i = 2; i >= 0; i--)
                {
                    var src = i == 0 ? path : Path.Combine(dir, $"{AppConstants.LogBaseName}.{i}.log");
                    var dst = Path.Combine(dir, $"{AppConstants.LogBaseName}.{i + 1}.log");
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
