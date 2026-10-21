using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwinCord;

public class AppSettings
{
    /// <summary>設定ファイルの形式版。形式を変えたときの移行判定に使う。</summary>
    public int SettingsVersion { get; set; } = CurrentSettingsVersion;

    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool UnreadBadge { get; set; } = true;
    public bool AutoReload { get; set; } = true;
    public bool CountUnreadChannels { get; set; }
    public bool StartMinimized { get; set; }
    public bool AutoUpdateCheck { get; set; } = true;
    public bool NotifyEnabled { get; set; } = true;
    public bool SkipViewedAccount { get; set; } = true;
    public bool NotifyHiddenOnly { get; set; }
    public bool HideDownloadButton { get; set; } = true;
    /// <summary>アカウント別ミュート。再起動後も維持する。</summary>
    public bool Mute1 { get; set; }
    public bool Mute2 { get; set; }
    public string Acc1Name { get; set; } = AppConstants.DefaultAccountName(1);
    public string Acc2Name { get; set; } = AppConstants.DefaultAccountName(2);
    /// <summary>旧names.txtからの移行が済んだか(移行は一度だけ行う)。</summary>
    public bool NamesMigrated { get; set; }

    /// <summary>
    /// 読み込み時にIO/権限エラーなどで内容を確認できなかったか。
    /// 次回保存時に元ファイルを退避してから上書きするため、JSONには保存しない。
    /// </summary>
    [JsonIgnore]
    internal bool LoadFailed { get; set; }

    /// <summary>アカウント名の最大文字数(表示崩れ防止)。</summary>
    public const int NameMaxLength = 24;

    /// <summary>現在の設定形式の版。上げるときはLoadFromへ移行処理を足す。</summary>
    public const int CurrentSettingsVersion = 1;

