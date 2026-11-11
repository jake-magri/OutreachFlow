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

        await using var transaction = await accounts.Database.BeginTransactionAsync();
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

            var linked = await users.AddLoginAsync(user, external);
            if (!linked.Succeeded)
                return new(null, "failed");

            await using (var db = await businessFactory.CreateDbContextAsync())
            {
                var workspace = GoogleSignupPolicy.NewWorkspace(email);
                db.Workspaces.Add(workspace);
                await db.SaveChangesAsync();
                newWorkspaceId = workspace.Id;
            }

            // These writes share the AccountDbContext transaction with the
            // user and Google subject link.
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

            await transaction.CommitAsync();
            return new(user, "created");
        }
        catch
        {
            await transaction.RollbackAsync();
            // Business and auth SQLite files cannot share one atomic transaction.
            // A failed signup must not leave an orphaned business workspace.
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
