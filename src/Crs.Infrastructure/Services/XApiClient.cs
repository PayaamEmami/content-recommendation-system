using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Crs.Core.Interfaces;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Crs.Core.Models;
using Crs.Core.Observability;
using Crs.Infrastructure.Configuration;
using Crs.Infrastructure.Services.XApi;
using Crs.Infrastructure.Services.XApi.Models;

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
        return XApiResponseMapper.MapToken(token);
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
        return XApiResponseMapper.MapToken(token);
    }

    public async Task<XUserProfile> GetCurrentUserAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        var payload = await GetCurrentUserPayloadAsync(
            $"{_settings.BaseUrl}/2/users/me?user.fields=profile_image_url",
            accessToken,
            cancellationToken);
        return payload?.Data == null
            ? new XUserProfile()
            : XApiResponseMapper.MapUserProfile(payload.Data);
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
                results.AddRange(payload.Data.Select(XApiResponseMapper.MapFollowedAccount));
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

            var payload = await response.Content.ReadFromJsonAsync<XPostsResponse>(JsonOptions, cancellationToken);
            if (payload?.Data != null)
            {
                var mediaByKey = payload.Includes?.Media?.ToDictionary(m => m.MediaKey, StringComparer.Ordinal) ?? new Dictionary<string, XMedia>();
                var usersById = payload.Includes?.Users?.ToDictionary(u => u.Id, StringComparer.Ordinal) ?? new Dictionary<string, XUser>();

                posts.AddRange(payload.Data.Select(item => XApiResponseMapper.MapPost(item, usersById, mediaByKey)));
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
}
