using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;

var root = FindRepository();
var appDll = Path.Combine(root, "OutreachFlow", "bin", "Release", "net10.0", "OutreachFlow.dll");
if (!File.Exists(appDll))
    throw new InvalidOperationException("Build Release first: dotnet build OutreachFlow.slnx -c Release");

var artifacts = Path.Combine(root, "OutreachFlow.BrowserTests", "TestResults");
// Remove stale local failure captures from a previous run; only the latest
// failing run retains diagnostic artifacts. Successful runs leave none.
await RemoveTestDirectoryAsync(artifacts);
var temporary = Path.Combine(Path.GetTempPath(), "outreachflow-ui-" + Guid.NewGuid().ToString("N"));
var port = FreePort();
var url = $"https://127.0.0.1:{port}";
using var app = new Process { StartInfo = new ProcessStartInfo("dotnet") {
    WorkingDirectory = temporary,
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    CreateNoWindow = true
} };
app.StartInfo.ArgumentList.Add(appDll);
app.StartInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
app.StartInfo.Environment["DOTNET_ENVIRONMENT"] = "Development";
app.StartInfo.Environment["ASPNETCORE_URLS"] = url;
app.StartInfo.Environment["ConnectionStrings__OutreachFlow"] = $"Data Source={Path.Combine(temporary, "browser.db")}";
app.StartInfo.Environment["ConnectionStrings__Accounts"] = $"Data Source={Path.Combine(temporary, "browser.auth.db")}";
app.StartInfo.Environment["Gmail__ClientId"] = "";
app.StartInfo.Environment["Gmail__ClientSecret"] = "";
app.StartInfo.Environment["GoogleAuth__ClientId"] = "";
app.StartInfo.Environment["GoogleAuth__ClientSecret"] = "";
app.StartInfo.Environment["Gmail__EnableSending"] = "false";
app.StartInfo.Environment["Tavily__ApiKey"] = "";
var output = new List<string>();
var logLock = new object();
app.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (logLock) output.Add(e.Data); };
app.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (logLock) output.Add(e.Data); };
IPlaywright? playwright = null;
IBrowser? browser = null;
IBrowserContext? context = null;
IPage? page = null;
var started = false;
try
{
    Directory.CreateDirectory(temporary);
    started = app.Start();
    if (!started) throw new InvalidOperationException("Could not launch OutreachFlow.");
    app.BeginOutputReadLine();
    app.BeginErrorReadLine();
    await ReadyAsync(url, app);
    playwright = await Playwright.CreateAsync();
    browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    context = await browser.NewContextAsync(new BrowserNewContextOptions {
        BaseURL = url, IgnoreHTTPSErrors = true, AcceptDownloads = false
    });
    await context.Tracing.StartAsync(new TracingStartOptions { Screenshots = true, Snapshots = true });
    page = await context.NewPageAsync();
    page.SetDefaultTimeout(12000);
    page.SetDefaultNavigationTimeout(20000);

    await Scenario("authentication", async () => {
        await page.GotoAsync(url + "/account/setup");
        await Visible(page.GetByRole(AriaRole.Heading, new() { Name = "Set up your owner account" }));
        await page.Locator("input[name=email]").FillAsync("browser-owner@example.org");
        await page.Locator("input[name=password]").FillAsync("BrowserOnlyPassword1234!");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create owner account" }).ClickAsync();
        await Visible(page.GetByRole(AriaRole.Heading, new() { Name = "Outreach dashboard" }));
    });

    await Scenario("offline provider safety", async () => {
        await page.GotoAsync(url + "/mailbox");
        await Visible(page.GetByText("Gmail OAuth is not configured", new() { Exact = false }));
        await page.GotoAsync(url + "/discover");
        await Visible(page.GetByText("Search isn't configured", new() { Exact = false }));
        await Check(await page.GetByRole(AriaRole.Button, new() { Name = "Search using workspace profile" }).IsDisabledAsync(),
            "Unconfigured provider search must be disabled.");
    });

    await Scenario("single-input batch simulation and duplicate-safe replay", async () => {
        await page.GotoAsync(url + "/prospects");
        var batch = page.GetByTestId("batch-inquiries");
        var input = batch.GetByRole(AriaRole.Textbox, new() { Name = "Batch prospect import string" });
        const string fixture = "Automated Fixture Company | South | https://example.org | batch-fixture@example.org";
        await input.FillAsync(fixture);
        await batch.GetByRole(AriaRole.Button, new() { Name = "Run simulated batch" }).ClickAsync();
        await Visible(batch.GetByText("Batch completed.", new() { Exact = false }));
        await Visible(batch.GetByText("Automated Fixture Company:", new() { Exact = false }));
        await batch.GetByRole(AriaRole.Link, new() { Name = "View history" }).ClickAsync();
        await Visible(page.GetByText("SIMULATED OUTBOUND", new() { Exact = false }));
        await page.GotoAsync(url + "/prospects");
        batch = page.GetByTestId("batch-inquiries");
        await batch.GetByRole(AriaRole.Textbox, new() { Name = "Batch prospect import string" }).FillAsync(fixture);
        await batch.GetByRole(AriaRole.Button, new() { Name = "Run simulated batch" }).ClickAsync();
        await Visible(batch.GetByText("Skipped: already recorded company/site or recipient", new() { Exact = false }));
        await Check(await page.Locator("a.list-row").Filter(new() { HasText = "Automated Fixture Company" }).CountAsync() == 1,
            "Replaying a batch created another prospect.");
    });

    const string company = "Browser Fixture Company";
    const string entry = company + " | South Charlotte | https://example.org | fixture@example.org";
    await Scenario("prospect import and dedupe", async () => {
        await page.GotoAsync(url + "/prospects");
        await page.Locator("#manual-import").FillAsync(entry);
        await page.GetByRole(AriaRole.Button, new() { Name = "Import batch" }).ClickAsync();
        await Visible(page.GetByText("Imported 1; skipped 0.", new() { Exact = false }));
        await page.Locator("#manual-import").FillAsync(entry);
        await page.GetByRole(AriaRole.Button, new() { Name = "Import batch" }).ClickAsync();
        await Visible(page.GetByText("Imported 0; skipped 1.", new() { Exact = false }));
        await Check(await page.Locator("a.list-row").Filter(new() { HasText = company }).CountAsync() == 1,
            "Duplicate import created another prospect.");
    });

    await Scenario("research, draft and suppressed simulated reply", async () => {
        await page.Locator("a.list-row").Filter(new() { HasText = company }).ClickAsync();
        await Visible(page.GetByRole(AriaRole.Heading, new() { Name = company }));
        await page.Locator("select").First.SelectOptionAsync("NoProcessFound");
        await page.Locator("textarea").First.FillAsync("Controlled fixture: no external website scan or vendor verification was performed.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save research finding" }).ClickAsync();
        await Visible(page.GetByRole(AriaRole.Button, new() { Name = "Generate inquiry draft" }));
        await page.GetByRole(AriaRole.Button, new() { Name = "Generate inquiry draft" }).ClickAsync();
        await Visible(page.GetByRole(AriaRole.Button, new() { Name = "Simulate initial email" }));
        await page.GetByRole(AriaRole.Button, new() { Name = "Simulate initial email" }).ClickAsync();
        await Visible(page.GetByRole(AriaRole.Button, new() { Name = "Simulate incoming reply" }));
        await page.Locator("select").Nth(1).SelectOptionAsync("Declined");
        await page.Locator("textarea[placeholder*='happy to talk']").FillAsync("Please do not contact us again.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Simulate incoming reply" }).ClickAsync();
        await Visible(page.GetByText("Suppressed recipient.", new() { Exact = false }));
        await Check(await page.GetByRole(AriaRole.Button, new() { Name = "Simulate initial email" }).CountAsync() == 0,
            "A suppressed prospect still exposes an initial-send control.");
    });

    await Scenario("workspace separation and recovery", async () => {
        await page.GotoAsync(url + "/account/workspaces");
        await page.Locator("form[action='/account/create-workspace'] input[name='name']")
            .FillAsync("Second Browser Fixture");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create workspace" }).ClickAsync();
        await Visible(page.GetByRole(AriaRole.Heading, new() { Name = "Outreach dashboard" }));
        await page.GotoAsync(url + "/prospects");
        await Check(await page.GetByText(company).CountAsync() == 0,
            "Prospect from first workspace leaked into the second.");
        await page.GotoAsync(url + "/account/workspaces");
        await page.Locator("input[name='workspaceId']").First.CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Switch workspace" }).ClickAsync();
        await page.GotoAsync(url + "/prospects");
        await Visible(page.GetByText(company));
    });

    await Scenario("logout and protected pages", async () => {
        await page.GotoAsync(url + "/account/logout");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();
        await Visible(page.GetByRole(AriaRole.Heading, new() { Name = "Sign in to OutreachFlow" }));
        await page.GotoAsync(url + "/prospects");
        await Visible(page.GetByRole(AriaRole.Heading, new() { Name = "Sign in to OutreachFlow" }));
    });

    await context.Tracing.StopAsync();
    Console.WriteLine("PASS: all isolated Playwright browser scenarios");
}
catch (Exception ex)
{
    Environment.ExitCode = 1;
    Console.Error.WriteLine("FAIL: " + ex);
    Directory.CreateDirectory(artifacts);
    if (page is not null)
        try { await page.ScreenshotAsync(new PageScreenshotOptions {
            Path = Path.Combine(artifacts, "failure.png"), FullPage = true
        }); } catch { /* Preserve original failure. */ }
    if (context is not null)
        try { await context.Tracing.StopAsync(new TracingStopOptions {
            Path = Path.Combine(artifacts, "failure-trace.zip")
        }); } catch { /* Preserve original failure. */ }
    lock (logLock)
        File.WriteAllLines(Path.Combine(artifacts, "app.log"), output.TakeLast(200));
    Environment.ExitCode = 1;
}
finally
{
    // Always release browser and app handles before deleting the SQLite
    // databases, DataProtection keys, cookies and browser-owned temp files.
    await CleanupStepAsync("Playwright context", async () =>
    {
        if (context is not null) await context.DisposeAsync();
    });
    await CleanupStepAsync("Chromium process", async () =>
    {
        if (browser is not null) await browser.DisposeAsync();
    });
    await CleanupStepAsync("Playwright driver", () =>
    {
        playwright?.Dispose();
        return Task.CompletedTask;
    });
    await CleanupStepAsync("OutreachFlow process", async () =>
    {
        if (!started || app.HasExited) return;
        app.Kill(entireProcessTree: true);
        await app.WaitForExitAsync();
    });
    await CleanupStepAsync("test databases and local state", () => RemoveTestDirectoryAsync(temporary));
    if (Environment.ExitCode == 0)
        await CleanupStepAsync("successful-run diagnostics", () => RemoveTestDirectoryAsync(artifacts));
}

