namespace TwinCord;

/// <summary>
/// アプリ全体で共有する固定値。文字列の散在によるタイポを防ぐため、
/// 複数箇所で使う名前・タイムアウトだけをここに置く。
/// </summary>
public static class AppConstants
{
    public const string AppName = "TwinCord";

    // 単一起動
    public const string SingleInstanceMutex = "TwinCord_SingleInstance";
    public const string ShowWindowEvent = "TwinCord_ShowWindow";

    // 自動起動のレジストリ
    public const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    public const string RunKeyName = "TwinCord";

    // データフォルダ内の名前
    public const string SettingsFileName = "settings.json";
    public const string LegacyNamesFileName = "names.txt";
    public const string HistoryFileName = "notifications.json";
    public const string LogBaseName = "diagnostics";
    public const string WebViewDataFolder = "webview2";
    public const string AvatarFolder = "avatars";

    // 通信・スクリプト実行
    public static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(15);
    public const int EvalTimeoutMs = 10000;
    public const int DiagnosticsEvalTimeoutMs = 8000;

    // アバターとして受け入れる最大サイズ(過大なレスポンスでメモリを消費しない)
    public const int AvatarMaxBytes = 2 * 1024 * 1024;
}
