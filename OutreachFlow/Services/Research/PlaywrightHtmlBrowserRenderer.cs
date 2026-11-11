using Microsoft.Playwright;

namespace OutreachFlow.Services.Research;

/// <summary>
/// A browser rendering experiment. Every HTTP request is fulfilled by a separate,
/// SSRF-guarded C# fetcher; the browser is never handed an unguarded network route.
/// No form submission, authentication, anti-bot bypass, cookies or persistent profile.
/// </summary>
public interface IHtmlBrowserRenderer
{
    Task<BrowserRenderResult> RenderAsync(Uri root, Uri page, Func<Uri, CancellationToken, Task<BrowserResource>> fetch,
        CancellationToken cancellationToken = default);
}

public sealed record BrowserResource(int StatusCode, string MediaType, string Body);

// Aggregate counts only: no resource URLs, page bodies, credentials or cookies retained.
public sealed record BrowserRenderResult(string Html, int FulfilledRequests,
    int BlockedRequests, int BlockedScriptRequests, int UnsupportedRequests, int FailedRequests);

public static class BrowserResearchPolicy
{
    public static bool MayRender(ResearchPageDiagnostic diagnostic)
        => diagnostic.IsInconclusive && !diagnostic.IsChallenge && diagnostic.HttpStatusCode == 200;

    public static bool AllowResource(Uri root, string url, string method)
        => string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
           Uri.TryCreate(url, UriKind.Absolute, out var target) &&
           ResearchUrlPolicy.IsSameSite(root, target) &&
           target.HostNameType == UriHostNameType.Dns &&
           string.IsNullOrEmpty(target.UserInfo) &&
           target.AbsoluteUri.Length <= 2048;

    public static bool AllowMediaType(string mediaType)
        => mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) ||
           mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase) ||
           mediaType.Equals("text/css", StringComparison.OrdinalIgnoreCase) ||
           mediaType.Equals("text/javascript", StringComparison.OrdinalIgnoreCase) ||
           mediaType.Equals("application/javascript", StringComparison.OrdinalIgnoreCase) ||
           mediaType.Equals("application/x-javascript", StringComparison.OrdinalIgnoreCase) ||
           mediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase) ||
           mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase);
}

public sealed class PlaywrightHtmlBrowserRenderer : IHtmlBrowserRenderer
{
    private const int MaxRequests = 24;
    private const int MaxRenderedChars = 300_000;
    private static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(22);
    private static readonly TimeSpan RenderDelay = TimeSpan.FromSeconds(3);

    public async Task<BrowserRenderResult> RenderAsync(Uri root, Uri page,
        Func<Uri, CancellationToken, Task<BrowserResource>> fetch, CancellationToken cancellationToken = default)
    {
        if (!ResearchUrlPolicy.IsSameSite(root, page))
            throw new InvalidOperationException("Browser page is outside the approved site.");

        var requested = 0;
        var fulfilled = 0;
        var blocked = 0;
        var blockedScripts = 0;
        var unsupported = 0;
        var failed = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MaxDuration);
        var ct = timeout.Token;
        using var playwright = await Playwright.CreateAsync().WaitAsync(ct);
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = true,
            Args = ["--disable-features=WebTransport"]
        }).WaitAsync(ct);
        await using var context = await browser.NewContextAsync(new()
        {
            JavaScriptEnabled = true,
            ServiceWorkers = ServiceWorkerPolicy.Block,
            AcceptDownloads = false,
            IgnoreHTTPSErrors = false
        }).WaitAsync(ct);

        // Keep the rendering process read-only. All HTTP requests are intercepted and
        // answered from the vetted C# fetcher; no Route.ContinueAsync or Route.FetchAsync.
        await context.RouteAsync("**/*", async route =>
        {
            var permitted = BrowserResearchPolicy.AllowResource(root, route.Request.Url, route.Request.Method);
            if (!permitted || Interlocked.Increment(ref requested) > MaxRequests)
            {
                Interlocked.Increment(ref blocked);
                if (string.Equals(route.Request.ResourceType, "script", StringComparison.OrdinalIgnoreCase))
                    Interlocked.Increment(ref blockedScripts);
                await route.AbortAsync("blockedbyclient");
                return;
            }

            try
            {
                var resourceUrl = new Uri(route.Request.Url);
                var response = await fetch(resourceUrl, ct);
                if (!BrowserResearchPolicy.AllowMediaType(response.MediaType) ||
                    response.Body.Length > MaxRenderedChars)
                {
                    Interlocked.Increment(ref unsupported);
                    await route.AbortAsync("blockedbyclient");
                    return;
                }

                await route.FulfillAsync(new()
                {
                    Status = response.StatusCode,
                    ContentType = response.MediaType,
                    Body = response.Body
                });
                Interlocked.Increment(ref fulfilled);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException
                                       or OperationCanceledException or IOException)
            {
                Interlocked.Increment(ref failed);
                await route.AbortAsync("blockedbyclient");
            }
        });

        // WebSockets are not HTTP routes; explicitly prevent their connection.
        await context.RouteWebSocketAsync("**/*", ws => ws.CloseAsync());
        await context.AddInitScriptAsync("""
            Object.defineProperty(window, 'RTCPeerConnection', {value: undefined});
            Object.defineProperty(window, 'webkitRTCPeerConnection', {value: undefined});
            Object.defineProperty(window, 'WebTransport', {value: undefined});
            """);

        var browserPage = await context.NewPageAsync().WaitAsync(ct);
        var navigation = await browserPage.GotoAsync(page.AbsoluteUri, new()
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 13000
        }).WaitAsync(ct);

        if (navigation?.Status != 200)
            throw new InvalidOperationException("Browser navigation did not return an accessible HTML page.");

        // Waiting without executing scripts would never render a JavaScript-only site.
        await Task.Delay(RenderDelay, ct);
        var html = await browserPage.ContentAsync().WaitAsync(ct);
        if (html.Length > MaxRenderedChars)
            throw new InvalidOperationException("Browser-rendered HTML exceeded the research limit.");
        return new BrowserRenderResult(html, fulfilled, blocked, blockedScripts, unsupported, failed);
    }

}