    public double Zoom1 { get; set; } = 1.0;
    public double Zoom2 { get; set; } = 1.0;
    // 未保存はnull。NaNはJSONに書けず保存自体が例外になるため使わない
    public double? WinLeft { get; set; }
    public double? WinTop { get; set; }
    public double? WinWidth { get; set; }
    public double? WinHeight { get; set; }
    public bool WinMaximized { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string FilePath() => Path.Combine(
        DataFolder(), AppConstants.SettingsFileName);

    /// <summary>
    /// アプリの配置フォルダ。単一ファイル発行でもAppContext.BaseDirectoryは実体のexeフォルダを返す。
    /// </summary>
    public static string AppDir()
    {
        // データはアプリの配置フォルダ基準にする。Environment.ProcessPath は
        // `dotnet TwinCord.dll` 起動時に dotnet.exe を指してしまうため使わない。
        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir))
            return Path.TrimEndingDirectorySeparator(baseDir);
        var exe = Environment.ProcessPath;
        var dir = string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe);
        return string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : dir;
    }

    /// <summary>
    /// データ(設定・ログ・通知履歴・ログイン情報)の保存先。exeと同じフォルダ配下のdataに置く
    /// ポータブル形式で、フォルダごと削除すればアプリとデータが完全に消える。
    /// </summary>
    public static string DataFolder() => Path.Combine(AppDir(), AppConstants.DataFolderName);

    /// <summary>
    /// データフォルダを用意して書き込めるか確かめる。Program Filesなど書き込めない場所に
    /// 置かれた場合はエラーメッセージを返す(起動前に知らせるため、起動処理の最初に呼ぶ)。
    /// </summary>
    public static string? PrepareDataFolder()
    {
        try
        {
            var dir = DataFolder();
            Directory.CreateDirectory(dir);
            // 実際に書けるか確かめる(権限・読み取り専用・容量不足などを早期に検出)
            var probe = Path.Combine(dir, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public static AppSettings Load() => LoadFrom(FilePath());

    /// <summary>指定したファイルから設定を読む(テストから一時ファイルで検証できるように分離)。</summary>
    internal static AppSettings LoadFrom(string path)
    {
        AppSettings s = new();
        try
        {
            // 存在確認と読み込みを分けると、その間に消された場合に例外が出るため一度で読む
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
            if (loaded is not null) s = loaded;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // JSONとして壊れている場合だけ退避する。退避に失敗した場合は、
            // 次回保存時に上書きする前にもう一度退避を試みる。
            Log.Write("設定の読み込みに失敗したため既定値で起動: " + ex.Message);
            var backedUp = BackupCorruptFile(path);
            s = new AppSettings { LoadFailed = !backedUp };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 一時的な共有違反・権限エラーで、正常な設定ファイルを退避・上書きしない
            Log.Write("設定を読み込めなかったため既定値で起動(ファイルは保持): " + ex.Message);
            s = new AppSettings { LoadFailed = true };
        }
        catch (Exception ex)
        {
            Log.Write("設定の読み込みに失敗したため既定値で起動(ファイルは保持): " + ex.Message);
            s = new AppSettings { LoadFailed = true };
        }
        // 形式版が無い/不正なファイルは現行版として扱う(将来、版ごとの移行処理をここで分岐する)
        if (s.SettingsVersion <= 0) s.SettingsVersion = CurrentSettingsVersion;
        s.NormalizeValues();
        var folder = Path.GetDirectoryName(path);
        MigrateNames(s, string.IsNullOrEmpty(folder) ? DataFolder() : folder);
        return s;
    }

    /// <summary>
    /// 読み込み・インポート共通の値の正規化(手書き・外部ファイルでも表示が崩れないようにする)。
    /// </summary>
    internal void NormalizeValues()
    {
        Zoom1 = ClampZoom(Zoom1);
        Zoom2 = ClampZoom(Zoom2);
        Acc1Name = CleanNameOr(Acc1Name, AppConstants.DefaultAccountName(1));
        Acc2Name = CleanNameOr(Acc2Name, AppConstants.DefaultAccountName(2));
    }

    /// <summary>
    /// 壊れた設定ファイルを settings.json.bak へ退避する。既に .bak がある場合は
    /// 上書きせず、日時付きの名前で残す(復旧用のバックアップを失わないため)。
    /// </summary>
    private static bool BackupCorruptFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;
            var backup = NextBackupPath(path);
            File.Move(path, backup);
            Log.Write("壊れた設定を退避: " + backup);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("壊れた設定の退避に失敗: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 既存の .bak を上書きしないバックアップ名を返す(日時付きで衝突を避ける)。
    /// </summary>
    private static string NextBackupPath(string path)
    {
        var backup = path + ".bak";
        if (!File.Exists(backup)) return backup;

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        backup = path + ".bak." + stamp;
        for (int i = 2; File.Exists(backup); i++)
            backup = path + ".bak." + stamp + "-" + i;
        return backup;
    }

    /// <summary>
    /// 読み込めなかった設定を保存で上書きする直前に、元ファイルをコピーして残す。
    /// コピーできない場合は、内容を失わないよう保存自体を中止する。
    /// </summary>
    private bool BackupBeforeOverwrite(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;
            var backup = NextBackupPath(path);
            File.Copy(path, backup, false);
            Log.Write("設定の保存前に元ファイルを退避: " + backup);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("設定の保存前の退避に失敗したため保存を中止: " + ex.Message);
            return false;
        }
    }

    /// <summary>制御文字(改行等)を除去して前後の空白を落とし、最大長で切り詰める。</summary>
    public static string CleanName(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (!char.IsControl(ch)) sb.Append(ch);
        }
        var s = sb.ToString().Trim();
        if (s.Length <= NameMaxLength) return s;
        var cut = NameMaxLength;
        // 絵文字などのサロゲートペアを途中で切らない
        if (cut > 0 && char.IsHighSurrogate(s[cut - 1])) cut--;
        return s[..cut];
    }

    private static string CleanNameOr(string? raw, string fallback)
    {
        var s = CleanName(raw);
        return s.Length > 0 ? s : fallback;
    }

    /// <summary>旧バージョンのnames.txtを一度だけ取り込む(次回保存でsettings.json側に確定する)。</summary>
    private static void MigrateNames(AppSettings s, string folder)
    {
        if (s.NamesMigrated) return;
        try
        {
            var file = Path.Combine(folder, AppConstants.LegacyNamesFileName);
            var lines = File.ReadAllLines(file);
            if (lines.Length > 0 && lines[0].Trim().Length > 0) s.Acc1Name = CleanNameOr(lines[0], s.Acc1Name);
            if (lines.Length > 1 && lines[1].Trim().Length > 0) s.Acc2Name = CleanNameOr(lines[1], s.Acc2Name);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch
        {
            // 一時的なロック・権限エラー等で読めなかった場合は「移行済み」にしない。
            // 次回起動で再試行し、空の名前で上書きしないようにする。
            // (File.Existsは権限エラーもfalseにするため、存在確認には使わない)
            return;
        }
        s.NamesMigrated = true;
    }

    public void Save() => SaveTo(FilePath());

    /// <summary>保存先を指定して書き込む(テストから退避処理を検証できるように分離)。</summary>
    internal void SaveTo(string path)
    {
        try
        {
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            // 読み込み時に内容を確認できなかった場合、元ファイルを消さずに退避してから保存する
            if (LoadFailed && !BackupBeforeOverwrite(path)) return;
            var tmp = path + ".tmp";
            // 直接上書きすると途中で落ちたときに設定が壊れるため、一時ファイル経由で置換する
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions), new UTF8Encoding(false));
            File.Move(tmp, path, true);
            LoadFailed = false;
        }
        catch (Exception ex)
        {
            Log.Write("設定の保存に失敗: " + ex.Message);
            try { File.Delete(path + ".tmp"); } catch { }
        }
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>別インスタンスの内容を丸ごとコピーする(インポート後に設定画面の編集用コピーを同期するため)。</summary>
    public void CopyFrom(AppSettings other)
    {
        StartWithWindows = other.StartWithWindows;
        MinimizeToTray = other.MinimizeToTray;
        UnreadBadge = other.UnreadBadge;
        AutoReload = other.AutoReload;
        CountUnreadChannels = other.CountUnreadChannels;
        StartMinimized = other.StartMinimized;
        AutoUpdateCheck = other.AutoUpdateCheck;
        NotifyEnabled = other.NotifyEnabled;
        SkipViewedAccount = other.SkipViewedAccount;
        NotifyHiddenOnly = other.NotifyHiddenOnly;
        HideDownloadButton = other.HideDownloadButton;
        Mute1 = other.Mute1;
        Mute2 = other.Mute2;
        Acc1Name = other.Acc1Name;
        Acc2Name = other.Acc2Name;
        NamesMigrated = other.NamesMigrated;
        SettingsVersion = other.SettingsVersion;
        Zoom1 = other.Zoom1;
        Zoom2 = other.Zoom2;
        WinLeft = other.WinLeft;
        WinTop = other.WinTop;
        WinWidth = other.WinWidth;
        WinHeight = other.WinHeight;
        WinMaximized = other.WinMaximized;
    }

    public static double ClampZoom(double v) =>
        double.IsNaN(v) || double.IsInfinity(v) ? 1.0
        : v < 0.5 ? 0.5
        : v > 2.0 ? 2.0
        : v;
}
