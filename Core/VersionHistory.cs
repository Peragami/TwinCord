using System.IO;
using System.Text;

namespace TwinCord;

/// <summary>
/// アプリ内に表示する「変更履歴」。README.md の「バージョン履歴」節をそのまま使う。
/// 履歴は README にだけ書けばよい(設定画面側と二重管理しないため、埋め込みリソースから読む)。
/// </summary>
public static class VersionHistory
{
    private const string ResourceName = "TwinCord.README.md";
    private const string Heading = "## バージョン履歴";
    private static string? _cached;

    public static string Text => _cached ??= Extract();

    private static string Extract()
    {
        try
        {
            using var stream = typeof(VersionHistory).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null) return "";
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var sb = new StringBuilder();
            var inSection = false;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var trimmed = line.Trim();
                if (!inSection)
                {
                    if (trimmed.StartsWith(Heading, StringComparison.Ordinal)) inSection = true;
                    continue;
                }
                if (trimmed.StartsWith("## ", StringComparison.Ordinal)) break; // 次の見出しまで
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith("- ", StringComparison.Ordinal)) trimmed = trimmed[2..].Trim();
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(trimmed);
            }
            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }
}
