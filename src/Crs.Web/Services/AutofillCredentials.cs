namespace Crs.Web.Services;

/// <summary>
/// Resolves login field values when a password manager fills the DOM
/// without raising the events Blazor data-binding listens for.
/// </summary>
public static class AutofillCredentials
{
    public static (string Email, string Password) PreferDom(
        string modelEmail,
        string modelPassword,
        string? domEmail,
        string? domPassword)
    {
        var email = string.IsNullOrEmpty(domEmail) ? modelEmail : domEmail.Trim();
        var password = string.IsNullOrEmpty(domPassword) ? modelPassword : domPassword;
        return (email, password);
    }
}
