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

        app.MapGet("/account/login", (HttpContext ctx, IAntiforgery xsrf) =>
            Page(ctx, xsrf, "Sign in to OutreachFlow", "/account/login",
                "<label>Email <input name='email' type='email' autocomplete='username' required /></label>" +
                "<label>Password <input name='password' type='password' autocomplete='current-password' required /></label>",
                "Sign in")).AllowAnonymous();

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
            UserManager<ApplicationUser> users) =>
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
            var fields = "<form method='post' action='/account/switch-workspace'>" +
                HiddenToken(token) + choices + "<button>Switch workspace</button></form>" +
                "<h2>New business workspace</h2>" +
                "<form method='post' action='/account/create-workspace'>" +
                HiddenToken(token) + "<label>Business name <input name='name' maxlength='150' required /></label>" +
                "<button>Create workspace</button></form>";
            return Html("Your workspaces", fields, ctx);
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
                ServiceDescription = "", ServiceArea = "", TargetDescription = ""
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
        string title, string action, string fields, string button)
    {
        var token = xsrf.GetAndStoreTokens(ctx).RequestToken!;
        var error = ctx.Request.Query.ContainsKey("failed")
            ? "<p role='alert'>Sign-in failed. Check your credentials or wait before retrying.</p>" : "";
        return Html(title, error + "<form method='post' action='" + action + "'>" +
            HiddenToken(token) + fields + "<button>" + WebUtility.HtmlEncode(button) +
            "</button></form>", ctx);
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
