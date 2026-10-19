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
    public string Acc1Name { get; set; } = "Acc1";
    public string Acc2Name { get; set; } = "Acc2";
    /// <summary>旧names.txtからの移行が済んだか(移行は一度だけ行う)。</summary>
    public bool NamesMigrated { get; set; }

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
    /// 実行中のexeがあるフォルダ。単一ファイル発行でもEnvironment.ProcessPathは実体のexeを返す
    /// (Assembly.Locationは空になるため使わない)。
    /// </summary>
    public static string AppDir()
    {
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
        catch (Exception ex)
        {
            // 破損していた場合は既定値で起動する(次の保存で上書きされる前に退避してログに残す)
            Diagnostics.Log("設定の読み込みに失敗したため既定値で起動: " + ex.Message);
            BackupCorruptFile(path);
            s = new AppSettings();
        }
        // 形式版が無い/不正なファイルは現行版として扱う(将来、版ごとの移行処理をここで分岐する)
        if (s.SettingsVersion <= 0) s.SettingsVersion = CurrentSettingsVersion;
        s.Zoom1 = ClampZoom(s.Zoom1);
        s.Zoom2 = ClampZoom(s.Zoom2);
        // 手書き/インポートされた設定でも表示が崩れないよう、名前を正規化しておく
        s.Acc1Name = CleanNameOr(s.Acc1Name, "Acc1");
        s.Acc2Name = CleanNameOr(s.Acc2Name, "Acc2");
        MigrateNames(s);
        return s;
    }

    /// <summary>壊れた設定ファイルを settings.json.bak へ退避する(中身を確認できるように残す)。</summary>
    private static void BackupCorruptFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Move(path, path + ".bak", true);
        }
        catch { }
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
        return s.Length > NameMaxLength ? s[..NameMaxLength] : s;
    }

    private static string CleanNameOr(string? raw, string fallback)
    {
        var s = CleanName(raw);
        return s.Length > 0 ? s : fallback;
    }

    /// <summary>旧バージョンのnames.txtを一度だけ取り込む(次回保存でsettings.json側に確定する)。</summary>
    private static void MigrateNames(AppSettings s)
    {
        if (s.NamesMigrated) return;
        try
        {
            var file = Path.Combine(DataFolder(), AppConstants.LegacyNamesFileName);
            if (File.Exists(file))
            {
                var lines = File.ReadAllLines(file);
                if (lines.Length > 0 && lines[0].Trim().Length > 0) s.Acc1Name = CleanNameOr(lines[0], s.Acc1Name);
                if (lines.Length > 1 && lines[1].Trim().Length > 0) s.Acc2Name = CleanNameOr(lines[1], s.Acc2Name);
            }
        }
        catch { }
        s.NamesMigrated = true;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataFolder());
            var path = FilePath();
            var tmp = path + ".tmp";
            // 直接上書きすると途中で落ちたときに設定が壊れるため、一時ファイル経由で置換する
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions), new UTF8Encoding(false));
            File.Move(tmp, path, true);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("設定の保存に失敗: " + ex.Message);
            try { File.Delete(FilePath() + ".tmp"); } catch { }
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
