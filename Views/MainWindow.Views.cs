using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;

namespace TwinCord;

public partial class MainWindow : Window
{
    // 起動〜初回表示までの所要時間をログに残すための基準時刻
    private static readonly DateTime AppStartedUtc = DateTime.UtcNow;
    private static long ElapsedMs() => (long)(DateTime.UtcNow - AppStartedUtc).TotalMilliseconds;

    private readonly bool[] _viewReady = new bool[3];
    private readonly Task?[] _initTasks = new Task?[3];
    // 初回読み込み(ページ表示)が済んだか。済むまでメモリ抑制を掛けない。
    private readonly bool[] _viewContentLoaded = new bool[3];
    // 1枚目の初回表示完了。2枚目の初期化はこれ(またはタイムアウト)を待ってから始める。
    private readonly TaskCompletionSource _firstViewLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // 2アカウントで共有するWebView2環境(1つだけ作る)
    private CoreWebView2Environment? _sharedEnv;
    private Task<CoreWebView2Environment>? _sharedEnvTask;

    private static void SetZoom(WpfWebView2 view, double factor)
    {
        try
        {
            view.ZoomFactor = AppSettings.ClampZoom(factor);
        }
        catch { }
    }

    private Task EnsureViewAsync(int n)
    {
        if (_viewReady[n]) return Task.CompletedTask;
        return _initTasks[n] ??= InitViewAsync(n, ViewOf(n), ZoomOf(n)).ContinueWith(t =>
        {
            if (t.IsFaulted) { _initTasks[n] = null; throw t.Exception!.InnerException!; }
            _viewReady[n] = true;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private WpfWebView2 ViewOf(int n) => n == 1 ? View1 : View2;

    private double ZoomOf(int n) => n == 1 ? _settings.Zoom1 : _settings.Zoom2;

    private async Task InitViewAsync(int n, WpfWebView2 view, double zoom)
    {
        // 表示中(1枚目)の初回表示を優先するため、2枚目は1枚目の初回表示完了まで待つ。
        // 1枚目が失敗してもこの待ちはタイムアウトで解ける(常駐が止まらないように)。
        if (n == 2) await WaitForFirstViewLoadedAsync();
        var sw = Stopwatch.StartNew();
        // 1つの環境(ブラウザ/GPUプロセスを共有)に、アカウント別プロファイルを載せる。
        // クッキー/localStorageはプロファイルごとに分離されるため、ログインも通知も両方そのまま動く。
        var env = await GetSharedEnvironmentAsync();
        var envMs = sw.ElapsedMilliseconds;
        var options = env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = ProfileName(n);
        await view.EnsureCoreWebView2Async(env, options);
        var ctlMs = sw.ElapsedMilliseconds;
        await ConfigureViewAsync(n, view, zoom);
        Log.Write($"acc{n} 初期化: 環境={envMs}ms コントローラ={ctlMs - envMs}ms 設定={sw.ElapsedMilliseconds - ctlMs}ms 累計={sw.ElapsedMilliseconds}ms");
    }

    /// <summary>1枚目の初回表示完了を待つ。上限を超えたら諦めて2枚目を始める。</summary>
    private async Task WaitForFirstViewLoadedAsync()
    {
        var t = _firstViewLoaded.Task;
        if (t.IsCompleted) return;
        if (await Task.WhenAny(t, Task.Delay(AppConstants.SecondViewWait)) != t)
            Log.Write($"acc1の初回表示待ちがタイムアウト({AppConstants.SecondViewWait.TotalSeconds}秒)したためacc2を開始");
    }

    /// <summary>1枚目の初回読み込みが失敗したときに、2枚目の開始条件を解く(待ち続けない)。</summary>
    private void FirstViewLoadFailed()
    {
        if (_firstViewLoaded.Task.IsCompleted) return;
        Log.Write("acc1の初回読み込みに失敗したためacc2を開始");
        _firstViewLoaded.TrySetResult();
    }

    private static string EnsureDataFolder(string name)
    {
        var dir = Path.Combine(AppSettings.DataFolder(), name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>共有環境は1つだけ作る(2つのViewから呼ばれても二重生成しない)。</summary>
    private async Task<CoreWebView2Environment> GetSharedEnvironmentAsync()
    {
        if (_sharedEnvTask is null) _sharedEnvTask = CreateSharedEnvironmentAsync();
        try
        {
            return await _sharedEnvTask;
        }
        catch
        {
            // 失敗したタスクを残すと再試行しても同じ失敗を返すため、作り直せるようにする
            _sharedEnvTask = null;
            throw;
        }
    }

    private async Task<CoreWebView2Environment> CreateSharedEnvironmentAsync()
    {
        var folder = EnsureDataFolder(AppConstants.WebViewDataFolder);
        // 初回(プロファイル新規)はキャッシュが無く読み込みが遅い。原因切り分け用に記録する。
        bool cold = !Directory.Exists(Path.Combine(folder, "EBWebView"));
        var sw = Stopwatch.StartNew();
        var env = await CoreWebView2Environment.CreateAsync(null, folder);
        Log.Write($"共有環境を作成: {sw.ElapsedMilliseconds}ms{(cold ? "(初回/キャッシュ無し)" : "")}");
        _sharedEnv = env;
        return env;
    }

    private static string ProfileName(int n) => "acc" + n;

    /// <summary>
    /// Discordの初回読み込み完了時に1度だけ呼ばれる。メモリ抑制の適用開始と、
    /// 2枚目の開始条件(1枚目の完了)の通知を行う。
    /// </summary>
    private void OnViewContentLoaded(int n, WpfWebView2 view)
    {
        if (_viewContentLoaded[n]) return;
        // 起動直後のabout:blank等を「読み込み完了」と数えない(メモリ抑制を早く掛けないため)
        string source;
        try { source = view.CoreWebView2?.Source ?? ""; }
        catch { source = ""; }
        if (!Discord.IsDiscordUri(source)) return;

        _viewContentLoaded[n] = true;
        Log.Write($"acc{n} 初回表示完了: 起動から{ElapsedMs()}ms ({source})");
        _watchdog?.SetViewLoaded(n);
        if (n == 1) _firstViewLoaded.TrySetResult();
    }
}
