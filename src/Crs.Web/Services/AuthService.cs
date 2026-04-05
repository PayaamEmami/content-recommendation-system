using System.Net.Http.Json;
using Blazored.LocalStorage;
using Microsoft.Extensions.Configuration;

namespace Crs.Web.Services;

/// <summary>
/// Authentication service that integrates with the CRS API.
/// Uses localStorage for token persistence in WebAssembly.
/// </summary>
public class AuthService
{
    private const string AuthStateKey = "crs_auth_state";

    private AuthState _currentState = new();
    private readonly HttpClient _httpClient;
    private readonly ILocalStorageService _localStorage;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthService> _logger;
    private bool _isInitialized;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public event Action? OnAuthStateChanged;

    public AuthState CurrentState => _currentState;

    public bool IsRegistrationEnabled =>
        _configuration.GetValue<bool>("Registration:Enabled", true);

    public string RegistrationDisabledMessage =>
        _configuration.GetValue<string>("Registration:DisabledMessage",
            "New account registrations are currently closed. Please check back later.") ?? string.Empty;

    public AuthService(
        HttpClient httpClient,
        ILocalStorageService localStorage,
        IConfiguration configuration,
        ILogger<AuthService> logger)
    {
        _httpClient = httpClient;
        _localStorage = localStorage;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Initialize auth state from localStorage. Call this on app startup.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        try
        {
            var storedState = await _localStorage.GetItemAsync<AuthState>(AuthStateKey);
            if (storedState?.IsAuthenticated == true && !string.IsNullOrEmpty(storedState.AccessToken))
            {
                _currentState = storedState;
                _logger.LogInformation("Restored auth state from storage");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore auth state from storage");
        }

        _isInitialized = true;
    }

    public async Task<bool> EnsureAuthenticatedAsync()
    {
        await InitializeAsync();

        if (!_currentState.IsAuthenticated)
        {
            return false;
        }

        if (!HasValidAccessToken())
        {
            var refreshed = await TryRefreshAsync();
            if (!refreshed)
            {
                await LogoutAsync();
                return false;
            }
        }

        return true;
    }

    public async Task<AuthResult> SignUpAsync(string email, string password, string? displayName)
    {
        try
        {
            // Check if registrations are enabled
            if (!IsRegistrationEnabled)
                return new AuthResult { Success = false, ErrorMessage = RegistrationDisabledMessage };

            var request = new
            {
                email = email,
                password = password,
                displayName = displayName
            };

            var response = await _httpClient.PostAsJsonAsync("/api/v1/auth/register", request);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<LoginResponse>();
                if (result != null)
                {
                    _currentState = new AuthState
                    {
                        IsAuthenticated = true,
                        UserId = result.User.Id,
                        Email = result.User.Email,
                        DisplayName = result.User.DisplayName,
                        AccessToken = result.AccessToken,
                        RefreshToken = result.RefreshToken,
                        ExpiresAt = result.ExpiresAt
                    };

                    await PersistAuthStateAsync();
                    OnAuthStateChanged?.Invoke();
                    return new AuthResult { Success = true };
                }
            }

            _logger.LogWarning("Registration failed: {StatusCode}", response.StatusCode);

            return new AuthResult { Success = false, ErrorMessage = "Registration failed. Please try again." };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during registration");
            return new AuthResult { Success = false, ErrorMessage = "An error occurred. Please try again." };
        }
    }

    public async Task<AuthResult> LoginAsync(string email, string password)
    {
        try
        {
            var request = new
            {
                email = email,
                password = password
            };

            var response = await _httpClient.PostAsJsonAsync("/api/v1/auth/login", request);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<LoginResponse>();
                if (result != null)
                {
                    _currentState = new AuthState
                    {
                        IsAuthenticated = true,
                        UserId = result.User.Id,
                        Email = result.User.Email,
                        DisplayName = result.User.DisplayName,
                        AccessToken = result.AccessToken,
                        RefreshToken = result.RefreshToken,
                        ExpiresAt = result.ExpiresAt
                    };

                    await PersistAuthStateAsync();
                    OnAuthStateChanged?.Invoke();
                    return new AuthResult { Success = true };
                }
            }

            return new AuthResult { Success = false, ErrorMessage = "Invalid email or password" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during login");
            return new AuthResult { Success = false, ErrorMessage = "An error occurred. Please try again." };
        }
    }

    public async Task LogoutAsync()
    {
        _currentState = new AuthState();
        _httpClient.DefaultRequestHeaders.Authorization = null;
        await _localStorage.RemoveItemAsync(AuthStateKey);
        OnAuthStateChanged?.Invoke();
    }

    public async Task<bool> TryRefreshAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentState.RefreshToken))
        {
            return false;
        }

        await _refreshLock.WaitAsync();
        try
        {
            if (HasValidAccessToken())
            {
                return true;
            }

            var request = new { refreshToken = _currentState.RefreshToken };
            var response = await _httpClient.PostAsJsonAsync("/api/v1/auth/refresh", request);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Refresh token request failed: {StatusCode}", response.StatusCode);
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<RefreshTokenResponse>();
            if (result == null || string.IsNullOrEmpty(result.AccessToken))
            {
                return false;
            }

            _currentState.AccessToken = result.AccessToken;
            _currentState.RefreshToken = result.RefreshToken;
            _currentState.ExpiresAt = result.ExpiresAt;

            await PersistAuthStateAsync();
            OnAuthStateChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh access token");
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private bool HasValidAccessToken()
    {
        if (string.IsNullOrEmpty(_currentState.AccessToken))
        {
            return false;
        }

        if (_currentState.ExpiresAt == default)
        {
            return false;
        }

        return _currentState.ExpiresAt > DateTime.UtcNow.AddMinutes(1);
    }

    private async Task PersistAuthStateAsync()
    {
        try
        {
            await _localStorage.SetItemAsync(AuthStateKey, _currentState);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist auth state to storage");
        }
    }
}

public class AuthState
{
    public bool IsAuthenticated { get; set; }
    public Guid? UserId { get; set; }
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public class AuthResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

public class LoginResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public UserResponse User { get; set; } = null!;
}

public class RefreshTokenResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}

public class UserResponse
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}
