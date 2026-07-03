using System.Net.Http.Json;
using System.Text.Json;

namespace Crs.Web.Services;

/// <summary>
/// Service for connecting X accounts and fetching X feed data.
/// </summary>
public class XFeedService
{
    private readonly HttpClient _httpClient;
    private readonly AuthorizedApiClient _api;
    private readonly AuthService _authService;
    private readonly ILogger<XFeedService> _logger;
    private readonly DevelopmentDataStore? _developmentData;

    private static readonly JsonSerializerOptions JsonOptions = CrsJsonOptions.Default;

    public XFeedService(
        HttpClient httpClient,
        AuthService authService,
        ILogger<XFeedService> logger,
        DevelopmentDataStore? developmentData = null)
    {
        _httpClient = httpClient;
        _api = new AuthorizedApiClient(httpClient, authService);
        _authService = authService;
        _logger = logger;
        _developmentData = developmentData;
    }

    public async Task<string?> GetConnectUrlAsync(string? redirectUri = null)
    {
        try
        {
            if (_authService.CurrentState.IsDevelopmentLogin)
            {
                return null;
            }

            var requestUri = string.IsNullOrWhiteSpace(redirectUri)
                ? "/api/v1/x/connect-url"
                : $"/api/v1/x/connect-url?redirectUri={Uri.EscapeDataString(redirectUri)}";
            var response = await _api.SendAsync(() => _httpClient.GetAsync(requestUri));
            if (response == null)
            {
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch X connect URL: {StatusCode}", response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<XConnectUrlResponse>(JsonOptions);
            return payload?.AuthorizationUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching X connect URL");
            return null;
        }
    }

    public async Task<XCallbackResult> HandleCallbackAsync(string code, string state)
    {
        try
        {
            var request = new XCallbackRequest
            {
                Code = code,
                State = state
            };

            var response = await _api.SendAsync(() =>
                _httpClient.PostAsJsonAsync("/api/v1/x/callback", request, JsonOptions));
            if (response == null)
            {
                _logger.LogWarning("X callback failed: no response (auth may have expired during OAuth flow)");
                return new XCallbackResult
                {
                    Success = false,
                    ErrorMessage = "Your session expired during the X connection flow. Please sign in again and retry."
                };
            }
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("X callback API returned {StatusCode}", response.StatusCode);
                return new XCallbackResult
                {
                    Success = false,
                    ErrorMessage = ExtractErrorMessage(body)
                };
            }
            return new XCallbackResult { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling X callback");
            return new XCallbackResult
            {
                Success = false,
                ErrorMessage = "Unexpected error while finishing the X connection."
            };
        }
    }

    private static string ExtractErrorMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "Failed to connect to X. Please try again.";
        }

        try
        {
            var payload = JsonSerializer.Deserialize<ApiProblemResponse>(body, JsonOptions);
            if (!string.IsNullOrWhiteSpace(payload?.Detail))
            {
                return payload.Detail;
            }

            if (!string.IsNullOrWhiteSpace(payload?.Title))
            {
                return payload.Title;
            }
        }
        catch (JsonException)
        {
            // Fall back to the raw response body below.
        }

