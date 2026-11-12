using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;
using Microsoft.AspNetCore.Identity;
using OutreachFlow.Models;
using System.Net;
using System.Text;
using OutreachFlow.Components;
using OutreachFlow.Data;
using OutreachFlow.Services;
using OutreachFlow.Services.Research;
using OutreachFlow.Services.Discovery;
using OutreachFlow.Services.Mailbox;
using OutreachFlow.Services.Persistence;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// Store local OAuth state and encrypted refresh-token keys outside the repository.
// On Windows, protect the key ring with current-user DPAPI. Linux CI runs only
// offline fixtures: Gmail connection deliberately remains unavailable there.
var mailboxKeys = Path.Combine(builder.Environment.ContentRootPath, "mailbox-keys");
var protection = builder.Services.AddDataProtection()
    .SetApplicationName("OutreachFlow.Mailbox.v1")
    .PersistKeysToFileSystem(new DirectoryInfo(mailboxKeys));
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();
builder.Services.AddDbContextFactory<AccountDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Accounts")
        ?? "Data Source=outreachflow.auth.db"));
builder.Services.AddIdentityCore<ApplicationUser>(options =>
{
    options.User.RequireUniqueEmail = true;
    options.Password.RequiredLength = 12;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
}).AddSignInManager().AddEntityFrameworkStores<AccountDbContext>();
var auth = builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme);
auth.AddIdentityCookies();
// Google sign-in needs only identity scopes. Gmail access is separately
// consented to from Mailbox using gmail.readonly, never at login.
if (GoogleSignInEndpoints.Configured(builder.Configuration))
{
    auth.AddGoogle(GoogleSignInEndpoints.Provider, options =>
    {
        options.ClientId = GoogleSignInEndpoints.ClientId(builder.Configuration)!;
        options.ClientSecret = GoogleSignInEndpoints.ClientSecret(builder.Configuration)!;
        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.CallbackPath = "/signin-google";
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("email");
        options.Scope.Add("profile");
        options.SaveTokens = false;
        // Google's OAuth userinfo response explicitly reports verified_email.
        // Fail closed for first-time registration if that flag is absent/false.
        options.ClaimActions.Add(new JsonKeyClaimAction("google_email_verified", "string", "verified_email"));
        options.ClaimActions.Add(new JsonKeyClaimAction("google_verified_email", "string", "email_verified"));
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
    });
}
builder.Services.Configure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, options =>
{
    options.LoginPath = "/account/login";
    options.AccessDeniedPath = "/account/login";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});
builder.Services.AddAuthorization();
builder.Services.AddScoped<IWorkspaceAccess, AuthenticatedWorkspaceAccess>();
builder.Services.AddScoped<GoogleUserProvisioner>();

builder.Services.AddDbContextFactory<OutreachDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("OutreachFlow")
        ?? "Data Source=outreachflow.db"));
builder.Services.AddSingleton<IResearchProfileStore>(_ =>
    new JsonResearchProfileStore(Path.Combine(builder.Environment.ContentRootPath, "research-profiles")));
builder.Services.AddSingleton<ISearchUsageBudget>(_ =>
    new LocalSearchUsageBudget(Path.Combine(builder.Environment.ContentRootPath, "search-usage"),
        builder.Configuration.GetValue<int?>("Search:MonthlyRequestLimit") ?? 100));
builder.Services.AddHttpClient<IWebSearchProvider, TavilySearchProvider>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(12);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false,
    UseProxy = false
});
builder.Services.AddSingleton(sp => new GmailConnectionStore(
    Path.Combine(builder.Environment.ContentRootPath, "mailbox-connections"),
    sp.GetRequiredService<IDataProtectionProvider>()));
builder.Services.AddHttpClient("GmailOAuth", client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false, UseProxy = false
    });
builder.Services.AddHttpClient("GmailApi", client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false, UseProxy = false
    });
builder.Services.AddScoped<GmailMailboxService>();
builder.Services.AddScoped<IGmailSender>(sp => sp.GetRequiredService<GmailMailboxService>());
builder.Services.AddScoped<ApprovedSendService>();
builder.Services.AddScoped<BatchInquiryService>();
builder.Services.AddScoped<IAutomationInbox, GmailAutomationInbox>();
builder.Services.AddScoped<FollowupAutomationService>();
// Opt-in only; the worker immediately exits unless Followups:Enabled is true.
builder.Services.AddHostedService<FollowupWorker>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<OpportunityDiscoveryService>();
builder.Services.AddScoped<OutreachService>();
builder.Services.AddSingleton<IHtmlBrowserRenderer, PlaywrightHtmlBrowserRenderer>();
builder.Services.AddSingleton<ICompanyResearcher, OfficialSiteResearcher>();
builder.Services.AddSingleton<IOutboundEmail, DryRunEmail>();

var app = builder.Build();

// Fail closed outside Development: identity exists, but hosted migrations, recovery,
// provider compliance, and production security hardening are incomplete.
if (!app.Environment.IsDevelopment())
    throw new InvalidOperationException("Prototype only: add authentication and security hardening before hosting OutreachFlow.");

// An explicit localhost guard also covers accidental --urls 0.0.0.0 in Development.
// This complements authentication, not a replacement for secure hosting.
app.Use(async (context, next) =>
{
    if (context.Connection.RemoteIpAddress is not IPAddress address || !IPAddress.IsLoopback(address))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Local-only prototype. Remote access is disabled.");
        return;
    }
    await next();
});

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<OutreachDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    // Preserve existing business data. New users' workspaces are provisioned
    // only during first-owner setup or Google-first registration, not at startup.
    await StartupDatabase.InitializeAsync(db);
    var authFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AccountDbContext>>();
    await using var authDb = await authFactory.CreateDbContextAsync();
    await StartupDatabase.InitializeAsync(authDb);
}

app.MapOutreachAccounts();
app.MapGoogleSignIn();
app.MapGmailMailboxEndpoints();

// Local export of pipeline metadata (not email content).
app.MapGet("/export/prospects.csv", async (OutreachService service) =>
    Results.File(Encoding.UTF8.GetBytes("\uFEFF" + await service.ExportCsvAsync()),
        "text/csv; charset=utf-8", "OutreachFlow-prospects.csv")).RequireAuthorization();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization();

app.Run();
