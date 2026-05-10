using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Crs.Core.Enums;

namespace Crs.Web.Services;

/// <summary>
/// Service for managing feeds and content that integrates with the CRS API.
/// </summary>
public class FeedService
{
    private readonly HttpClient _httpClient;
    private readonly AuthService _authService;
    private readonly ILogger<FeedService> _logger;
    private readonly DevelopmentDataStore? _developmentData;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public FeedService(
        HttpClient httpClient,
        AuthService authService,
        ILogger<FeedService> logger,
        DevelopmentDataStore? developmentData = null)
    {
        _httpClient = httpClient;
        _authService = authService;
        _logger = logger;
        _developmentData = developmentData;
    }

    private void SetAuthHeader()
    {
        if (!string.IsNullOrEmpty(_authService.CurrentState.AccessToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _authService.CurrentState.AccessToken);
        }
    }

    private async Task<HttpResponseMessage?> SendAuthorizedAsync(Func<Task<HttpResponseMessage>> requestFactory)
    {
        if (!await _authService.EnsureAuthenticatedAsync())
        {
            return null;
        }

        SetAuthHeader();
        var response = await requestFactory();

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var refreshed = await _authService.TryRefreshAsync();
            if (!refreshed)
            {
                await _authService.LogoutAsync();
                return response;
            }

            SetAuthHeader();
            response = await requestFactory();
        }

