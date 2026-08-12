namespace Warp.UI;

/// <summary>
/// Implement this interface to validate credentials for the built-in Warp login page.
/// Register in DI as scoped. Can inject DbContext or other services for async DB lookups.
/// </summary>
public interface IWarpCredentialValidator
{
    Task<bool> ValidateAsync(string username, string password);
}
