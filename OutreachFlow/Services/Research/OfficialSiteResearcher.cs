using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using OutreachFlow.Services.Discovery;

namespace OutreachFlow.Services.Research;

/// <summary>
/// Read-only, deliberately narrow HTML discovery of the submitted company website.
/// No external search engine, PDF parsing, browser scripts, form submission or email.
/// </summary>
public sealed class OfficialSiteResearcher : IBrowserCompanyResearcher, IDisposable
{
    private const int MaxPages = 6;
    private const int MaxBytes = 300_000;
    private readonly HttpClient client;
    private readonly IHtmlBrowserRenderer? renderer;

    public OfficialSiteResearcher(IHtmlBrowserRenderer? renderer = null)
    {
        this.renderer = renderer;
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

    public Task<ResearchScan> SearchAsync(string website, CancellationToken cancellationToken = default)
        => SearchAsync(website, ResearchProfileCatalog.Default(), cancellationToken);

    public Task<ResearchScan> SearchAsync(string website, ResearchProfile profile, CancellationToken cancellationToken = default)
        => SearchCoreAsync(website, profile, useBrowser: false, cancellationToken);

    public Task<ResearchScan> SearchWithBrowserAsync(string website, ResearchProfile profile, CancellationToken cancellationToken = default)
        => SearchCoreAsync(website, profile, useBrowser: true, cancellationToken);

    private async Task<ResearchScan> SearchCoreAsync(string website, ResearchProfile profile,
        bool useBrowser, CancellationToken cancellationToken)
    {
        if (useBrowser && renderer is null)
            throw new InvalidOperationException("Experimental browser rendering is not configured.");
        profile = ResearchProfile.Validate(profile);
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
        var contacts = new Dictionary<string, ResearchContact>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<ResearchPageDiagnostic>();
        var processLinks = new Dictionary<string, ProcessLinkEvidence>(StringComparer.OrdinalIgnoreCase);
        int skippedPages = 0;
        int browserAttempts = 0;
        int browserImproved = 0;
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
                skippedPages++;
                continue;
            }
            if (page.StatusCode != HttpStatusCode.OK || !page.IsHtml)
            {
                skippedPages++;
                diagnostics.Add(new ResearchPageDiagnostic(
                    page.Url.AbsoluteUri, "(HTTP response unavailable)", 0, [], 
                    "The server did not return a usable HTTP 200 HTML page.")
                {
                    HttpStatusCode = (int)page.StatusCode,
                    InitialRuleId = page.IsHtml ? "http-status" : "http-status-or-media-type",
                    BrowserDecision = useBrowser
                        ? "Skipped: no approved HTML to render"
                        : "Not requested"
                });
                continue;
            }
            pages.Add(page.Url.AbsoluteUri);
            var diagnostic = ResearchPageInspector.Inspect(page.Url, page.Content, profile)
                with { HttpStatusCode = (int)page.StatusCode };
            if (useBrowser)
            {
                if (diagnostic.IsChallenge)
                {
                    diagnostic = diagnostic with { BrowserDecision =
                        "Skipped: access-challenge rule " + diagnostic.InitialRuleId +
                        " matched phrase '" + diagnostic.MatchedPhrase + "'" };
                }
                else if (!BrowserResearchPolicy.MayRender(diagnostic))
                {
                    diagnostic = diagnostic with { BrowserDecision =
                        "Skipped: original HTML did not require rendering" };
                }
                else
                {
                    browserAttempts++;
                    diagnostic = diagnostic with { BrowserDecision = "Attempted" };
                    try
                    {
                        var rendered = await renderer!.RenderAsync(root, page.Url,
                            (target, token) => DownloadBrowserResourceAsync(root, target, robots, token),
                            cancellationToken);
                        var revised = ResearchPageInspector.Inspect(page.Url, rendered.Html, profile);
                        if (!revised.IsInconclusive)
                        {
                            browserImproved++;
                            page = page with { Content = rendered.Html };
                            diagnostic = revised with
                            {
                                HttpStatusCode = (int)page.StatusCode,
                                BrowserDecision = "Rendered meaningful HTML; original rule: " + diagnostic.InitialRuleId + ". " + RenderResourceSummary(rendered)
                            };
                        }
                        else
                        {
                            diagnostic = diagnostic with { Warning = diagnostic.Warning +
                                " Browser-rendered HTML was still inconclusive.",
                                BrowserDecision = "Attempted; rendered result still inconclusive (" +
                                    revised.InitialRuleId + "). " + RenderResourceSummary(rendered) };
                        }
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                        ex is InvalidOperationException or IOException or TimeoutException or
                            OperationCanceledException or Microsoft.Playwright.PlaywrightException)
                    {
                        diagnostic = diagnostic with { Warning = diagnostic.Warning +
                            " Browser rendering unavailable or incomplete; verify manually.",
                            BrowserDecision = "Attempted; failed (" + ex.GetType().Name + ")" };
                    }
                }
            }
            diagnostics.Add(diagnostic);
            if (diagnostic.IsInconclusive)
            {
                // A challenge, script-only shell or suspiciously empty page cannot support an
                // absence conclusion. Do not extract misleading contacts or links from it.
                continue;
            }
            foreach (var contact in PublicContactExtractor.Extract(page.Content, page.Url))
            {
                if (contacts.Count >= 30) break;
                // Distinct email addresses need only their first, provenance-bearing checked page.
                contacts.TryAdd(contact.Address, contact);
            }
            var title = ExtractTitle(page.Content);
            var observation = ExtractSnippet(page.Content, profile);
            if (!string.IsNullOrWhiteSpace(observation))
                candidates[page.Url.AbsoluteUri] = new ResearchCandidate($"Page text: {title}", page.Url.AbsoluteUri, observation);
            foreach (var link in ProcessLinkAnalyzer.Extract(page.Url, page.Content, profile))
            {
                if (processLinks.Count < 30)
                    processLinks.TryAdd(link.DestinationUrl, link);
            }
            foreach (var (url, anchor) in ExtractLinks(page.Url, page.Content))
            {
                var score = ResearchProfileRules.ScoreLink(anchor + " " + url.AbsolutePath, profile);
                if (score < 1) continue;
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
                if (!candidates.ContainsKey(key) && candidates.Count < 24)
                    candidates[key] = new ResearchCandidate("Link: " + Shorten(anchor, 100), key, detail);
            }
        }
        if (pages.Count == 0 && diagnostics.Count == 0)
            throw new InvalidOperationException("No accessible HTML pages were retrieved (blocked, JavaScript-only, or incompatible site). Nothing was concluded.");
        var note = $"Checked {pages.Count} same-site HTML page(s), max {MaxPages}. " +
                   $"Experimental browser attempts: {browserAttempts}, meaningful rendered pages: {browserImproved}. " +
                   "Links are suggestions, not proof of an accepted supplier process. " +
                   "PDFs, third-party portals, dynamic pages and search-engine results were not inspected. " +
                   "Do not mark 'No process found' without additional manual checking.";
        var possibleProcess = candidates.Values.Any(x =>
            ResearchProfileRules.ContainsProcessSignal(x.Title + " " + x.Observation, profile));
        var inconclusiveCount = diagnostics.Count(x => x.IsInconclusive);
        var triage = inconclusiveCount > 0 || skippedPages > 0
            ? $"Research incomplete: {inconclusiveCount} HTML page(s) returned suspicious or insufficient content, and {skippedPages} page request(s) could not be evaluated. Verify manually; do not infer no process exists."
            : possibleProcess
                ? "Potential process/application reference found: verify its relevance and whether this opportunity qualifies before assigning a route."
                : contacts.Count > 0
                    ? "Published contact candidates found, but no verified application process. Continue manual source review before inquiry eligibility."
                    : "No matching process or contact discovered in checked HTML. Review additional sources; absence is not proof.";
        return new ResearchScan(candidates.Values.Take(16).ToArray(), pages, note)
        {
            Contacts = contacts.Values.ToArray(),
            SuggestedReview = triage,
            PageDiagnostics = diagnostics,
            ProcessLinks = processLinks.Values.ToArray()
        };
    }

    private static string RenderResourceSummary(BrowserRenderResult result) =>
        $"Network: fulfilled={result.FulfilledRequests}, blocked={result.BlockedRequests}, " +
        $"blocked scripts={result.BlockedScriptRequests}, unsupported={result.UnsupportedRequests}, failed={result.FailedRequests}.";

    /// <summary>
    /// Diagnostic-only probe using the SAME protected downloader and user-agent as normal scans.
    /// No cookies or complete HTML are exposed. Called only by an opt-in developer test.
    /// </summary>
    public async Task<PublicPageProbe> ProbeAsync(string website, CancellationToken ct = default)
    {
        var root = ResearchUrlPolicy.ParseWebsite(website);
        var robotsUrl = new Uri(root.GetLeftPart(UriPartial.Authority) + "/robots.txt");
        var robotsResponse = await DownloadAsync(root, robotsUrl, ct);
        var robots = robotsResponse.StatusCode == HttpStatusCode.NotFound ? "" :
            robotsResponse.StatusCode == HttpStatusCode.OK && !robotsResponse.IsHtml
                ? robotsResponse.Content
                : throw new InvalidOperationException("Probe stopped: robots.txt could not be verified.");
        if (!RobotsAllow(robots, root))
            throw new InvalidOperationException("Probe stopped: robots.txt excludes the requested page.");
        var page = await DownloadAsync(root, root, ct, robots);
        var html = page.Content;
        var visible = Regex.Replace(
            Regex.Replace(html, "<(script|style|noscript)\\b[^>]*>[\\s\\S]*?</\\1>", " ",
                RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
            "<[^>]*>", " ", RegexOptions.Singleline, TimeSpan.FromMilliseconds(200));
        visible = Regex.Replace(WebUtility.HtmlDecode(visible), "\\s+", " ",
            RegexOptions.None, TimeSpan.FromMilliseconds(200)).Trim();
        var scriptTagCount = Regex.Matches(html, "<script\\b", RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(200)).Count;
        var scriptSrcs = Regex.Matches(html,
            "<script\\b[^>]*\\bsrc\\s*=\\s*['\\\"](?<src>[^'\\\"]+)['\\\"]",
            RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        var externalScriptCount = 0;
        var sameSiteScriptCount = 0;
        foreach (Match m in scriptSrcs)
        {
            if (!Uri.TryCreate(page.Url, WebUtility.HtmlDecode(m.Groups["src"].Value),
                out var src)) continue;
            if (ResearchUrlPolicy.IsSameSite(root, src)) sameSiteScriptCount++;
            else externalScriptCount++;
        }
        return new PublicPageProbe((int)page.StatusCode, page.IsHtml, html.Length,
            ExtractTitle(html), visible[..Math.Min(240, visible.Length)], scriptTagCount,
            sameSiteScriptCount, externalScriptCount);
    }

    public sealed record PublicPageProbe(int HttpStatus, bool IsHtml, int HtmlCharacters,
        string Title, string ReadableTextPreview, int ScriptTags,
        int SameSiteScriptSources, int CrossSiteScriptSources);

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

    // Browser network requests are resolved ONLY through this existing SSRF-hardened client.
    // The read-only browser is never allowed to fetch directly or access external hosts.
    private async Task<BrowserResource> DownloadBrowserResourceAsync(
        Uri root, Uri target, string robots, CancellationToken ct)
    {
        const int resourceLimit = 150_000;
        for (var redirects = 0; redirects <= 2; redirects++)
        {
            if (!ResearchUrlPolicy.IsSameSite(root, target) || !RobotsAllow(robots, target))
                throw new InvalidOperationException("Browser resource redirected outside approved scope or robots rules.");
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                if (response.Headers.Location is not Uri location)
                    throw new InvalidOperationException("Browser resource redirect has no destination.");
                target = location.IsAbsoluteUri ? location : new Uri(target, location);
                continue;
            }
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!BrowserResearchPolicy.AllowMediaType(mediaType))
                throw new InvalidOperationException("Browser resource content type is not permitted.");
            if (response.Content.Headers.ContentLength > resourceLimit)
                throw new InvalidOperationException("Browser resource exceeds size limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var remaining = resourceLimit + 1 - (int)buffer.Length;
                var read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remaining)), ct);
                if (read == 0) break;
                buffer.Write(chunk, 0, read);
                if (buffer.Length > resourceLimit)
                    throw new InvalidOperationException("Browser resource exceeded size limit.");
            }
            return new BrowserResource((int)response.StatusCode, mediaType,
                Encoding.UTF8.GetString(buffer.ToArray()));
        }
        throw new InvalidOperationException("Browser resource redirected too many times.");
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

    private static string ExtractTitle(string html)
    {
        var match = Regex.Match(html, "<title[^>]*>(?<title>[\\s\\S]*?)</title>", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(300));
        return Shorten(match.Success ? PlainText(match.Groups["title"].Value) : "Untitled company page", 120);
    }

    private static string ExtractSnippet(string html, ResearchProfile profile)
    {
        var visible = PlainText(ScriptRegex.Replace(html, " "));
        var index = ResearchProfileRules.FirstMatchIndex(visible, profile);
        if (index < 0) return "";
        var start = Math.Max(0, index - 100);
        return "Unverified page excerpt: …" + Shorten(visible[start..], 440);
    }

    private static string PlainText(string html)
        => SpaceRegex.Replace(WebUtility.HtmlDecode(TagRegex.Replace(html, " ")), " ").Trim();

    private static string Shorten(string value, int max)
        => value.Length <= max ? value : value[..max].TrimEnd() + "…";

    public void Dispose() => client.Dispose();
}