static async Task CleanupStepAsync(string name, Func<Task> action)
{
    try { await action(); }
    catch (Exception ex)
    {
        Console.Error.WriteLine("FAIL: cleanup of " + name + ": " + ex.Message);
        Environment.ExitCode = 1;
    }
}

static async Task RemoveTestDirectoryAsync(string path)
{
    // Windows may hold a released SQLite or Chromium file briefly after exit.
    // A bounded retry is safer than swallowing the error or deleting user data.
    Exception? last = null;
    for (var attempt = 0; attempt < 10; attempt++)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            return;
        }
        catch (IOException ex) { last = ex; }
        catch (UnauthorizedAccessException ex) { last = ex; }
        await Task.Delay(150 * (attempt + 1));
    }
    throw new IOException("Unable to remove isolated test directory: " + path, last);
}

static async Task Visible(ILocator locator) =>
    await locator.WaitForAsync(new LocatorWaitForOptions {
        State = WaitForSelectorState.Visible, Timeout = 12000
    });

static Task Check(bool condition, string explanation) =>
    condition ? Task.CompletedTask : throw new InvalidOperationException(explanation);

static async Task Scenario(string name, Func<Task> action)
{
    await action();
    Console.WriteLine("PASS: " + name);
}

static string FindRepository()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "OutreachFlow.slnx"))) return dir.FullName;
    throw new DirectoryNotFoundException("Could not locate OutreachFlow.slnx.");
}

static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static async Task ReadyAsync(string url, Process process)
{
    using var client = new HttpClient(new HttpClientHandler {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    }) { Timeout = TimeSpan.FromSeconds(3) };
    var deadline = DateTime.UtcNow.AddSeconds(45);
    while (DateTime.UtcNow < deadline)
    {
        if (process.HasExited)
            throw new InvalidOperationException("OutreachFlow exited before becoming ready.");
        try {
            using var result = await client.GetAsync(url + "/account/setup");
            if (result.IsSuccessStatusCode) return;
        } catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        await Task.Delay(500);
    }
    throw new TimeoutException("OutreachFlow did not become ready on isolated HTTPS loopback.");
}
