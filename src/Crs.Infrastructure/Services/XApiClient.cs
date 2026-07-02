using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using Crs.Core.Interfaces;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Crs.Core.Models;
using Crs.Core.Observability;
using Crs.Infrastructure.Configuration;

namespace Crs.Infrastructure.Services;

/// <summary>
/// HTTP client for X API operations.
/// </summary>
public class XApiClient : IXApiClient
{
    private readonly HttpClient _httpClient;
    private readonly XApiSettings _settings;
    private readonly ILogger<XApiClient> _logger;
    private readonly IObservabilityMetrics _metrics;
    private readonly IHostEnvironment _environment;
    private readonly ObservabilitySettings _observabilitySettings;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public XApiClient(
        HttpClient httpClient,
        IOptions<XApiSettings> options,
        ILogger<XApiClient> logger,
        IObservabilityMetrics metrics,
        IHostEnvironment environment,
        IOptions<ObservabilitySettings> observabilityOptions)
    {
        _httpClient = httpClient;
        _settings = options.Value;
        _logger = logger;
        _metrics = metrics;
        _environment = environment;
        _observabilitySettings = observabilityOptions.Value;
    }

    public async Task<XTokenResponse> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["client_id"] = _settings.ClientId,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.TokenUrl)
        {
            Content = new FormUrlEncodedContent(form)
        };

        AddClientAuthHeader(request);

        var response = await SendAsync(request, "exchange authorization code", cancellationToken);
        await EnsureSuccessAsync(response, request, cancellationToken);

        var token = await response.Content.ReadFromJsonAsync<XTokenApiResponse>(JsonOptions, cancellationToken);
        return token == null
            ? new XTokenResponse()
            : new XTokenResponse
            {
                AccessToken = token.AccessToken,
                RefreshToken = token.RefreshToken,
                ExpiresIn = token.ExpiresIn,
                Scope = token.Scope,
                TokenType = token.TokenType
            };
    }

    public async Task<XTokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
            ["client_id"] = _settings.ClientId
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.TokenUrl)
        {
            Content = new FormUrlEncodedContent(form)
        };

        AddClientAuthHeader(request);

        var response = await SendAsync(request, "refresh access token", cancellationToken);
        await EnsureSuccessAsync(response, request, cancellationToken);

        var token = await response.Content.ReadFromJsonAsync<XTokenApiResponse>(JsonOptions, cancellationToken);
        return token == null
            ? new XTokenResponse()
            : new XTokenResponse
            {
                AccessToken = token.AccessToken,
                RefreshToken = token.RefreshToken,
                ExpiresIn = token.ExpiresIn,
                Scope = token.Scope,
                TokenType = token.TokenType
            };
    }

    public async Task<XUserProfile> GetCurrentUserAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        var payload = await GetCurrentUserPayloadAsync(
            $"{_settings.BaseUrl}/2/users/me?user.fields=profile_image_url",
            accessToken,
            cancellationToken);
        if (payload?.Data == null)
        {
            return new XUserProfile();
        }

        return new XUserProfile
        {
            XUserId = payload.Data.Id,
            Handle = payload.Data.Username,
            DisplayName = payload.Data.Name,
            ProfileImageUrl = payload.Data.ProfileImageUrl
        };
    }

    private async Task<XUserResponse?> GetCurrentUserPayloadAsync(string requestUri, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await SendAsync(request, "get current user profile", cancellationToken);
        if (!response.IsSuccessStatusCode &&
            response.StatusCode == System.Net.HttpStatusCode.Forbidden &&
            requestUri.Contains("user.fields=profile_image_url", StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "X denied profile lookup with optional user fields. Retrying without user.fields for {RequestUri}",
                request.RequestUri);
            response.Dispose();
            using var fallbackRequest = new HttpRequestMessage(HttpMethod.Get, $"{_settings.BaseUrl}/2/users/me");
            fallbackRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var fallbackResponse = await SendAsync(fallbackRequest, "get current user profile fallback", cancellationToken);
            await EnsureSuccessAsync(fallbackResponse, fallbackRequest, cancellationToken);
            return await fallbackResponse.Content.ReadFromJsonAsync<XUserResponse>(JsonOptions, cancellationToken);
        }

        await EnsureSuccessAsync(response, request, cancellationToken);
        return await response.Content.ReadFromJsonAsync<XUserResponse>(JsonOptions, cancellationToken);
    }

    public async Task<List<XFollowedAccountInfo>> GetFollowedAccountsAsync(string accessToken, string userId, CancellationToken cancellationToken = default)
    {
        var results = new List<XFollowedAccountInfo>();
        string? nextToken = null;

        do
        {
            var url = $"{_settings.BaseUrl}/2/users/{userId}/following?max_results=1000&user.fields=profile_image_url";
            if (!string.IsNullOrEmpty(nextToken))
            {
                url += $"&pagination_token={Uri.EscapeDataString(nextToken)}";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await SendAsync(request, "get followed accounts", cancellationToken);
            await EnsureSuccessAsync(response, request, cancellationToken);

            var payload = await response.Content.ReadFromJsonAsync<XFollowResponse>(JsonOptions, cancellationToken);
            if (payload?.Data != null)
            {
                results.AddRange(payload.Data.Select(item => new XFollowedAccountInfo
                {
                    XUserId = item.Id,
                    Handle = item.Username,
                    DisplayName = item.Name,
                    ProfileImageUrl = item.ProfileImageUrl
                }));
            }

            nextToken = payload?.Meta?.NextToken;
        }
        while (!string.IsNullOrEmpty(nextToken));

        return results;
    }

    public async Task<List<XPostInfo>> GetRecentPostsAsync(string accessToken, string userId, DateTime? since, CancellationToken cancellationToken = default)
    {
        var posts = new List<XPostInfo>();
        string? nextToken = null;

        do
        {
            var url = $"{_settings.BaseUrl}/2/users/{userId}/tweets"
                + "?max_results=100"
                + "&tweet.fields=created_at,public_metrics,attachments"
                + "&expansions=attachments.media_keys,author_id"
                + "&media.fields=preview_image_url,url,type"
                + "&user.fields=profile_image_url";

            if (since.HasValue)
            {
                var startTime = since.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
                url += $"&start_time={Uri.EscapeDataString(startTime)}";
            }

            if (!string.IsNullOrEmpty(nextToken))
            {
                url += $"&pagination_token={Uri.EscapeDataString(nextToken)}";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await SendAsync(request, "get recent posts", cancellationToken);
            await EnsureSuccessAsync(response, request, cancellationToken);

            var payload = await response.Content.ReadFromJsonAsync<XPostResponse>(JsonOptions, cancellationToken);
            if (payload?.Data != null)
            {
                var mediaByKey = payload.Includes?.Media?.ToDictionary(m => m.MediaKey, StringComparer.Ordinal) ?? new Dictionary<string, XMedia>();
                var usersById = payload.Includes?.Users?.ToDictionary(u => u.Id, StringComparer.Ordinal) ?? new Dictionary<string, XUser>();

                foreach (var item in payload.Data)
                {
                    var author = usersById.TryGetValue(item.AuthorId ?? string.Empty, out var user)
                        ? new XUserProfile
                        {
                            XUserId = user.Id,
                            Handle = user.Username,
                            DisplayName = user.Name,
                            ProfileImageUrl = user.ProfileImageUrl
                        }
                        : new XUserProfile { XUserId = item.AuthorId ?? string.Empty };

                    var media = new List<XMediaInfo>();
                    if (item.Attachments?.MediaKeys != null)
                    {
                        foreach (var key in item.Attachments.MediaKeys)
                        {
                            if (mediaByKey.TryGetValue(key, out var mediaItem))
                            {
                                media.Add(new XMediaInfo
                                {
                                    Type = mediaItem.Type ?? string.Empty,
                                    Url = mediaItem.Url,
                                    PreviewImageUrl = mediaItem.PreviewImageUrl
                                });
                            }
                        }
                    }

                    posts.Add(new XPostInfo
                    {
                        PostId = item.Id,
                        Text = item.Text ?? string.Empty,
                        Url = $"https://x.com/{author.Handle}/status/{item.Id}",
                        CreatedAt = item.CreatedAt ?? DateTime.UtcNow,
                        Author = author,
                        LikeCount = item.PublicMetrics?.LikeCount ?? 0,
                        ReplyCount = item.PublicMetrics?.ReplyCount ?? 0,
                        RepostCount = item.PublicMetrics?.RepostCount ?? 0,
                        QuoteCount = item.PublicMetrics?.QuoteCount ?? 0,
                        Media = media
                    });
                }
            }

            nextToken = payload?.Meta?.NextToken;
        }
        while (!string.IsNullOrEmpty(nextToken));

        return posts;
    }

    private void AddClientAuthHeader(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_settings.ClientSecret))
        {
            var credentials = $"{_settings.ClientId}:{_settings.ClientSecret}";
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", base64);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        string operation,
        CancellationToken cancellationToken)
    {
        using var activity = CrsTelemetry.ActivitySource.StartActivity("xapi.request");
        activity?.SetTag(CrsTelemetry.Tags.Dependency, "x");
        activity?.SetTag("x.operation", operation);
        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation(
            "Sending X API request for {Operation}: {Method} {RequestUri} using {AuthScheme} auth",
            operation,
            request.Method,
            request.RequestUri,
            request.Headers.Authorization?.Scheme ?? "None");

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            stopwatch.Stop();
            RecordMetric(operation, "success", stopwatch.Elapsed);
            _logger.LogInformation(
                "X API request succeeded for {Operation}: {StatusCode} {Method} {RequestUri}",
                operation,
                (int)response.StatusCode,
                request.Method,
                request.RequestUri);

            return response;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        stopwatch.Stop();
        RecordMetric(operation, "failed", stopwatch.Elapsed);
        _logger.LogWarning(
            "X API request failed for {Operation}: {StatusCode} {ReasonPhrase} on {Method} {RequestUri}. Response headers: {ResponseHeaders}. Body: {ResponseBody}",
            operation,
            (int)response.StatusCode,
            response.ReasonPhrase,
            request.Method,
            request.RequestUri,
            FormatResponseHeaders(response),
            FormatResponseBody(body));

        return response;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"X API request failed with {(int)response.StatusCode} {response.ReasonPhrase} " +
            $"for {request.Method} {request.RequestUri}. " +
            $"Headers: {FormatResponseHeaders(response)}. Body: {body}",
            inner: null,
            statusCode: response.StatusCode);
    }

    private static string FormatResponseHeaders(HttpResponseMessage response)
    {
        var interestingHeaders = new[]
        {
            "x-request-id",
            "x-client-trace-id",
            "x-rate-limit-limit",
            "x-rate-limit-remaining",
            "x-rate-limit-reset",
            "content-type",
            "date"
        };

        var values = new List<string>();
        foreach (var headerName in interestingHeaders)
        {
            if (TryGetHeaderValues(response, headerName, out var headerValues))
            {
                values.Add($"{headerName}={string.Join(",", headerValues)}");
            }
        }

        return values.Count == 0 ? "<none>" : string.Join("; ", values);
    }

    private static bool TryGetHeaderValues(HttpResponseMessage response, string headerName, out IEnumerable<string> values)
    {
        if (response.Headers.TryGetValues(headerName, out var responseHeaderValues))
        {
            values = responseHeaderValues;
            return true;
        }

        if (response.Content.Headers.TryGetValues(headerName, out var contentHeaderValues))
        {
            values = contentHeaderValues;
            return true;
        }

        values = Array.Empty<string>();
        return false;
    }

    private void RecordMetric(string operation, string outcome, TimeSpan duration)
    {
        DependencyMetrics.RecordCall(_metrics, "X", operation, outcome, duration);
    }

    private string FormatResponseBody(string body)
    {
        var allowFullBody = _observabilitySettings.EnableSensitiveBodyLogging || _environment.IsDevelopment();
        return ResponseBodyFormatter.Format(body, allowFullBody);
    }

    private class XTokenApiResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;
        [JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; } = string.Empty;
        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
        [JsonPropertyName("scope")]
        public string Scope { get; set; } = string.Empty;
        [JsonPropertyName("token_type")]
        public string TokenType { get; set; } = string.Empty;
    }

    private class XUserResponse
    {
        public XUser? Data { get; set; }
    }

    private class XFollowResponse
    {
        public List<XUser>? Data { get; set; }
        public XMeta? Meta { get; set; }
    }

    private class XPostResponse
    {
        public List<XPost>? Data { get; set; }
        public XPostIncludes? Includes { get; set; }
        public XMeta? Meta { get; set; }
    }

    private class XPostIncludes
    {
        public List<XMedia>? Media { get; set; }
        public List<XUser>? Users { get; set; }
    }

    private class XMeta
    {
        [JsonPropertyName("next_token")]
        public string? NextToken { get; set; }
    }

    private class XUser
    {
        public string Id { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string? Name { get; set; }
        [JsonPropertyName("profile_image_url")]
        public string? ProfileImageUrl { get; set; }
    }

    private class XPost
    {
        public string Id { get; set; } = string.Empty;
        public string? Text { get; set; }
        [JsonPropertyName("created_at")]
        public DateTime? CreatedAt { get; set; }
        [JsonPropertyName("author_id")]
        public string? AuthorId { get; set; }
        [JsonPropertyName("public_metrics")]
        public XPublicMetrics? PublicMetrics { get; set; }
        public XAttachments? Attachments { get; set; }
    }

    private class XPublicMetrics
    {
        [JsonPropertyName("like_count")]
        public int LikeCount { get; set; }
        [JsonPropertyName("reply_count")]
        public int ReplyCount { get; set; }
        [JsonPropertyName("retweet_count")]
        public int RepostCount { get; set; }
        [JsonPropertyName("quote_count")]
        public int QuoteCount { get; set; }
    }

    private class XAttachments
    {
        [JsonPropertyName("media_keys")]
        public List<string>? MediaKeys { get; set; }
    }

    private class XMedia
    {
        [JsonPropertyName("media_key")]
        public string MediaKey { get; set; } = string.Empty;
        public string? Type { get; set; }
        public string? Url { get; set; }
        [JsonPropertyName("preview_image_url")]
        public string? PreviewImageUrl { get; set; }
    }
}
