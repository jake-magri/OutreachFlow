using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Text;
using OutreachFlow.Components;
using OutreachFlow.Data;
using OutreachFlow.Services;
using OutreachFlow.Services.Research;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContextFactory<OutreachDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("OutreachFlow")
        ?? "Data Source=outreachflow.db"));
builder.Services.AddScoped<OutreachService>();
builder.Services.AddSingleton<ICompanyResearcher, OfficialSiteResearcher>();
builder.Services.AddSingleton<IOutboundEmail, DryRunEmail>();

var app = builder.Build();

// Fail closed: authentication and production hardening are intentionally NOT included yet.
if (!app.Environment.IsDevelopment())
    throw new InvalidOperationException("Prototype only: add authentication and security hardening before hosting OutreachFlow.");

// An explicit localhost guard also covers accidental --urls 0.0.0.0 in Development.
// This is not a replacement for production authentication.
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
app.UseAntiforgery();

using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<OutreachDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync(); // Prototype schema; replace with migrations later.
    await DemoData.SeedAsync(db);
}

// Local export of pipeline metadata (not email content).
app.MapGet("/export/prospects.csv", async (OutreachService service) =>
    Results.File(Encoding.UTF8.GetBytes("\uFEFF" + await service.ExportCsvAsync()),
        "text/csv; charset=utf-8", "OutreachFlow-prospects.csv"));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
