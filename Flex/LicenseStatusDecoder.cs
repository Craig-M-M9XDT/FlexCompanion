using System.Text;

namespace FlexCompanion.Flex;

/// <summary>
/// Decodes only observational licence status records. Does not send API commands,
/// create licence material, or infer disabled state from missing fields.
/// The radio remains authoritative for entitlement and command admission.
/// </summary>
public static class LicenseStatusDecoder
{
    public enum Kind { Summary, Feature, Subscription }

    public sealed record Record(
        Kind Type, string Name, bool? Enabled, string Reason, string Expiration,
        IReadOnlyDictionary<string, string> Fields);

    public static bool TryDecode(string body, out Record? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(body)) return false;
        var tokens = Tokenize(body);
        if (tokens.Count == 0 || !tokens[0].Equals("license", StringComparison.OrdinalIgnoreCase))
            return false;

        var type = Kind.Summary;
        var firstField = 1;
        if (tokens.Count > 1 && tokens[1].Equals("feature", StringComparison.OrdinalIgnoreCase))
        {
            type = Kind.Feature;
            firstField = 2;
        }
        else if (tokens.Count > 1 && tokens[1].Equals("subscription", StringComparison.OrdinalIgnoreCase))
        {
            type = Kind.Subscription;
            firstField = 2;
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens.Skip(firstField))
        {
            var idx = token.IndexOf('=');
            if (idx <= 0) continue;
            fields[token[..idx]] = token[(idx + 1)..].Replace('\u007f', ' ').Replace('\u00a0', ' ');
        }

        fields.TryGetValue("name", out var name);
        fields.TryGetValue("reason", out var reason);
        fields.TryGetValue("expiration", out var expiration);
        bool? enabled = null;
        if (fields.TryGetValue("enabled", out var state))
        {
            if (state.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                state.Equals("true", StringComparison.OrdinalIgnoreCase)) enabled = true;
            else if (state.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                     state.Equals("false", StringComparison.OrdinalIgnoreCase)) enabled = false;
        }

        result = new Record(type, name ?? "", enabled, reason ?? "", expiration ?? "", fields);
        return true;
    }

    static List<string> Tokenize(string source)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        bool quote = false;
        foreach (var c in source)
        {
            if (c == '"') { quote = !quote; continue; }
            if (char.IsWhiteSpace(c) && !quote)
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
            }
            else sb.Append(c);
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }
}
