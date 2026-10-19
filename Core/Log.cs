using System.IO;
using System.Text;

namespace TwinCord;

/// <summary>
/// 診断ログのファイル書き込み。呼び出し元を待たせないようキューに積み、
/// 専用スレッドが追記する。終了時はFlushで書き切る。
/// </summary>
public static class Log
{
    // WebView2イベント・タイマー・未処理例外など複数スレッドから呼ばれる。呼び出し元は
    // UIスレッドのことが多く、その場で同期I/Oすると操作が引っかかるため、enqueueだけして
    // 実際の書き込みは専用スレッドに任せる(通知が多い日でもUIを止めない)。
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> Pending = new();
    private static readonly AutoResetEvent PendingSignal = new(false);
    private static int _workerStarted;
    // Flushの判定用。キューが空になっても「取り出し済みで書き込み中」の1行が残るため、
    // 件数(投入/書き込み完了)で追いつきを判定する
    private static int _enqueued;
    private static int _written;

    // 書き込み(ローテーション含む)は単一スレッドからしか行わないが、
    // 将来の直接呼び出しに備えて直列化しておく。
    private static readonly object WriteLock = new();

    public static void Write(string msg)
    {
        Pending.Enqueue($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}");
        Interlocked.Increment(ref _enqueued);
        if (Interlocked.Exchange(ref _workerStarted, 1) == 0)
            new Thread(WorkerLoop) { IsBackground = true, Name = "TwinCordLog" }.Start();
        try { PendingSignal.Set(); } catch { }
    }

    /// <summary>書き込み待ちのログを吐き出す(終了時に呼ぶ)。</summary>
    public static void Flush(int timeoutMs = 2000)
    {
        // キューが空でも書き込み中の1行が残るため、書き込み済み件数が追いつくまで待つ
        var target = Volatile.Read(ref _enqueued);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _written) < target && sw.ElapsedMilliseconds < timeoutMs)
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
                Interlocked.Increment(ref _written);
            }
            catch { }
        }
    }

    private static void WriteLocked(string line)
    {
        lock (WriteLock)
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
