using Microsoft.AspNetCore.Identity;

namespace OutreachFlow.Models;

// Identity passwords are hashed by ASP.NET Core Identity; never store plaintext credentials.
public sealed class ApplicationUser : IdentityUser
{
    public int? ActiveWorkspaceId { get; set; }
}

public sealed class WorkspaceMembership
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public int WorkspaceId { get; set; }
    public string Role { get; set; } = "Member"; // Owner or Member
}
