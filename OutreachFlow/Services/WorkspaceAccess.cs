using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OutreachFlow.Data;
using OutreachFlow.Models;

namespace OutreachFlow.Services;

public interface IWorkspaceAccess
{
    Task<int> ActiveWorkspaceIdAsync();
}

// Local test-only substitute; NEVER register this in the web application's DI container.
public sealed class FixedWorkspaceAccess(int workspaceId) : IWorkspaceAccess
{
    public Task<int> ActiveWorkspaceIdAsync() => Task.FromResult(workspaceId);
}

public sealed class AuthenticatedWorkspaceAccess(
    AuthenticationStateProvider authentication,
    IHttpContextAccessor http,
    AccountDbContext accounts) : IWorkspaceAccess
{
    public async Task<int> ActiveWorkspaceIdAsync()
    {
        var principal = http.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
            principal = (await authentication.GetAuthenticationStateAsync()).User;

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            throw new UnauthorizedAccessException("Sign in to access a workspace.");

        var id = await accounts.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.ActiveWorkspaceId)
            .SingleOrDefaultAsync();

        if (id is null || !await accounts.Memberships.AsNoTracking()
            .AnyAsync(m => m.UserId == userId && m.WorkspaceId == id.Value))
            throw new UnauthorizedAccessException("No active workspace membership. Contact the workspace owner.");

        return id.Value;
    }
}
