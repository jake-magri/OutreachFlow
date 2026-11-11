using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OutreachFlow.Data;
using OutreachFlow.Models;

namespace OutreachFlow.Services;

public sealed record GoogleRegistrationResult(ApplicationUser? User, string Status);

/// <summary>
/// First-time Google account creation. A new Google subject gets its own account,
/// workspace and Owner membership. No email-based linking or reused default workspace.
/// </summary>
public sealed class GoogleUserProvisioner(
    UserManager<ApplicationUser> users,
    AccountDbContext accounts,
    IDbContextFactory<OutreachDbContext> businessFactory)
{
    public async Task<GoogleRegistrationResult> RegisterAsync(ExternalLoginInfo external)
    {
        if (external.LoginProvider != GoogleSignInEndpoints.Provider ||
            string.IsNullOrWhiteSpace(external.ProviderKey))
            return new(null, "failed");

        var email = GoogleSignupPolicy.VerifiedEmail(external.Principal);
        if (email is null)
            return new(null, "unverified");

        // The owner of an existing password-based identity must explicitly link it
        // while signed in. An identical Google email is NOT proof of ownership.
        if (await users.FindByEmailAsync(email) is not null ||
            await users.FindByNameAsync(email) is not null)
            return new(null, "notlinked");

        // Identity UserManager uses a separate scoped EF context. The auth and
        // business SQLite files cannot participate in one atomic transaction.
        // Roll back partial provisioning explicitly on any failed step.
        ApplicationUser? newUser = null;
        int? newWorkspaceId = null;
        try
        {
            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true
            };
            var created = await users.CreateAsync(user);
            if (!created.Succeeded)
                return new(null, "failed");
            newUser = user;

            var linked = await users.AddLoginAsync(user, external);
            if (!linked.Succeeded)
                throw new InvalidOperationException("Could not link newly created Google account.");

            await using (var db = await businessFactory.CreateDbContextAsync())
            {
                var workspace = GoogleSignupPolicy.NewWorkspace(email);
                db.Workspaces.Add(workspace);
                await db.SaveChangesAsync();
                newWorkspaceId = workspace.Id;
            }

            // Workspaces are deliberately owned only by the newly created user.
            accounts.Memberships.Add(new WorkspaceMembership
            {
                UserId = user.Id,
                WorkspaceId = newWorkspaceId.Value,
                Role = "Owner"
            });
            await accounts.SaveChangesAsync();
            user.ActiveWorkspaceId = newWorkspaceId.Value;
            var changed = await users.UpdateAsync(user);
            if (!changed.Succeeded)
                throw new InvalidOperationException("Could not activate new Google workspace.");

            return new(user, "created");
        }
        catch
        {
            // Compensation is best-effort, with an explicit error if it fails.
            // Never log or sign in a partially provisioned identity. Memberships
            // are not configured as EF user dependents; remove them explicitly.
            if (newUser is not null)
            {
                await accounts.Memberships
                    .Where(m => m.UserId == newUser.Id).ExecuteDeleteAsync();
                var linkedUser = await users.FindByIdAsync(newUser.Id);
                if (linkedUser is not null)
                {
                    var deleted = await users.DeleteAsync(linkedUser);
                    if (!deleted.Succeeded)
                        throw new InvalidOperationException(
                            "Google signup incomplete and identity cleanup failed; administrator repair required.");
                }
            }
            if (newWorkspaceId is int workspaceId)
            {
                await using var db = await businessFactory.CreateDbContextAsync();
                var orphan = await db.Workspaces.SingleOrDefaultAsync(w => w.Id == workspaceId);
                if (orphan is not null)
                {
                    db.Workspaces.Remove(orphan);
                    await db.SaveChangesAsync();
                }
            }
            throw;
        }
    }
}
