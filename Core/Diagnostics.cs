using System.IO;
using System.Reflection;

namespace TwinCord;

/// <summary>
/// 各機能の連続失敗を数え、閾値を超えたら「ソフトが古くなった可能性」を通知する。
/// </summary>
public class Diagnostics
{
    private readonly object _lock = new();
    private readonly Dictionary<(Feature Feature, int Account), int> _failures = new();
    private readonly Dictionary<Feature, int> _thresholds = new()
    {
        [Feature.Badge] = 6,        // 5〜10秒間隔 x6 = 約30〜60秒応答なしで劣化判定
        [Feature.HotkeyGlobal] = 1, // 登録失敗は即劣化
        [Feature.HotkeyPage] = 1,   // 注入失敗は即劣化
        [Feature.Page] = 3,         // 死活3連続失敗で劣化判定
    };
    private readonly HashSet<(Feature Feature, int Account)> _notified = new();

    /// <summary>初めて閾値に到達したときに発火する(機能・アカウント単位でセッション中1回)。</summary>
    public event Action<Feature, int>? Degraded;

    /// <summary>劣化後、同じ機能・アカウントが成功して復旧したときに発火する。</summary>
    public event Action<Feature, int>? Recovered;

    public static string DisplayName(Feature key) => key switch
    {
        Feature.Badge => "未読バッジ",
        Feature.HotkeyGlobal => "グローバル切替キー(Ctrl+Alt+1/2/M)",
        Feature.HotkeyPage => "ページ内切替キー(Ctrl+1/2)",
        Feature.Page => "ページ死活監視",
        _ => key.ToString(),
    };

    /// <summary>アカウント単位の機能は、どのアカウントで起きたか分かる表示名にする。</summary>
    public static string DisplayName(Feature key, int account) =>
        account is 1 or 2 ? $"アカウント{account}の{DisplayName(key)}" : DisplayName(key);

    public static string AppVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        if (v is null) return "不明";
        // 4要素のバージョン(例: 1.11.2.1)はリビジョンまで表示する。
        // 3要素に丸めると更新比較で「最新なのに毎回更新通知」になるため。
        if (v.Revision > 0) return v.ToString(4);
        if (v.Build >= 0) return v.ToString(3);
        return v.ToString(2);
    }

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
    public static bool IsDevBuild()
    {
        if (IsDevBuildIn(ExePath())) return true;
        // `dotnet TwinCord.dll` 起動ではExePathがdotnet.exeになるため、
        // アプリの配置フォルダ側も確認する(開発中の自動更新を防ぐ)
        return IsDevBuildIn(Path.Combine(AppSettings.AppDir(), AppConstants.AppName + ".exe"));
    }

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

    public void ReportOk(Feature key, int account = 0)
    {
        bool recovered = false;
        lock (_lock)
        {
            var k = (key, account);
            if (_failures.TryGetValue(k, out var c))
            {
                var th = _thresholds.TryGetValue(key, out var t) ? t : 3;
                recovered = c >= th;
                _failures.Remove(k);
            }
            _notified.Remove(k);
        }
        if (recovered)
        {
            Log.Write($"{DisplayName(key, account)} が復旧");
            try { Recovered?.Invoke(key, account); } catch { }
        }
    }

    public void ReportFail(Feature key, int account = 0)
    {
        bool notify = false;
        lock (_lock)
        {
            var k = (key, account);
            _failures.TryGetValue(k, out var c);
            c++;
            _failures[k] = c;
            var th = _thresholds.TryGetValue(key, out var t) ? t : 3;
            if (c >= th && _notified.Add(k))
                notify = true;
        }
        if (notify)
        {
            Log.Write($"{DisplayName(key, account)} が劣化と判定");
            try { Degraded?.Invoke(key, account); } catch { }
        }
    }

    /// <summary>劣化中の機能(アカウントは問わない)。設定ボタンの警告表示などに使う。</summary>
    public IReadOnlyList<Feature> DegradedKeys()
    {
        lock (_lock)
        {
            return _failures
                .Where(kv => kv.Value >= (_thresholds.TryGetValue(kv.Key.Feature, out var t) ? t : 3))
                .Select(kv => kv.Key.Feature)
                .Distinct()
                .ToList();
        }
    }

    /// <summary>劣化中の機能とアカウントの組。自己診断の表示に使う。</summary>
    public IReadOnlyList<(Feature Feature, int Account)> DegradedDetails()
    {
        lock (_lock)
        {
            return _failures
                .Where(kv => kv.Value >= (_thresholds.TryGetValue(kv.Key.Feature, out var t) ? t : 3))
                .Select(kv => kv.Key)
                .ToList();
        }
    }
}
