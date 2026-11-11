using System.Net.Mail;
using System.Security.Claims;
using OutreachFlow.Models;

namespace OutreachFlow.Services;

/// <summary>
/// Google verifies authentication; we provision only if its userinfo explicitly
/// reports a verified email. Its stable subject (not email) links future logins.
/// </summary>
public static class GoogleSignupPolicy
{
    public static string? VerifiedEmail(ClaimsPrincipal? principal)
    {
        if (principal is null) return null;
        var verified = principal.FindFirst("google_email_verified")?.Value ??
            principal.FindFirst("google_verified_email")?.Value;
        if (!string.Equals(verified, "true", StringComparison.OrdinalIgnoreCase))
            return null;
        var email = principal.FindFirstValue(ClaimTypes.Email)?.Trim();
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254 ||
            !MailAddress.TryCreate(email, out var parsed) ||
            !string.Equals(parsed.Address, email, StringComparison.OrdinalIgnoreCase))
            return null;
        return email;
    }

    public static Workspace NewWorkspace(string verifiedEmail) => new()
    {
        BusinessName = "New workspace",
        // The business name, sender identity and mailing address must be
        // consciously configured before commercial outreach.
        SenderEmail = verifiedEmail,
        SenderName = "",
        SenderPhone = "",
        PostalAddress = "",
        ServiceDescription = "",
        ServiceArea = "",
        TargetDescription = ""
    };
}
