using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Crs.Core.Entities;
using Crs.Core.Interfaces;
using Crs.Infrastructure.Configuration;

namespace Crs.Api.Services;

/// <summary>
/// Service for handling X account connections and feed data.
/// </summary>
public class XAccountService : IXAccountService
{
    private static readonly string[] RequiredConnectionScopes = ["tweet.read", "users.read"];
    private static readonly string[] RequiredFollowScopes = ["tweet.read", "users.read", "follows.read"];

    private readonly IXConnectionRepository _connectionRepository;
    private readonly IXAuthStateRepository _authStateRepository;
    private readonly IXFollowedAccountRepository _followedAccountRepository;
    private readonly IXSelectedAccountRepository _selectedAccountRepository;
    private readonly IXPostRepository _postRepository;
    private readonly IXApiClient _xApiClient;
    private readonly XApiSettings _settings;
    private readonly IDataProtector _protector;
    private readonly ILogger<XAccountService> _logger;

    public XAccountService(
        IXConnectionRepository connectionRepository,
        IXAuthStateRepository authStateRepository,
        IXFollowedAccountRepository followedAccountRepository,
        IXSelectedAccountRepository selectedAccountRepository,
        IXPostRepository postRepository,
        IXApiClient xApiClient,
        IOptions<XApiSettings> settings,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<XAccountService> logger)
    {
        _connectionRepository = connectionRepository;
        _authStateRepository = authStateRepository;
        _followedAccountRepository = followedAccountRepository;
        _selectedAccountRepository = selectedAccountRepository;
        _postRepository = postRepository;
        _xApiClient = xApiClient;
        _settings = settings.Value;
        _protector = dataProtectionProvider.CreateProtector("Crs.X.Tokens");
        _logger = logger;
    }

    public async Task<string> CreateConnectUrlAsync(Guid userId, string? redirectUri = null, CancellationToken cancellationToken = default)
    {
        await _authStateRepository.RemoveExpiredAsync(cancellationToken);

        var state = CreateBase64Url(32);
        var codeVerifier = CreateBase64Url(64);
        var codeChallenge = CreateCodeChallenge(codeVerifier);
        var effectiveRedirectUri = !string.IsNullOrWhiteSpace(redirectUri)
            ? redirectUri
            : _settings.RedirectUri;

        if (string.IsNullOrWhiteSpace(effectiveRedirectUri))
        {
            throw new InvalidOperationException("X redirect URI is not configured");
        }

        await _authStateRepository.AddAsync(new XAuthState
        {
            State = state,
            UserId = userId,
            CodeVerifier = codeVerifier,
            RedirectUri = effectiveRedirectUri,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        }, cancellationToken);

        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = _settings.ClientId,
            ["redirect_uri"] = effectiveRedirectUri,
            ["scope"] = _settings.Scopes,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };

