using Microsoft.Maui.Storage;
using System.Diagnostics;

namespace AIUsageMonitor.Services;

public class TokenStorageService
{
    private const string AccessTokenSuffix = "_access_token";
    private const string RefreshTokenSuffix = "_refresh_token";

    /// <summary>
    /// Saves the access and refresh tokens for a specific account without expiration information.
    /// </summary>
    public async Task SaveTokensAsync(string accountId, string? accessToken, string? refreshToken)
    {
        // Backward‑compatible overload – just stores tokens without expiration info
        await SaveTokensAsync(accountId, accessToken, refreshToken, null);
    }

    // New overload that also stores the token expiration timestamp (UTC)
    /// <summary>
    /// Saves the access and refresh tokens along with an optional token expiration duration in seconds.
    /// </summary>
    public async Task SaveTokensAsync(string accountId, string? accessToken, string? refreshToken, int? expiresInSeconds)
    {
        try
        {
            if (!string.IsNullOrEmpty(accessToken))
            {
                await SecureStorage.Default.SetAsync($"{accountId}{AccessTokenSuffix}", accessToken);
            }
            else
            {
                SecureStorage.Default.Remove($"{accountId}{AccessTokenSuffix}");
            }

            if (!string.IsNullOrEmpty(refreshToken))
            {
                await SecureStorage.Default.SetAsync($"{accountId}{RefreshTokenSuffix}", refreshToken);
            }
            else
            {
                SecureStorage.Default.Remove($"{accountId}{RefreshTokenSuffix}");
            }

            // Store expirations as an ISO‑8601 UTC string (if we have a value)
            if (expiresInSeconds.HasValue)
            {
                var expiresAt = DateTime.UtcNow.AddSeconds(expiresInSeconds.Value);
                await SecureStorage.Default.SetAsync($"{accountId}_expires_at", expiresAt.ToString("o"));
            }
            else
            {
                SecureStorage.Default.Remove($"{accountId}_expires_at");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save tokens", ex);
        }
    }

    // Returns (access, refresh, expiresAt) – expiresAt may be null if not stored
    /// <summary>
    /// Loads the access and refresh tokens, as well as the expiration time, for a specific account.
    /// </summary>
    public async Task<(string? AccessToken, string? RefreshToken, DateTime? ExpiresAt)> LoadTokensAsync(string accountId)
    {
        try
        {
            var accessToken = await SecureStorage.Default.GetAsync($"{accountId}{AccessTokenSuffix}");
            var refreshToken = await SecureStorage.Default.GetAsync($"{accountId}{RefreshTokenSuffix}");
            var expiresStr = await SecureStorage.Default.GetAsync($"{accountId}_expires_at");
            DateTime? expiresAt = null;
            if (!string.IsNullOrEmpty(expiresStr) && DateTime.TryParse(expiresStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                expiresAt = dt;
            return (accessToken, refreshToken, expiresAt);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load tokens", ex);
            return (null, null, null);
        }
    }

    /// <summary>
    /// Removes all stored tokens for a specific account.
    /// </summary>
    public void RemoveTokens(string accountId)
    {
        try
        {
            SecureStorage.Default.Remove($"{accountId}{AccessTokenSuffix}");
            SecureStorage.Default.Remove($"{accountId}{RefreshTokenSuffix}");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to remove tokens", ex);
        }
    }
}
