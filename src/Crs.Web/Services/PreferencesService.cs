using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Crs.Core.Enums;

namespace Crs.Web.Services;

public class PreferencesService
{
    private readonly HttpClient _httpClient;
    private readonly AuthService _authService;
    private readonly ILogger<PreferencesService> _logger;
    private readonly DevelopmentDataStore? _developmentData;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public PreferencesService(
        HttpClient httpClient,
        AuthService authService,
        ILogger<PreferencesService> logger,
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

    public async Task<List<PreferenceItem>> GetPreferencesAsync()
    {
        try
        {
            if (_authService.CurrentState.IsDevelopmentLogin && _developmentData != null)
            {
                return _developmentData.GetPreferences();
            }

            var response = await SendAuthorizedAsync(() => _httpClient.GetAsync("/api/v1/preferences"));
            if (response == null || !response.IsSuccessStatusCode)
            {
                return new List<PreferenceItem>();
            }

            var preferences = await response.Content.ReadFromJsonAsync<List<PreferenceItem>>(JsonOptions);
            return preferences ?? new List<PreferenceItem>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching preferences");
            return new List<PreferenceItem>();
        }
    }

    public async Task<PreferenceItem?> CreatePreferenceAsync(PreferenceUpsertRequest request)
    {
        try
        {
            if (_authService.CurrentState.IsDevelopmentLogin && _developmentData != null)
            {
                return _developmentData.CreatePreference(request);
            }

            var response = await SendAuthorizedAsync(() =>
                _httpClient.PostAsJsonAsync("/api/v1/preferences", request, JsonOptions));
            if (response == null || !response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<PreferenceItem>(JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating preference");
            return null;
        }
    }

    public async Task<PreferenceItem?> UpdatePreferenceAsync(Guid id, PreferenceUpsertRequest request)
    {
        try
        {
            if (_authService.CurrentState.IsDevelopmentLogin && _developmentData != null)
            {
                return _developmentData.UpdatePreference(id, request);
            }

            var response = await SendAuthorizedAsync(() =>
                _httpClient.PutAsJsonAsync($"/api/v1/preferences/{id}", request, JsonOptions));
            if (response == null || !response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<PreferenceItem>(JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating preference {PreferenceId}", id);
            return null;
        }
    }

    public async Task<bool> DeletePreferenceAsync(Guid id)
    {
        try
        {
            if (_authService.CurrentState.IsDevelopmentLogin && _developmentData != null)
            {
                return _developmentData.DeletePreference(id);
            }

            var response = await SendAuthorizedAsync(() => _httpClient.DeleteAsync($"/api/v1/preferences/{id}"));
            return response != null && response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting preference {PreferenceId}", id);
            return false;
        }
    }
}

public class PreferenceItem
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Url { get; set; }
    public ContentType? ContentType { get; set; }
    public VoteType VoteType { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class PreferenceUpsertRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Url { get; set; }
    public ContentType? ContentType { get; set; }
    public VoteType VoteType { get; set; }
}