        var url = $"{_settings.AuthorizationUrl}?{ToQueryString(query)}";
        _logger.LogInformation("Created X connect URL for user {UserId}", userId);
        return url;
    }

    public async Task HandleCallbackAsync(Guid userId, string code, string state, CancellationToken cancellationToken = default)
    {
        await _authStateRepository.RemoveExpiredAsync(cancellationToken);

        _logger.LogInformation("Handling X callback for user {UserId}", userId);

        var authState = await _authStateRepository.GetAndRemoveAsync(state, cancellationToken);
        if (authState == null || authState.UserId != userId)
        {
            _logger.LogWarning("X callback rejected: invalid or expired state for user {UserId} (state found: {StateFound})", userId, authState != null);
            throw new InvalidOperationException("Invalid or expired X authorization state");
        }

        _logger.LogInformation("Exchanging X OAuth code for user {UserId}", userId);
        var token = await _xApiClient.ExchangeCodeAsync(code, authState.CodeVerifier, authState.RedirectUri, cancellationToken);
        if (!string.IsNullOrWhiteSpace(token.Scope))
        {
            _logger.LogInformation("X OAuth token scopes: {Scopes}", token.Scope);
        }

        EnsureRequiredScopes(token.Scope, RequiredConnectionScopes, "retrieve the authenticated user profile");
        _logger.LogInformation("Fetching current X profile for user {UserId}", userId);
        var profile = await _xApiClient.GetCurrentUserAsync(token.AccessToken, cancellationToken);

        var connection = new XConnection
        {
            UserId = userId,
            XUserId = profile.XUserId,
            Handle = profile.Handle,
            DisplayName = profile.DisplayName,
            AccessTokenEncrypted = Protect(token.AccessToken),
            RefreshTokenEncrypted = Protect(token.RefreshToken),
            TokenExpiresAt = DateTime.UtcNow.AddSeconds(token.ExpiresIn),
            Scopes = token.Scope
        };

        await _connectionRepository.UpsertAsync(connection, cancellationToken);
        _logger.LogInformation("Connected X account {Handle} for user {UserId}", profile.Handle, userId);
        await RefreshFollowedAccountsAsync(userId, cancellationToken);
    }

    public async Task DisconnectAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await _postRepository.DeleteByUserIdAsync(userId, cancellationToken);
        await _selectedAccountRepository.DeleteByUserIdAsync(userId, cancellationToken);
        await _followedAccountRepository.DeleteByUserIdAsync(userId, cancellationToken);
        await _connectionRepository.DeleteByUserIdAsync(userId, cancellationToken);
        _logger.LogInformation("Disconnected X account for user {UserId}", userId);
    }

    public Task<List<XFollowedAccount>> GetFollowedAccountsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return _followedAccountRepository.GetByUserIdAsync(userId, cancellationToken);
    }

    public Task<List<XSelectedAccount>> GetSelectedAccountsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return _selectedAccountRepository.GetByUserIdAsync(userId, cancellationToken);
    }

    public async Task<List<XFollowedAccount>> RefreshFollowedAccountsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var connection = await GetValidConnectionAsync(userId, cancellationToken);
        var accounts = await _xApiClient.GetFollowedAccountsAsync(connection.AccessToken, connection.XUserId, cancellationToken);

        _logger.LogInformation("Fetched {Count} followed X accounts for user {UserId}", accounts.Count, userId);

        var entities = accounts.Select(account => new XFollowedAccount
        {
            UserId = userId,
            XUserId = account.XUserId,
            Handle = account.Handle,
            DisplayName = account.DisplayName,
            ProfileImageUrl = account.ProfileImageUrl,
            FollowedAt = DateTime.UtcNow
        }).ToList();

        await _followedAccountRepository.ReplaceForUserAsync(userId, entities, cancellationToken);
        return await _followedAccountRepository.GetByUserIdAsync(userId, cancellationToken);
    }

    public async Task<List<XSelectedAccount>> UpdateSelectedAccountsAsync(Guid userId, List<Guid> followedAccountIds, CancellationToken cancellationToken = default)
    {
        var selected = followedAccountIds.Select(id => new XSelectedAccount
        {
            XFollowedAccountId = id,
            SelectedAt = DateTime.UtcNow
        }).ToList();

        await _selectedAccountRepository.ReplaceForUserAsync(userId, selected, cancellationToken);
        return await _selectedAccountRepository.GetByUserIdAsync(userId, cancellationToken);
    }

    public Task<List<XPost>> GetPostsAsync(Guid userId, int limit, CancellationToken cancellationToken = default)
    {
        return _postRepository.GetLatestByUserIdAsync(userId, limit, cancellationToken);
    }

    private async Task<ValidConnection> GetValidConnectionAsync(Guid userId, CancellationToken cancellationToken)
    {
        var connection = await _connectionRepository.GetByUserIdAsync(userId, cancellationToken);
        if (connection == null)
        {
            throw new InvalidOperationException("X account is not connected");
        }

        var accessToken = Unprotect(connection.AccessTokenEncrypted);
        var refreshToken = Unprotect(connection.RefreshTokenEncrypted);

        if (connection.TokenExpiresAt.HasValue && connection.TokenExpiresAt.Value <= DateTime.UtcNow.AddMinutes(1))
        {
            var refreshed = await _xApiClient.RefreshTokenAsync(refreshToken, cancellationToken);
            accessToken = refreshed.AccessToken;
            refreshToken = refreshed.RefreshToken;

            connection.AccessTokenEncrypted = Protect(accessToken);
            connection.RefreshTokenEncrypted = Protect(refreshToken);
            connection.TokenExpiresAt = DateTime.UtcNow.AddSeconds(refreshed.ExpiresIn);
            connection.Scopes = refreshed.Scope;

            await _connectionRepository.UpsertAsync(connection, cancellationToken);
        }

        EnsureRequiredScopes(connection.Scopes, RequiredFollowScopes, "read followed X accounts");
        return new ValidConnection
        {
            XUserId = connection.XUserId,
            AccessToken = accessToken
        };
    }

    private string Protect(string value)
    {
        return string.IsNullOrEmpty(value) ? string.Empty : _protector.Protect(value);
    }

    private string Unprotect(string value)
    {
        return string.IsNullOrEmpty(value) ? string.Empty : _protector.Unprotect(value);
    }

    private static string CreateBase64Url(int byteLength)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteLength);
        return Base64UrlEncode(bytes);
    }

    private static string CreateCodeChallenge(string codeVerifier)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", string.Empty);
    }

    private static string ToQueryString(Dictionary<string, string> parameters)
    {
        return string.Join("&", parameters.Select(kvp =>
            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
    }

    private void EnsureRequiredScopes(string? grantedScopes, IEnumerable<string> requiredScopes, string operation)
    {
        var granted = new HashSet<string>(
            (grantedScopes ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.Ordinal);
        var missing = requiredScopes.Where(scope => !granted.Contains(scope)).ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        var grantedDisplay = string.IsNullOrWhiteSpace(grantedScopes) ? "<none>" : grantedScopes;
        _logger.LogError(
            "X OAuth token is missing required scopes for {Operation}. Missing: {MissingScopes}. Granted: {GrantedScopes}",
            operation,
            string.Join(", ", missing),
            grantedDisplay);

        throw new InvalidOperationException(
            $"X OAuth token is missing required scope(s) for {operation}: {string.Join(", ", missing)}. " +
            $"Granted scopes: {grantedDisplay}. Update the X app scopes and reconnect the account.");
    }

    private class ValidConnection
    {
        public string XUserId { get; set; } = string.Empty;
        public string AccessToken { get; set; } = string.Empty;
    }
}