        return response;
    }

    public async Task<List<ContentItem>> GetFeedAsync(ContentType? type = null)
    {
        try
        {
            if (_authService.CurrentState.IsDevelopmentLogin && _developmentData != null)
            {
                return _developmentData.GetFeed(type);
            }

            var response = await SendAuthorizedAsync(() => _httpClient.GetAsync("/api/v1/recommendations"));
            if (response == null)
            {
                _logger.LogWarning("User not authenticated, cannot fetch feed");
                return new List<ContentItem>();
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch recommendations: {StatusCode}", response.StatusCode);
                return new List<ContentItem>();
            }

            var feedRecommendations = await response.Content.ReadFromJsonAsync<List<FeedRecommendationsResponse>>(JsonOptions);

            if (feedRecommendations == null || !feedRecommendations.Any())
            {
                _logger.LogInformation("No recommendations available");
                return new List<ContentItem>();
            }

            // Flatten all recommendations into a single list
            var content = new List<ContentItem>();

            foreach (var feed in feedRecommendations)
            {
                if (type.HasValue && feed.FeedType != type.Value)
                    continue;

                foreach (var rec in feed.Recommendations)
                {
                    content.Add(new ContentItem
                    {
                        Id = rec.Content.Id,
                        Title = rec.Content.Title,
                        Url = rec.Content.Url,
                        Type = rec.Content.Type,
                        Description = rec.Content.Description,
                        PublishedAt = rec.Content.PublishedDate ?? rec.Content.CreatedAt
                    });
                }
            }

            return content.OrderByDescending(r => r.PublishedAt).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching feed");
            return new List<ContentItem>();
        }
    }

    public async Task<List<VoteItem>> GetUserVotesAsync()
    {
        try
        {
            if (_authService.CurrentState.IsDevelopmentLogin && _developmentData != null)
            {
                return _developmentData.GetVotes();
            }

            var response = await SendAuthorizedAsync(() => _httpClient.GetAsync("/api/v1/users/me/votes"));
            if (response == null)
            {
                return new List<VoteItem>();
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch user votes: {StatusCode}", response.StatusCode);
                return new List<VoteItem>();
            }

            var votes = await response.Content.ReadFromJsonAsync<List<VoteItem>>(JsonOptions);
            return votes ?? new List<VoteItem>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching user votes");
            return new List<VoteItem>();
        }
    }

    public async Task<List<VoteHistoryItem>> GetVoteHistoryAsync()
    {
        try
        {
            if (_authService.CurrentState.IsDevelopmentLogin && _developmentData != null)
            {
                return _developmentData.GetVoteHistory();
            }

            var response = await SendAuthorizedAsync(() => _httpClient.GetAsync("/api/v1/users/me/vote-history"));
            if (response == null)
            {
                return new List<VoteHistoryItem>();
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch vote history: {StatusCode}", response.StatusCode);
                return new List<VoteHistoryItem>();
            }

            var history = await response.Content.ReadFromJsonAsync<List<VoteHistoryApiResponse>>(JsonOptions);
            return history?.Select(item => new VoteHistoryItem
            {
                Id = item.Id,
                UserId = item.UserId,
                ContentId = item.ContentId,
                VoteType = item.VoteType,
                VoteCreatedAt = item.CreatedAt,
                VoteUpdatedAt = item.UpdatedAt,
                Title = item.Title,
                Description = item.Description,
                Url = item.Url,
                Type = item.Type,
                ContentDate = item.ContentDate
            }).ToList() ?? new List<VoteHistoryItem>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching vote history");
            return new List<VoteHistoryItem>();
        }
    }

    public async Task<VoteItem?> VoteAsync(Guid contentId, VoteType voteType)
    {
        try
        {
            if (_authService.CurrentState.IsDevelopmentLogin && _developmentData != null)
            {
                return _developmentData.Vote(contentId, voteType);
            }

            var request = new { voteType = voteType };
            var response = await SendAuthorizedAsync(() =>
                _httpClient.PostAsJsonAsync($"/api/v1/content/{contentId}/vote", request, JsonOptions));
            if (response == null)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to vote on content {ContentId}: {StatusCode}", contentId, response.StatusCode);
                return null;
            }

            var vote = await response.Content.ReadFromJsonAsync<VoteItem>(JsonOptions);
            _logger.LogInformation("Successfully voted {VoteType} on content {ContentId}", voteType, contentId);
            return vote;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error voting on content {ContentId}", contentId);
            return null;
        }
    }

    public async Task<bool> RemoveVoteAsync(Guid contentId)
    {
        try
        {
            if (_authService.CurrentState.IsDevelopmentLogin && _developmentData != null)
            {
                return _developmentData.RemoveVote(contentId);
            }

            var response = await SendAuthorizedAsync(() =>
                _httpClient.DeleteAsync($"/api/v1/content/{contentId}/vote"));
            if (response == null)
            {
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to remove vote from content {ContentId}: {StatusCode}", contentId, response.StatusCode);
                return false;
            }

            _logger.LogInformation("Successfully removed vote from content {ContentId}", contentId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing vote from content {ContentId}", contentId);
            return false;
        }
    }
}

public class ContentItem
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public ContentType Type { get; set; }
    public string? Description { get; set; }
    public DateTime PublishedAt { get; set; }
}

public class VoteItem
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid ContentId { get; set; }
    public VoteType VoteType { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class VoteHistoryItem
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid ContentId { get; set; }
    public VoteType? VoteType { get; set; }
    public DateTime VoteCreatedAt { get; set; }
    public DateTime? VoteUpdatedAt { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Url { get; set; } = string.Empty;
    public ContentType Type { get; set; }
    public DateTime ContentDate { get; set; }
}

// API Response DTOs
public class FeedRecommendationsResponse
{
    public ContentType FeedType { get; set; }
    public DateOnly Date { get; set; }
    public List<RecommendationItemResponse> Recommendations { get; set; } = new();
}

public class RecommendationItemResponse
{
    public Guid Id { get; set; }
    public ContentItemResponse Content { get; set; } = null!;
    public int Position { get; set; }
    public double Score { get; set; }
    public DateTime GeneratedAt { get; set; }
}

public class ContentItemResponse
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTime? PublishedDate { get; set; }
    public ContentType Type { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class VoteHistoryApiResponse
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid ContentId { get; set; }
    public VoteType VoteType { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Url { get; set; } = string.Empty;
    public ContentType Type { get; set; }
    public DateTime ContentDate { get; set; }
}
