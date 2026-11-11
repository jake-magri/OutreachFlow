using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OutreachFlow.Data;
using OutreachFlow.Models;

namespace OutreachFlow.Services;

/// <summary>
/// Optional Google authentication for an existing account, based on Google's stable
/// subject identifier. Email address alone never links or grants workspace access.
/// </summary>
public static class GoogleSignInEndpoints
{
    public const string Provider = "Google";

    public static bool Configured(IConfiguration config) =>
        !string.IsNullOrWhiteSpace(config["GoogleAuth:ClientId"]) &&
        !string.IsNullOrWhiteSpace(config["GoogleAuth:ClientSecret"]);

    public static IEndpointRouteBuilder MapGoogleSignIn(this IEndpointRouteBuilder app)
    {
        app.MapGet("/account/google/login", (IConfiguration config, SignInManager<ApplicationUser> signIn) =>
        {
            if (!Configured(config)) return Results.Redirect("/account/login?google=unavailable");
            var properties = signIn.ConfigureExternalAuthenticationProperties(
                Provider, "/account/google/callback");
            return Results.Challenge(properties, [Provider]);
        }).AllowAnonymous();

        app.MapGet("/account/google/callback", async (HttpContext context,
            UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn) =>
        {
            var info = await signIn.GetExternalLoginInfoAsync();
            if (info is null || info.LoginProvider != Provider ||
                string.IsNullOrWhiteSpace(info.ProviderKey))
                return Results.Redirect("/account/login?google=failed");
            var user = await users.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
            await context.SignOutAsync(IdentityConstants.ExternalScheme);
            if (user is null)
            {
                // Never auto-link existing identities using an email claim. Nor
                // allow an unknown Google account to join the owner's workspace.
                return Results.Redirect("/account/login?google=notlinked");
            }
            if (await users.IsLockedOutAsync(user))
                return Results.Redirect("/account/login?google=locked");
            await signIn.SignInAsync(user, isPersistent: false, info.LoginProvider);
            return Results.Redirect("/");
        }).AllowAnonymous();

        // An authenticated local user explicitly connects a Google subject once.
        // The XSRF user ID embedded by SignInManager is validated at callback.
        app.MapGet("/account/google/link", async (HttpContext context,
            IConfiguration config, UserManager<ApplicationUser> users,
            SignInManager<ApplicationUser> signIn) =>
        {
            if (!Configured(config)) return Results.Redirect("/account/workspaces?google=unavailable");
            var user = await users.GetUserAsync(context.User);
            if (user is null) return Results.Unauthorized();
            var existing = await users.GetLoginsAsync(user);
            if (existing.Any(x => x.LoginProvider == Provider))
                return Results.Redirect("/account/workspaces?google=linked");
            var properties = signIn.ConfigureExternalAuthenticationProperties(
                Provider, "/account/google/link-callback", user.Id);
            return Results.Challenge(properties, [Provider]);
        }).RequireAuthorization();

        app.MapGet("/account/google/link-callback", async (HttpContext context,
            UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn) =>
        {
            var user = await users.GetUserAsync(context.User);
            if (user is null) return Results.Unauthorized();
            var info = await signIn.GetExternalLoginInfoAsync(user.Id);
            if (info is null || info.LoginProvider != Provider ||
                string.IsNullOrWhiteSpace(info.ProviderKey))
                return Results.Redirect("/account/workspaces?google=failed");
            var existingOwner = await users.FindByLoginAsync(Provider, info.ProviderKey);
            if (existingOwner is not null && existingOwner.Id != user.Id)
            {
                await context.SignOutAsync(IdentityConstants.ExternalScheme);
                return Results.Redirect("/account/workspaces?google=alreadyused");
            }
            var result = await users.AddLoginAsync(user, info);
            await context.SignOutAsync(IdentityConstants.ExternalScheme);
            return Results.Redirect(result.Succeeded
                ? "/account/workspaces?google=linked"
                : "/account/workspaces?google=failed");
        }).RequireAuthorization();

        return app;
    }
}
