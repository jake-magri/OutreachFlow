using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace OutreachFlow.Services.Research;

/// <summary>
/// Read-only, deliberately narrow HTML discovery of the submitted company website.
/// No external search engine, PDF parsing, browser scripts, form submission or email.
/// </summary>
public sealed class OfficialSiteResearcher : ICompanyResearcher, IDisposable
{
    private const int MaxPages = 6;
    private const int MaxBytes = 300_000;
    private readonly HttpClient client;

    public OfficialSiteResearcher()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            MaxConnectionsPerServer = 2,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
                // Fail on mixed public/private DNS answers, rather than just choosing a convenient one.
                if (addresses.Length == 0 || addresses.Any(a => !ResearchUrlPolicy.IsPublicAddress(a)))
                    throw new InvalidOperationException("Destination DNS includes an unsafe or non-public address; research stopped.");
                Exception? lastError = null;
                foreach (var address in addresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex) when (ex is SocketException or IOException)
                    {
                        lastError = ex;
                        socket.Dispose();
                    }
                    catch { socket.Dispose(); throw; }
                }
                throw new HttpRequestException("Could not reach public destination.", lastError);
            }
        };
        client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OutreachFlowResearchBot/0.1 (local read-only vendor research)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
    }

    public async Task<ResearchScan> SearchAsync(string website, CancellationToken cancellationToken = default)
    {
        var root = ResearchUrlPolicy.ParseWebsite(website);
        var robotsUrl = new Uri(root.GetLeftPart(UriPartial.Authority) + "/robots.txt");
        string robots;
        try
        {
            var response = await DownloadAsync(root, robotsUrl, cancellationToken);
            robots = response.StatusCode == HttpStatusCode.NotFound ? "" :
                response.StatusCode == HttpStatusCode.OK && !response.IsHtml ? response.Content :
                throw new InvalidOperationException("Cannot verify robots.txt permissions. Research stopped; check site manually.");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("Could not safely retrieve robots.txt; research stopped. " + ex.Message, ex);
        }

        // Honor ordinary robots Crawl-delay where possible; decline slow/restrictive sites rather than hammer them.
        var delays = Regex.Matches(robots, @"(?mi)^\s*crawl-delay\s*:\s*(\d+(?:\.\d+)?)")
            .Select(m => double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0).ToArray();
        var requestedDelay = delays.Length > 0 ? delays.Max() : 0;
        if (requestedDelay > 3)
            throw new InvalidOperationException("The site requests a long robots.txt crawl delay; research it manually.");
        var pageDelay = TimeSpan.FromSeconds(Math.Max(1, requestedDelay));

        var pages = new List<string>();
        var candidates = new Dictionary<string, ResearchCandidate>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<Uri>();
        queue.Enqueue(root);
        while (queue.Count > 0 && pages.Count < MaxPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = queue.Dequeue();
            if (!visited.Add(uri.AbsoluteUri)) continue;
            if (!ResearchUrlPolicy.IsSameSite(root, uri) || !RobotsAllow(robots, uri)) continue;
            if (pages.Count > 0) await Task.Delay(pageDelay, cancellationToken); // bounded sequential requests
            PageResponse page;
            try { page = await DownloadAsync(root, uri, cancellationToken, robots); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                // Keep investigating remaining links; error is not evidence of no published process.
                continue;
            }
            if (page.StatusCode != HttpStatusCode.OK || !page.IsHtml) continue;
            pages.Add(page.Url.AbsoluteUri);
            var title = ExtractTitle(page.Content);
            var observation = ExtractSnippet(page.Content);
            if (!string.IsNullOrWhiteSpace(observation))
                candidates[page.Url.AbsoluteUri] = new ResearchCandidate($"Page text: {title}", page.Url.AbsoluteUri, observation);
            foreach (var (url, anchor) in ExtractLinks(page.Url, page.Content))
            {
                var score = Score(anchor + " " + url.AbsolutePath);
                if (score < 3 || candidates.Count >= 16) continue;
                var same = ResearchUrlPolicy.IsSameSite(root, url);
                if (same)
                {
                    var isHtml = !url.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
                    if (isHtml && visited.Count + queue.Count < 24) queue.Enqueue(url);
                }
                var detail = same
                    ? "Relevant link found on " + page.Url.AbsoluteUri + ". Review its applicability; this is not a confirmed vendor process."
                    : "External destination LINKED FROM " + page.Url.AbsoluteUri + "; not fetched. Verify it belongs to the company before using.";
                var key = url.AbsoluteUri;
                if (!candidates.ContainsKey(key))
                    candidates[key] = new ResearchCandidate("Link: " + Shorten(anchor, 100), key, detail);
            }
        }
        if (pages.Count == 0)
            throw new InvalidOperationException("No accessible HTML pages were retrieved (blocked, JavaScript-only, or incompatible site). Nothing was concluded.");
        var note = $"Checked {pages.Count} same-site HTML page(s), max {MaxPages}. " +
                   "Links are suggestions, not proof of an accepted supplier process. " +
                   "PDFs, third-party portals, dynamic pages and search-engine results were not inspected. " +
                   "Do not mark 'No process found' without additional manual checking.";
        return new ResearchScan(candidates.Values.Take(12).ToArray(), pages, note);
    }

    private sealed record PageResponse(Uri Url, HttpStatusCode StatusCode, bool IsHtml, string Content);

    private async Task<PageResponse> DownloadAsync(Uri root, Uri target, CancellationToken ct, string? robotsText = null)
    {
        for (var redirects = 0; redirects <= 2; redirects++)
        {
            if (!ResearchUrlPolicy.IsSameSite(root, target))
                throw new InvalidOperationException("The website redirected outside its approved hostname; no external host was fetched.");
            if (robotsText is not null && !RobotsAllow(robotsText, target))
                throw new InvalidOperationException("Robots.txt excludes the requested or redirected page.");
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode >= 300 && (int)response.StatusCode <= 399)
            {
                if (response.Headers.Location is not Uri location)
                    throw new InvalidOperationException("Redirect without a location header.");
                target = location.IsAbsoluteUri ? location : new Uri(target, location);
                continue;
            }
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            bool robots = target.AbsolutePath.Equals("/robots.txt", StringComparison.OrdinalIgnoreCase);
            bool html = mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) ||
                        mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
            if (response.StatusCode != HttpStatusCode.OK || (!robots && !html))
                return new PageResponse(target, response.StatusCode, false, "");
            if (response.Content.Headers.ContentLength is long contentLength && contentLength > MaxBytes)
                throw new InvalidOperationException("Page is larger than the read-only research limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaxBytes + 1 - (int)memory.Length)), ct);
                if (n == 0) break;
                memory.Write(buffer, 0, n);
                if (memory.Length > MaxBytes)
                    throw new InvalidOperationException("Page exceeded the read-only research limit.");
            }
            return new PageResponse(target, response.StatusCode, html, Encoding.UTF8.GetString(memory.ToArray()));
        }
        throw new InvalidOperationException("Too many redirects; research stopped for this page.");
    }

    internal static bool RobotsAllow(string text, Uri uri)
    {
        // Support standard User-agent, Allow, Disallow path-prefix rules; conservative on complicated sites.
        var groups = new List<(string Agent, List<(bool Allow, string Path)> Rules)>();
        string? agent = null;
        var rules = new List<(bool Allow, string Path)>();
        foreach (var line in text.Split('\n'))
        {
            var cleaned = line.Split('#')[0].Trim();
            var split = cleaned.IndexOf(':');
            if (split < 0) continue;
            var key = cleaned[..split].Trim().ToLowerInvariant();
            var value = cleaned[(split + 1)..].Trim();
            if (key == "user-agent")
            {
                if (agent is not null && rules.Count > 0) { groups.Add((agent, rules)); rules = new(); }
                agent = value.ToLowerInvariant();
            }
            else if (agent is not null && (key == "allow" || key == "disallow") && value.Length > 0)
                rules.Add((key == "allow", value));
        }
        if (agent is not null) groups.Add((agent, rules));
        var exact = groups.Where(g => g.Agent == "outreachflowresearchbot").SelectMany(g => g.Rules).ToArray();
        var applicable = exact.Length > 0 ? exact : groups.Where(g => g.Agent == "*").SelectMany(g => g.Rules).ToArray();
        var path = uri.PathAndQuery;
        var matching = applicable.Where(r =>
            Regex.IsMatch(path, "^" + Regex.Escape(r.Path.TrimEnd('$')).Replace(@"\*", ".*") +
                (r.Path.EndsWith('$') ? "$" : ""), RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
            .OrderByDescending(r => r.Path.Length).ThenByDescending(r => r.Allow).FirstOrDefault();
        return matching.Path is null || matching.Allow;
    }

    private static readonly Regex AnchorRegex = new("<a\\b(?<attributes>[^>]{0,1800})>(?<label>[\\s\\S]*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(300));
    private static readonly Regex HrefRegex = new("\\bhref\\s*=\\s*(?:\"(?<href>[^\"]+)\"|'(?<href>[^']+)'|(?<href>[^\\s>]+))", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(300));
    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Singleline | RegexOptions.Compiled, TimeSpan.FromMilliseconds(300));
    private static readonly Regex ScriptRegex = new("<(script|style|noscript)\\b[^>]*>[\\s\\S]*?</\\1>", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(300));
    private static readonly Regex SpaceRegex = new("\\s+", RegexOptions.Compiled, TimeSpan.FromMilliseconds(300));
    private static readonly Regex KeywordRegex = new("supplier|vendor|procurement|contractor|purchasing|registration|onboard|subcontract|bid opportunit|doing business with", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(300));

    private static IEnumerable<(Uri Url, string Label)> ExtractLinks(Uri page, string html)
    {
        foreach (Match anchor in AnchorRegex.Matches(html))
        {
            var href = HrefRegex.Match(anchor.Groups["attributes"].Value);
            if (!href.Success) continue;
            var raw = WebUtility.HtmlDecode(href.Groups["href"].Value);
            if (raw.Length > 2048 || !Uri.TryCreate(page, raw, out var url) ||
                (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) ||
                url.HostNameType != UriHostNameType.Dns || !string.IsNullOrWhiteSpace(url.UserInfo) ||
                (url.Port != 80 && url.Port != 443)) continue;
            var label = PlainText(anchor.Groups["label"].Value);
            if (label.Length == 0) label = url.AbsolutePath;
            yield return (new UriBuilder(url) { Fragment = "" }.Uri, Shorten(label, 120));
        }
    }

    private static int Score(string text)
    {
        var s = text.ToLowerInvariant();
        int score = 0;
        if (s.Contains("supplier") || s.Contains("vendor")) score += 5;
        if (s.Contains("procurement") || s.Contains("purchasing") || s.Contains("subcontract")) score += 4;
        if (s.Contains("register") || s.Contains("onboard") || s.Contains("apply") || s.Contains("contractor")) score += 3;
        if (s.Contains("bid") || s.Contains("doing-business") || s.Contains("doing business")) score += 3;
        if (s.Contains("contact")) score += 1;
        if (s.Contains("career") || s.Contains("jobs") || s.Contains("privacy") || s.Contains("investor")) score -= 5;
        return score;
    }

    private static string ExtractTitle(string html)
    {
        var match = Regex.Match(html, "<title[^>]*>(?<title>[\\s\\S]*?)</title>", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(300));
        return Shorten(match.Success ? PlainText(match.Groups["title"].Value) : "Untitled company page", 120);
    }

    private static string ExtractSnippet(string html)
    {
        var visible = PlainText(ScriptRegex.Replace(html, " "));
        var match = KeywordRegex.Match(visible);
        if (!match.Success) return "";
        var start = Math.Max(0, match.Index - 100);
        return "Unverified page excerpt: …" + Shorten(visible[start..], 440);
    }

    private static string PlainText(string html)
        => SpaceRegex.Replace(WebUtility.HtmlDecode(TagRegex.Replace(html, " ")), " ").Trim();

    private static string Shorten(string value, int max)
        => value.Length <= max ? value : value[..max].TrimEnd() + "…";

    public void Dispose() => client.Dispose();
}