        return body.Length > 240 ? $"{body[..240]}..." : body;
    }

    public async Task<bool> DisconnectAsync()
    {
        try
        {
            if (_authService.CurrentState.IsDevelopmentLogin)
            {
                return true;
            }

            var response = await _api.SendAsync(() =>
                _httpClient.DeleteAsync("/api/v1/x/connection"));
            return response?.IsSuccessStatusCode ?? false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disconnecting X account");
            return false;
        }
    }

    public async Task<List<XFollowedAccountItem>> GetFollowedAccountsAsync(bool refresh = false)
    {
        try
        {
            if (_authService.CurrentState.IsDevelopmentLogin && _developmentData != null)
            {
                return _developmentData.GetFollowedAccounts();
            }

            var response = await _api.SendAsync(() =>
                _httpClient.GetAsync($"/api/v1/x/followed-accounts?refresh={refresh.ToString().ToLowerInvariant()}"));
            if (response == null)
            {
                return new List<XFollowedAccountItem>();
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch followed X accounts: {StatusCode}", response.StatusCode);
                return new List<XFollowedAccountItem>();
            }

            var payload = await response.Content.ReadFromJsonAsync<List<XFollowedAccountResponse>>(JsonOptions);
            if (payload == null)
            {
                return new List<XFollowedAccountItem>();
            }

            return payload.Select(p => new XFollowedAccountItem
            {
                Id = p.Id,
                XUserId = p.XUserId,
                Handle = p.Handle,
                DisplayName = p.DisplayName,
                ProfileImageUrl = p.ProfileImageUrl,
                IsSelected = p.IsSelected
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching followed X accounts");
            return new List<XFollowedAccountItem>();
        }
    }

    public async Task<List<XFollowedAccountItem>> UpdateSelectedAccountsAsync(List<Guid> followedAccountIds)
    {
        try
        {
            if (_authService.CurrentState.IsDevelopmentLogin && _developmentData != null)
            {
                return _developmentData.UpdateSelectedAccounts(followedAccountIds);
            }

            var request = new XSelectedAccountsRequest
            {
                FollowedAccountIds = followedAccountIds
            };

            var response = await _api.SendAsync(() =>
                _httpClient.PostAsJsonAsync("/api/v1/x/selected-accounts", request, JsonOptions));
            if (response == null)
            {
                return new List<XFollowedAccountItem>();
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to update selected X accounts: {StatusCode}", response.StatusCode);
                return new List<XFollowedAccountItem>();
            }

            var payload = await response.Content.ReadFromJsonAsync<List<XFollowedAccountResponse>>(JsonOptions);
            if (payload == null)
            {
                return new List<XFollowedAccountItem>();
            }

            return payload.Select(p => new XFollowedAccountItem
            {
                Id = p.Id,
                XUserId = p.XUserId,
                Handle = p.Handle,
                DisplayName = p.DisplayName,
                ProfileImageUrl = p.ProfileImageUrl,
                IsSelected = p.IsSelected
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating selected X accounts");
            return new List<XFollowedAccountItem>();
        }
    }

    public async Task<List<XPostItem>> GetPostsAsync(int limit = 30)
    {
        try
        {
            if (_authService.CurrentState.IsDevelopmentLogin && _developmentData != null)
            {
                return _developmentData.GetPosts(limit);
            }

            var response = await _api.SendAsync(() =>
                _httpClient.GetAsync($"/api/v1/x/posts?limit={limit}"));
            if (response == null)
            {
                return new List<XPostItem>();
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch X posts: {StatusCode}", response.StatusCode);
                return new List<XPostItem>();
            }

            var payload = await response.Content.ReadFromJsonAsync<List<XPostResponse>>(JsonOptions);
            if (payload == null)
            {
                return new List<XPostItem>();
            }

            return payload.Select(p => new XPostItem
            {
                Id = p.Id,
                PostId = p.PostId,
                Text = p.Text,
                Url = p.Url,
                PostCreatedAt = p.PostCreatedAt,
                AuthorHandle = p.AuthorHandle,
                AuthorName = p.AuthorName,
                AuthorProfileImageUrl = p.AuthorProfileImageUrl,
                MediaJson = p.MediaJson,
                LikeCount = p.LikeCount,
                ReplyCount = p.ReplyCount,
                RepostCount = p.RepostCount,
                QuoteCount = p.QuoteCount
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching X posts");
            return new List<XPostItem>();
        }
    }
}

public class XConnectUrlResponse
{
    public string AuthorizationUrl { get; set; } = string.Empty;
}

public class XCallbackRequest
{
    public string Code { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
}

public class XCallbackResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

public class XSelectedAccountsRequest
{
    public List<Guid> FollowedAccountIds { get; set; } = new();
}

public class XFollowedAccountResponse
{
    public Guid Id { get; set; }
    public string XUserId { get; set; } = string.Empty;
    public string Handle { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? ProfileImageUrl { get; set; }
    public bool IsSelected { get; set; }
}

public class XPostResponse
{
    public Guid Id { get; set; }
    public string PostId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTime PostCreatedAt { get; set; }
    public string AuthorHandle { get; set; } = string.Empty;
    public string? AuthorName { get; set; }
    public string? AuthorProfileImageUrl { get; set; }
    public string? MediaJson { get; set; }
    public int LikeCount { get; set; }
    public int ReplyCount { get; set; }
    public int RepostCount { get; set; }
    public int QuoteCount { get; set; }
}

public class XFollowedAccountItem
{
    public Guid Id { get; set; }
    public string XUserId { get; set; } = string.Empty;
    public string Handle { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? ProfileImageUrl { get; set; }
    public bool IsSelected { get; set; }
}

public class XPostItem
{
    public Guid Id { get; set; }
    public string PostId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTime PostCreatedAt { get; set; }
    public string AuthorHandle { get; set; } = string.Empty;
    public string? AuthorName { get; set; }
    public string? AuthorProfileImageUrl { get; set; }
    public string? MediaJson { get; set; }
    public int LikeCount { get; set; }
    public int ReplyCount { get; set; }
    public int RepostCount { get; set; }
    public int QuoteCount { get; set; }
}

public class ApiProblemResponse
{
    public string? Title { get; set; }
    public string? Detail { get; set; }
}
