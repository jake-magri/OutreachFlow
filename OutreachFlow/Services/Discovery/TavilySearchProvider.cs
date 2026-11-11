using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace OutreachFlow.Services.Discovery;

/// <summary>
/// Tavily supplies indexed candidates; no generated answers or full-page content are requested.
/// The host owns the key. Never expose it in HTML, JS, logs or workspace settings.
/// </summary>
public sealed class TavilySearchProvider(HttpClient client, IConfiguration config) : IWebSearchProvider
{
    private const int MaxResponseBytes = 250_000;
    public string Name => "Tavily Search";
    private string Key => config["Tavily:ApiKey"] ??
        Environment.GetEnvironmentVariable("TAVILY_API_KEY") ?? "";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Key);

    public async Task<IReadOnlyList<SearchListing>> SearchAsync(string query, int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Search is not configured. The host must set a Tavily API key.");
        if (string.IsNullOrWhiteSpace(query) || query.Length > 400 || maxResults is < 1 or > 10)
            throw new InvalidOperationException("Invalid bounded search request.");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key.Trim());
        request.Content = JsonContent.Create(new
        {
            query, max_results = maxResults,
            search_depth = "basic",
            include_answer = false,
            include_raw_content = false,
            include_images = false
        });

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("Search provider rate limit reached. Try again later.");
        if (response.StatusCode is HttpStatusCode.PaymentRequired or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Search provider quota or authorization issue. Ask the application owner to review service settings.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Search provider temporarily unavailable (HTTP " +
                (int)response.StatusCode + ").");
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new InvalidOperationException("Search provider response exceeds the supported limit.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var data = new byte[8192];
        while (true)
        {
            int n = await stream.ReadAsync(data.AsMemory(0,
                Math.Min(data.Length, MaxResponseBytes + 1 - (int)buffer.Length)), cancellationToken);
            if (n == 0) break;
            buffer.Write(data, 0, n);
            if (buffer.Length > MaxResponseBytes)
                throw new InvalidOperationException("Search provider response exceeded the supported limit.");
        }
        return ParseResults(System.Text.Encoding.UTF8.GetString(buffer.ToArray()), maxResults);
    }

    public static IReadOnlyList<SearchListing> ParseResults(string json, int maxResults = 5)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 10 });
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Search provider returned an unexpected results format.");

        var list = new List<SearchListing>();
        foreach (var item in results.EnumerateArray())
        {
            if (list.Count >= maxResults) break;
            if (item.ValueKind != JsonValueKind.Object) continue;
            static string Read(JsonElement item, string key)
                => item.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String
                    ? prop.GetString() ?? "" : "";
            var url = Read(item, "url");
            var title = Read(item, "title");
            if (string.IsNullOrWhiteSpace(url)) continue;
            list.Add(new SearchListing(title, url, Read(item, "content")));
        }
        return list;
    }
}
