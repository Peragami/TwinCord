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
        [Feature.Badge] = 6,        // 5〜10秒間隔 x6 = 約30〜60秒応答なしで劣化判定
        [Feature.HotkeyGlobal] = 1, // 登録失敗は即劣化
        [Feature.HotkeyPage] = 1,   // 注入失敗は即劣化
        [Feature.Page] = 3,         // 死活3連続失敗で劣化判定
    };
    private readonly HashSet<Feature> _notified = new();

    /// <summary>初めて閾値に到達したときに発火する(セッション中1回のみ)。</summary>
    public event Action<Feature>? Degraded;

    public static string DisplayName(Feature key) => key switch
    {
        Feature.Badge => "未読バッジ",
        Feature.HotkeyGlobal => "グローバル切替キー(Ctrl+Alt+1/2/M)",
        Feature.HotkeyPage => "ページ内切替キー(Ctrl+1/2)",
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

    /// <summary>
    /// binから起動している開発ビルドか(発行物には付かないruntimeconfig.dev.jsonの有無で判定)。
    /// 開発ビルドでは更新の自動適用と旧データの引き継ぎを行わない(作業中のデータを壊さないため)。
    /// </summary>
    public static bool IsDevBuild() => IsDevBuildIn(ExePath());

    internal static bool IsDevBuildIn(string? exePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(exePath ?? "");
            return !string.IsNullOrEmpty(dir)
                && File.Exists(Path.Combine(dir, AppConstants.AppName + ".runtimeconfig.dev.json"));
        }
        catch
        {
            return false;
        }
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

    // WebView2イベント・タイマー・未処理例外など複数スレッドから呼ばれる。呼び出し元は
    // UIスレッドのことが多く、その場で同期I/Oすると操作が引っかかるため、enqueueだけして
    // 実際の書き込みは専用スレッドに任せる(通知が多い日でもUIを止めない)。
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> Pending = new();
    private static readonly AutoResetEvent PendingSignal = new(false);
    private static int _workerStarted;

    // 書き込み(ローテーション含む)は単一スレッドからしか行わないが、
    // 将来の直接呼び出しに備えて直列化しておく。
    private static readonly object LogLock = new();

    public static void Log(string msg)
    {
        Pending.Enqueue($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}");
        if (Interlocked.Exchange(ref _workerStarted, 1) == 0)
            new Thread(WorkerLoop) { IsBackground = true, Name = "TwinCordLog" }.Start();
        try { PendingSignal.Set(); } catch { }
    }

    /// <summary>書き込み待ちのログを吐き出す(終了時に呼ぶ)。</summary>
    public static void Flush(int timeoutMs = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!Pending.IsEmpty && sw.ElapsedMilliseconds < timeoutMs)
            Thread.Sleep(20);
    }

    private static void WorkerLoop()
    {
        while (true)
        {
            try
            {
                if (!Pending.TryDequeue(out var line))
                {
                    // 通知が無ければ待つ(取りこぼしを防ぐため定期的にも起きる)
                    PendingSignal.WaitOne(250);
                    continue;
                }
                WriteLocked(line);
            }
            catch { }
        }
    }

    private static void WriteLocked(string line)
    {
        lock (LogLock)
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
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
