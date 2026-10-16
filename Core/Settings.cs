using System.IO;
using System.Text.Json;

namespace TwinCord;

public class AppSettings
{
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool UnreadBadge { get; set; } = true;
    public bool AutoReload { get; set; } = true;
    public bool CountUnreadChannels { get; set; }
    public bool StartMinimized { get; set; }
    public bool AutoUpdateCheck { get; set; } = true;
    public double Zoom1 { get; set; } = 1.0;
    public double Zoom2 { get; set; } = 1.0;
    public double WinLeft { get; set; } = double.NaN;
    public double WinTop { get; set; } = double.NaN;
    public double WinWidth { get; set; } = double.NaN;
    public double WinHeight { get; set; } = double.NaN;
    public bool WinMaximized { get; set; }

    public static string FilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TwinCord", "settings.json");

    public static string DataFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TwinCord");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath()))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath()));
                if (s is not null)
                {
                    s.Zoom1 = ClampZoom(s.Zoom1);
                    s.Zoom2 = ClampZoom(s.Zoom2);
                    return s;
                }
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataFolder());
            File.WriteAllText(FilePath(), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public AppSettings Clone() => new()
    {
        StartWithWindows = StartWithWindows,
        MinimizeToTray = MinimizeToTray,
        UnreadBadge = UnreadBadge,
        AutoReload = AutoReload,
        CountUnreadChannels = CountUnreadChannels,
        StartMinimized = StartMinimized,
        AutoUpdateCheck = AutoUpdateCheck,
        Zoom1 = Zoom1,
        Zoom2 = Zoom2,
        WinLeft = WinLeft,
        WinTop = WinTop,
        WinWidth = WinWidth,
        WinHeight = WinHeight,
        WinMaximized = WinMaximized,
    };

    public static double ClampZoom(double v) => v < 0.5 ? 0.5 : v > 2.0 ? 2.0 : v;
}
