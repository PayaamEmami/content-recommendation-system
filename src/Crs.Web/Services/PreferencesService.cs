using System.Net.Http.Json;
using System.Text.Json;
using Crs.Core.Enums;

namespace Crs.Web.Services;

public class PreferencesService
{
    private readonly HttpClient _httpClient;
    private readonly AuthorizedApiClient _api;
    private readonly AuthService _authService;
    private readonly ILogger<PreferencesService> _logger;
    private readonly DevelopmentDataStore? _developmentData;

    private static readonly JsonSerializerOptions JsonOptions = CrsJsonOptions.Default;

    public PreferencesService(
        HttpClient httpClient,
        AuthService authService,
        ILogger<PreferencesService> logger,
        DevelopmentDataStore? developmentData = null)
    {
        _httpClient = httpClient;
        _api = new AuthorizedApiClient(httpClient, authService);
        _authService = authService;
        _logger = logger;
        _developmentData = developmentData;
    }

    public async Task<List<PreferenceItem>> GetPreferencesAsync()
    {
        try
        {
            if (_authService.CurrentState.IsDevelopmentLogin && _developmentData != null)
            {
                return _developmentData.GetPreferences();
            }

            var response = await _api.SendAsync(() => _httpClient.GetAsync("/api/v1/preferences"));
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

            var response = await _api.SendAsync(() =>
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

            var response = await _api.SendAsync(() =>
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

            var response = await _api.SendAsync(() => _httpClient.DeleteAsync($"/api/v1/preferences/{id}"));
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
