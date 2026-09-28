using System.Globalization;
using System.Text;

namespace FlexCompanion.Flex;

/// <summary>Helpers for the SmartSDR "key=value key=value" text protocol.</summary>
public static class Kv
{
    /// <summary>Splits on spaces, keeping "quoted strings" together (quotes are removed).</summary>
    public static List<string> Tokenize(string s)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        foreach (char c in s)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ' ' && !quoted)
            {
                if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    /// <summary>Turns tokens into a case-insensitive dictionary. 0x7F is the radio's encoding for a space.</summary>
    public static Dictionary<string, string> Parse(IEnumerable<string> tokens)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tokens)
        {
            int i = t.IndexOf('=');
            if (i > 0) d[t[..i]] = t[(i + 1)..].Replace('\u007f', ' ').Replace('\u00a0', ' ');
        }
        return d;
    }

    public static double? D(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static string F(double v, string format) => v.ToString(format, CultureInfo.InvariantCulture);
}

internal static class Ui
{
    /// <summary>Runs an action on the Avalonia UI thread (fire and forget).</summary>
    public static void Post(Action a)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(a);
    }
}
