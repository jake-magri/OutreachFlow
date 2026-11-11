using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OutreachFlow.Data;
using OutreachFlow.Models;

namespace OutreachFlow.Services;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapOutreachAccounts(this IEndpointRouteBuilder app)
    {
        app.MapGet("/account/setup", async (HttpContext ctx, IAntiforgery xsrf,
            UserManager<ApplicationUser> users) =>
        {
            if (await users.Users.AnyAsync()) return Results.Redirect("/account/login");
            return Page(ctx, xsrf, "Set up your owner account", "/account/setup",
                "<label>Email <input name='email' type='email' autocomplete='email' required /></label>" +
                "<label>Password (12+ characters) <input name='password' type='password' autocomplete='new-password' minlength='12' required /></label>",
                "Create owner account");
        }).AllowAnonymous();

        app.MapPost("/account/setup", async (HttpContext ctx, IAntiforgery xsrf,
            UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn,
            AccountDbContext accounts, IDbContextFactory<OutreachDbContext> factory) =>
        {
            await xsrf.ValidateRequestAsync(ctx);
            if (await users.Users.AnyAsync()) return Results.Conflict("Initial account already exists.");
            var form = await ctx.Request.ReadFormAsync();
            var email = form["email"].ToString().Trim();
            var password = form["password"].ToString();
            await using var db = await factory.CreateDbContextAsync();
            var workspaceId = await db.Workspaces.OrderBy(w => w.Id).Select(w => w.Id).FirstAsync();
            var user = new ApplicationUser { UserName = email, Email = email, ActiveWorkspaceId = workspaceId };
            var result = await users.CreateAsync(user, password);
            if (!result.Succeeded)
                return Results.BadRequest(string.Join("; ", result.Errors.Select(e => e.Description)));
            accounts.Memberships.Add(new WorkspaceMembership
                { UserId = user.Id, WorkspaceId = workspaceId, Role = "Owner" });
            await accounts.SaveChangesAsync();
            await signIn.SignInAsync(user, isPersistent: false);
            return Results.Redirect("/");
        }).AllowAnonymous();

        app.MapGet("/account/login", (HttpContext ctx, IAntiforgery xsrf,
            IConfiguration config) =>
        {
            var google = GoogleSignInEndpoints.Configured(config)
                ? "<p><a href='/account/google/login'>Continue with Google</a></p>" +
                  "<small>First link your Google account from Workspaces after signing in with your existing password.</small>"
                : "";
            var page = Page(ctx, xsrf, "Sign in to OutreachFlow", "/account/login",
                "<label>Email <input name='email' type='email' autocomplete='username' required /></label>" +
                "<label>Password <input name='password' type='password' autocomplete='current-password' required /></label>",
                "Sign in", google);
            return page;
        }).AllowAnonymous();

        app.MapPost("/account/login", async (HttpContext ctx, IAntiforgery xsrf,
            SignInManager<ApplicationUser> signIn) =>
        {
            await xsrf.ValidateRequestAsync(ctx);
            var form = await ctx.Request.ReadFormAsync();
            var email = form["email"].ToString().Trim();
            var result = await signIn.PasswordSignInAsync(email, form["password"].ToString(),
                isPersistent: false, lockoutOnFailure: true);
            return result.Succeeded
                ? Results.Redirect("/")
                : Results.Redirect("/account/login?failed=1");
        }).AllowAnonymous();

        app.MapGet("/account/logout", (HttpContext ctx, IAntiforgery xsrf) =>
            Page(ctx, xsrf, "Sign out", "/account/logout", "<p>End the current browser session?</p>", "Sign out"))
            .RequireAuthorization();
        app.MapPost("/account/logout", async (HttpContext ctx, IAntiforgery xsrf,
            SignInManager<ApplicationUser> signIn) =>
        {
            await xsrf.ValidateRequestAsync(ctx);
            await signIn.SignOutAsync();
            return Results.Redirect("/account/login");
        }).RequireAuthorization();

        app.MapGet("/account/workspaces", async (HttpContext ctx, IAntiforgery xsrf,
            AccountDbContext accounts, IDbContextFactory<OutreachDbContext> factory,
            UserManager<ApplicationUser> users, IConfiguration config) =>
        {
            var userId = users.GetUserId(ctx.User)!;
            var memberships = await accounts.Memberships.AsNoTracking()
                .Where(m => m.UserId == userId).ToListAsync();
            await using var db = await factory.CreateDbContextAsync();
            var ids = memberships.Select(m => m.WorkspaceId).ToArray();
            var workspaces = await db.Workspaces.AsNoTracking()
                .Where(w => ids.Contains(w.Id)).OrderBy(w => w.BusinessName).ToListAsync();
            var active = await users.Users.Where(u => u.Id == userId)
                .Select(u => u.ActiveWorkspaceId).SingleAsync();
            var choices = string.Join("", workspaces.Select(w =>
                "<label><input type='radio' name='workspaceId' value='" + w.Id + "' " +
                (w.Id == active ? "checked" : "") + " /> " +
                WebUtility.HtmlEncode(w.BusinessName) + "</label>"));
            var token = xsrf.GetAndStoreTokens(ctx).RequestToken!;
            var google = GoogleSignInEndpoints.Configured(config)
                ? "<h2>Google sign-in</h2>" +
                  "<p>Link your existing account to Google's stable account ID to sign in without a local password. Gmail access is requested separately.</p>" +
                  "<p><a href='/account/google/link'>Link Google sign-in</a></p>"
                : "";
            var fields = "<form method='post' action='/account/switch-workspace'>" +
                HiddenToken(token) + choices + "<button>Switch workspace</button></form>" +
                google +
                "<h2>New business workspace</h2>" +
                "<form method='post' action='/account/create-workspace'>" +
                HiddenToken(token) + "<label>Business name <input name='name' maxlength='150' required /></label>" +
                "<button>Create workspace</button></form>";
            var status = ctx.Request.Query["google"].ToString() switch
            {
                "linked" => "<p>Google sign-in linked successfully.</p>",
                "failed" => "<p role='alert'>Google linking failed; no changes were made.</p>",
                "alreadyused" => "<p role='alert'>Google account is already linked to another OutreachFlow user.</p>",
                "unavailable" => "<p role='alert'>Google sign-in isn't configured.</p>",
                _ => ""
            };
            return Html("Your workspaces", status + fields, ctx);
        }).RequireAuthorization();

        app.MapPost("/account/switch-workspace", async (HttpContext ctx, IAntiforgery xsrf,
            AccountDbContext accounts, UserManager<ApplicationUser> users) =>
        {
            await xsrf.ValidateRequestAsync(ctx);
            var form = await ctx.Request.ReadFormAsync();
            if (!int.TryParse(form["workspaceId"], out var id)) return Results.BadRequest("Invalid workspace.");
            var user = (await users.GetUserAsync(ctx.User))!;
            var allowed = await accounts.Memberships.AnyAsync(m => m.UserId == user.Id && m.WorkspaceId == id);
            if (!allowed) return Results.Forbid();
            user.ActiveWorkspaceId = id;
            var updated = await users.UpdateAsync(user);
            return updated.Succeeded ? Results.Redirect("/") : Results.BadRequest("Could not select workspace.");
        }).RequireAuthorization();

        app.MapPost("/account/create-workspace", async (HttpContext ctx, IAntiforgery xsrf,
            AccountDbContext accounts, UserManager<ApplicationUser> users,
            IDbContextFactory<OutreachDbContext> factory) =>
        {
            await xsrf.ValidateRequestAsync(ctx);
            var form = await ctx.Request.ReadFormAsync();
            var name = form["name"].ToString().Trim();
            if (name.Length is < 2 or > 150) return Results.BadRequest("Business name must be 2–150 characters.");
            var user = (await users.GetUserAsync(ctx.User))!;
            await using var db = await factory.CreateDbContextAsync();
            var workspace = new Workspace
            {
                BusinessName = name, SenderName = user.UserName ?? "",
                SenderEmail = user.Email ?? "", SenderPhone = "", PostalAddress = "",
                ServiceDescription = "", ServiceArea = "", TargetDescription = "",
                InitialSubjectTemplate = "Vendor registration inquiry",
                InitialBodyTemplate = "Hi [team],\\n\\nI represent [business] and would like to learn how to apply or register as a provider for [company]. Is there an official process or person I should contact?\\n\\nThanks,\\n[sender]",
                FollowupOneTemplate = "Hi, following up on the vendor registration process for [company]. Could you point me toward the correct contact or application?\\n\\nThanks,\\n[sender]",
                FollowupTwoTemplate = "Hi, one final question about how [business] can be considered as a provider for [company]. I would appreciate any direction.\\n\\nThanks,\\n[sender]"
            };
            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync();
            accounts.Memberships.Add(new WorkspaceMembership
                { UserId = user.Id, WorkspaceId = workspace.Id, Role = "Owner" });
            await accounts.SaveChangesAsync();
            user.ActiveWorkspaceId = workspace.Id;
            var updated = await users.UpdateAsync(user);
            return updated.Succeeded ? Results.Redirect("/") : Results.BadRequest("Workspace created; selection failed.");
        }).RequireAuthorization();

        return app;
    }

    private static string HiddenToken(string token) =>
        "<input type='hidden' name='__RequestVerificationToken' value='" +
        WebUtility.HtmlEncode(token) + "' />";

    private static IResult Page(HttpContext ctx, IAntiforgery xsrf,
        string title, string action, string fields, string button,
        string extraContent = "")
    {
        var token = xsrf.GetAndStoreTokens(ctx).RequestToken!;
        var error = ctx.Request.Query.ContainsKey("failed")
            ? "<p role='alert'>Sign-in failed. Check your credentials or wait before retrying.</p>" : "";
        var googleStatus = ctx.Request.Query["google"].ToString() switch
        {
            "notlinked" => "<p role='alert'>This Google account is not linked. Sign in with your existing password, then link it under Workspaces.</p>",
            "failed" => "<p role='alert'>Google sign-in or linking failed. Try again.</p>",
            "unavailable" => "<p role='alert'>Google sign-in has not been configured.</p>",
            "alreadyused" => "<p role='alert'>That Google account is already linked to another account.</p>",
            "locked" => "<p role='alert'>This account is locked. Try again later.</p>",
            "linked" => "<p>Google sign-in is linked. You can now use Continue with Google at login.</p>",
            _ => ""
        };
        return Html(title, error + googleStatus +
            "<form method='post' action='" + action + "'>" +
            HiddenToken(token) + fields + "<button>" + WebUtility.HtmlEncode(button) +
            "</button></form>" + extraContent, ctx);
    }

    private static IResult Html(string title, string body, HttpContext ctx) =>
        Results.Content("<!doctype html><html lang='en'><head><meta charset='utf-8' />" +
            "<meta name='viewport' content='width=device-width,initial-scale=1' />" +
            "<title>" + WebUtility.HtmlEncode(title) + " · OutreachFlow</title>" +
            "<style>body{font:16px system-ui;background:#f5f6f8;color:#172032;margin:0;padding:10vh 16px}" +
            "main{max-width:480px;margin:auto;background:white;padding:28px;border-radius:12px;" +
            "box-shadow:0 2px 14px #182b4520}label{display:block;margin:18px 0}input{display:block;" +
            "box-sizing:border-box;width:100%;padding:10px;margin-top:6px}input[type=radio]{display:inline;width:auto}" +
            "button{background:#1d4f91;color:white;padding:12px 20px;border:0;border-radius:6px;cursor:pointer}" +
            "a{color:#1d4f91}form{margin-bottom:24px}</style></head><body><main><h1>" +
            WebUtility.HtmlEncode(title) + "</h1>" + body +
            "<p><a href='/'>Dashboard</a></p></main></body></html>", "text/html; charset=utf-8");
}
