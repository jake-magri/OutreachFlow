using System.Net;
using System.Net.Mail;
using System.Text.RegularExpressions;

namespace OutreachFlow.Services.Research;

/// <summary>
/// Extracts only email addresses explicitly printed on a public HTML page or in mailto links.
/// Does not follow mailto links, run scripts or attempt to infer personal addresses.
/// </summary>
public static class PublicContactExtractor
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);
    private static readonly Regex Hidden = new(
        @"<!--.*?-->|<(script|style|noscript|template)\b[^>]*>[\s\S]*?</\1>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline, Timeout);
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled | RegexOptions.Singleline, Timeout);
    private static readonly Regex Address = new(
        @"(?<![\w.+-])(?<email>[A-Z0-9][A-Z0-9._%+\-]{0,63}@[A-Z0-9](?:[A-Z0-9\-]{0,61}[A-Z0-9])?(?:\.[A-Z0-9](?:[A-Z0-9\-]{0,61}[A-Z0-9])?)+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, Timeout);
    private static readonly Regex Mailto = new(
        @"mailto:(?<address>[^\s'""<>?#]{1,254})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, Timeout);

    public static IReadOnlyList<ResearchContact> Extract(string html, Uri page, int limit = 10)
    {
        if (limit <= 0 || html.Length == 0) return [];
        var addresses = new Dictionary<string, ResearchContact>(StringComparer.OrdinalIgnoreCase);
        var visible = WebUtility.HtmlDecode(Tags.Replace(Hidden.Replace(html, " "), " "));
        foreach (Match m in Address.Matches(visible))
        {
            Add(m.Groups["email"].Value);
            if (addresses.Count >= limit) break;
        }

        if (addresses.Count < limit)
        {
            var withoutScripts = Hidden.Replace(html, " ");
            foreach (Match m in Mailto.Matches(withoutScripts))
            {
                string raw = WebUtility.HtmlDecode(m.Groups["address"].Value);
                try { raw = Uri.UnescapeDataString(raw); }
                catch (UriFormatException) { continue; }
                Add(raw);
                if (addresses.Count >= limit) break;
            }
        }
        return addresses.Values.ToArray();

        void Add(string raw)
        {
            raw = raw.Trim().TrimEnd('.', ',', ';', ':', ')');
            if (raw.Length > 254 || raw.Length < 6 || raw.Contains(' ') || !Address.IsMatch(raw)) return;
            try
            {
                var parsed = new MailAddress(raw);
                if (!string.Equals(parsed.Address, raw, StringComparison.OrdinalIgnoreCase)) return;
                var local = raw[..raw.IndexOf('@')];
                if (local.StartsWith("noreply", StringComparison.OrdinalIgnoreCase) ||
                    local.StartsWith("no-reply", StringComparison.OrdinalIgnoreCase) ||
                    local.StartsWith("donotreply", StringComparison.OrdinalIgnoreCase)) return;
                if (!addresses.ContainsKey(raw) && addresses.Count < limit)
                    addresses.Add(raw, new ResearchContact(raw, page.AbsoluteUri,
                        "Email address published on public HTML; verify the intended contact before use."));
            }
            catch (FormatException) { /* Ignore invalid addresses. */ }
        }
    }
}
