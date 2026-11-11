using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OutreachFlow.Data;
using OutreachFlow.Models;

namespace OutreachFlow.Services.Mailbox;

/// <summary>Local-only OAuth callback. Never sends email or stores Google access tokens.</summary>
public static class GmailMailboxEndpoints
{
    private const string PendingCookie = "OutreachFlow.GmailOAuth.Pending";
    private sealed record PendingOAuth(string State, string Verifier, string UserId,
        int WorkspaceId, DateTime ExpiresUtc);

    public static IEndpointRouteBuilder MapGmailMailboxEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/mailbox/google/connect", async (HttpContext context,
            GmailMailboxService gmail, AccountDbContext accounts,
            IDbContextFactory<OutreachDbContext> factory,
            IDataProtectionProvider protection) =>
        {
            if (!gmail.IsConfigured)
                return Results.BadRequest("Gmail connection is not configured for the local Windows prototype.");
            var workspaceId = await MailboxOwner.RequireAsync(context.User, accounts);
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await using var db = await factory.CreateDbContextAsync();
            var senderEmail = await db.Workspaces.Where(w => w.Id == workspaceId)
                .Select(w => w.SenderEmail).SingleAsync();
            if (string.IsNullOrWhiteSpace(senderEmail))
                return Results.BadRequest("Set a sender email in workspace Settings before connecting Gmail.");
            var state = GmailMailboxService.NewVerifier();
            var verifier = GmailMailboxService.NewVerifier();
            var pending = new PendingOAuth(state, verifier, userId, workspaceId,
                DateTime.UtcNow.AddMinutes(10));
            var protector = protection.CreateProtector("OutreachFlow.GmailOAuth.Pending.v1");
            context.Response.Cookies.Append(PendingCookie,
                protector.Protect(JsonSerializer.Serialize(pending)),
                new CookieOptions
                {
                    HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax,
                    IsEssential = true, MaxAge = TimeSpan.FromMinutes(10),
                    Path = "/mailbox/google/callback"
                });
            return Results.Redirect(gmail.AuthorizationUrl(state, GmailMailboxService.Challenge(verifier)));
        }).RequireAuthorization();

        app.MapGet("/mailbox/google/callback", async (HttpContext context,
            GmailMailboxService gmail, AccountDbContext accounts,
            IDbContextFactory<OutreachDbContext> factory,
            IDataProtectionProvider protection) =>
        {
            var protectedPending = context.Request.Cookies[PendingCookie];
            context.Response.Cookies.Delete(PendingCookie, new CookieOptions
            {
                Path = "/mailbox/google/callback", Secure = true, SameSite = SameSiteMode.Lax
            });
            if (string.IsNullOrEmpty(protectedPending))
                return Results.Redirect("/mailbox?error=state");
            PendingOAuth? pending;
            try
            {
                var protector = protection.CreateProtector("OutreachFlow.GmailOAuth.Pending.v1");
                pending = JsonSerializer.Deserialize<PendingOAuth>(
                    protector.Unprotect(protectedPending));
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException)
            {
                return Results.Redirect("/mailbox?error=state");
            }
            if (pending is null || pending.ExpiresUtc <= DateTime.UtcNow ||
                pending.State.Length < 40 ||
                context.Request.Query["state"].ToString() is not string actualState ||
                actualState.Length != pending.State.Length ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(actualState), Encoding.UTF8.GetBytes(pending.State)))
                return Results.Redirect("/mailbox?error=state");
            var activeWorkspace = await MailboxOwner.RequireAsync(context.User, accounts);
            if (activeWorkspace != pending.WorkspaceId ||
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) != pending.UserId)
                return Results.Forbid();
            var code = context.Request.Query["code"].ToString();
            if (context.Request.Query.ContainsKey("error") || string.IsNullOrWhiteSpace(code))
                return Results.Redirect("/mailbox?error=denied");
            await using var db = await factory.CreateDbContextAsync();
            var expectedEmail = await db.Workspaces.Where(w => w.Id == activeWorkspace)
                .Select(w => w.SenderEmail).SingleAsync();
            try
            {
                await gmail.ConnectAsync(activeWorkspace, code, pending.Verifier, expectedEmail);
                return Results.Redirect("/mailbox?connected=1");
            }
            catch (Exception ex) when (ex is InvalidOperationException or
                HttpRequestException or TaskCanceledException or JsonException)
            {
                // Never write OAuth codes, refresh tokens or provider responses to a URL or logs.
                return Results.Redirect("/mailbox?error=connection");
            }
        }).RequireAuthorization();

        return app;
    }
}
