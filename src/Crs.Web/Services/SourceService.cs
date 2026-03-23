using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Crs.Core.Enums;
using Microsoft.JSInterop;

namespace Crs.Web.Services;

/// <summary>
/// Service for managing user sources that integrates with the CRS API.
/// </summary>
public class SourceService
{
    private readonly HttpClient _httpClient;
    private readonly AuthService _authService;
    private readonly ILogger<SourceService> _logger;
    private readonly IJSRuntime _jsRuntime;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public SourceService(
        HttpClient httpClient,
        AuthService authService,
        ILogger<SourceService> logger,
        IJSRuntime jsRuntime)
    {
        _httpClient = httpClient;
        _authService = authService;
        _logger = logger;
        _jsRuntime = jsRuntime;
    }

    public SourceService(
        HttpClient httpClient,
        AuthService authService,
        ILogger<SourceService> logger)
        : this(httpClient, authService, logger, new NoOpJsRuntime())
    {
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

    public async Task<List<SourceItem>> GetUserSourcesAsync()
    {
        var result = await GetUserSourcesResultAsync();
        return result.Sources;
    }

    public async Task<SourceLoadResult> GetUserSourcesResultAsync()
    {
        try
        {
            var response = await SendAuthorizedAsync(() => _httpClient.GetAsync("/api/v1/sources"));
            if (response == null)
            {
                _logger.LogWarning("User not authenticated, cannot fetch sources");
                return SourceLoadResult.Failure("Your session expired. Please sign in again.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Failed to fetch sources: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return SourceLoadResult.Failure("Couldn't load your sources right now. Please try again.");
            }

            var sources = await response.Content.ReadFromJsonAsync<List<SourceResponse>>(JsonOptions);

            if (sources == null)
            {
                _logger.LogWarning("Sources response was null");
                return SourceLoadResult.Failure("Received an empty response while loading sources.");
            }

            _logger.LogInformation("Successfully fetched {Count} sources from API", sources.Count);

            return SourceLoadResult.Success(sources.Select(s => new SourceItem
            {
                Id = s.Id,
                UserId = s.UserId,
                Name = s.Name,
                Url = s.Url,
                Category = s.Category,
                Description = s.Description,
                IsActive = s.IsActive,
                CreatedAt = s.CreatedAt,
                LastFetchedAt = s.LastFetchedAt,
                ContentCount = s.ContentCount
            }).OrderBy(s => s.Name).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching sources");
            return SourceLoadResult.Failure("Couldn't load your sources right now. Please try again.");
        }
    }

    public async Task<bool> AddSourceAsync(string name, string url, ContentType category, string? description)
    {
        try
        {
            var request = new CreateSourceRequest
            {
                Name = name,
                Url = url,
                Category = category,
                Description = description
            };

            var response = await SendAuthorizedAsync(() => _httpClient.PostAsJsonAsync("/api/v1/sources", request));
            if (response == null)
            {
                _logger.LogWarning("User not authenticated, cannot add source");
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Failed to add source: {StatusCode} - {Content}", response.StatusCode, errorContent);
                return false;
            }

            _logger.LogInformation("Successfully added source: {Name}", name);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding source");
            return false;
        }
    }

    public async Task<bool> DeleteSourceAsync(Guid sourceId)
    {
        try
        {
            var response = await SendAuthorizedAsync(() => _httpClient.DeleteAsync($"/api/v1/sources/{sourceId}"));
            if (response == null)
            {
                _logger.LogWarning("User not authenticated, cannot delete source");
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to delete source: {StatusCode}", response.StatusCode);
                return false;
            }

            _logger.LogInformation("Successfully deleted source: {SourceId}", sourceId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting source");
            return false;
        }
    }

    public async Task<bool> ToggleSourceActiveAsync(Guid sourceId)
    {
        try
        {
            // First, get the current source to determine its state
            var getResponse = await SendAuthorizedAsync(() => _httpClient.GetAsync($"/api/v1/sources/{sourceId}"));
            if (getResponse == null)
            {
                _logger.LogWarning("User not authenticated, cannot toggle source");
                return false;
            }

            if (!getResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch source: {StatusCode}", getResponse.StatusCode);
                return false;
            }

            var source = await getResponse.Content.ReadFromJsonAsync<SourceResponse>(JsonOptions);
            if (source == null)
            {
                return false;
            }

            // Update the source with toggled IsActive state
            var updateRequest = new UpdateSourceRequest
            {
                Name = source.Name,
                Url = source.Url,
                Category = source.Category,
                Description = source.Description,
                IsActive = !source.IsActive
            };

            var updateResponse = await SendAuthorizedAsync(() =>
                _httpClient.PutAsJsonAsync($"/api/v1/sources/{sourceId}", updateRequest));
            if (updateResponse == null)
            {
                return false;
            }

            if (!updateResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to update source: {StatusCode}", updateResponse.StatusCode);
                return false;
            }

            _logger.LogInformation("Successfully toggled source: {SourceId}", sourceId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling source");
            return false;
        }
    }

    public async Task<bool> UpdateSourceAsync(Guid sourceId, string name, string url, ContentType category, string? description)
    {
        try
        {
            // First get the current source to preserve its IsActive state
            var getResponse = await SendAuthorizedAsync(() => _httpClient.GetAsync($"/api/v1/sources/{sourceId}"));
            if (getResponse == null)
            {
                _logger.LogWarning("User not authenticated, cannot update source");
                return false;
            }

            if (!getResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch source for update: {StatusCode}", getResponse.StatusCode);
                return false;
            }

            var source = await getResponse.Content.ReadFromJsonAsync<SourceResponse>(JsonOptions);
            if (source == null)
            {
                return false;
            }

            var updateRequest = new UpdateSourceRequest
            {
                Name = name,
                Url = url,
                Category = category,
                Description = description,
                IsActive = source.IsActive // Preserve existing active state
            };

            var response = await SendAuthorizedAsync(() =>
                _httpClient.PutAsJsonAsync($"/api/v1/sources/{sourceId}", updateRequest));
            if (response == null)
            {
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Failed to update source: {StatusCode} - {Content}", response.StatusCode, errorContent);
                return false;
            }

            _logger.LogInformation("Successfully updated source: {SourceId}", sourceId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating source");
            return false;
        }
    }

    public async Task<ImportResultModel> ImportSourcesAsync(string json)
    {
        try
        {
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await SendAuthorizedAsync(() => _httpClient.PostAsync("/api/v1/sources/bulk-import", content));
            if (response == null)
            {
                throw new InvalidOperationException("User not authenticated");
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Failed to import sources: {StatusCode} - {Content}", response.StatusCode, errorContent);
                throw new HttpRequestException($"Failed to import sources: {response.StatusCode}");
            }

            var result = await response.Content.ReadFromJsonAsync<ImportResultResponse>(JsonOptions);
            if (result == null)
            {
                throw new InvalidOperationException("Failed to parse import response");
            }

            return new ImportResultModel
            {
                Imported = result.Imported,
                Failed = result.Failed,
                Errors = result.Errors.Select(e => new ImportErrorModel
                {
                    Url = e.Url,
                    Error = e.Error
                }).ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during source import");
            throw;
        }
    }

    public Task<ImportResultModel> BulkImportSourcesAsync(string json)
    {
        return ImportSourcesAsync(json);
    }

    public async Task ExportSourcesAsync(IEnumerable<SourceItem> sources)
    {
        var exportPayload = sources
            .OrderBy(s => s.Name)
            .Select(s => new SourceExportItem
            {
                Name = s.Name,
                Url = s.Url,
                Category = s.Category,
                Description = s.Description
            })
            .ToList();

        var json = JsonSerializer.Serialize(exportPayload, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        });

        var fileName = $"crs-sources-{DateTime.UtcNow:yyyy-MM-dd}.json";
        await _jsRuntime.InvokeVoidAsync("downloadFile", fileName, "application/json", json);
    }
}

public class SourceItem
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public ContentType Category { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastFetchedAt { get; set; }
    public int ContentCount { get; set; }
}

// API Request/Response DTOs
public class CreateSourceRequest
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public ContentType Category { get; set; }
    public string? Description { get; set; }
}

public class UpdateSourceRequest
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public ContentType Category { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; }
}

public class SourceResponse
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ContentType Category { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastFetchedAt { get; set; }
    public string? LastFetchError { get; set; }
    public int ContentCount { get; set; }
}

public class ImportResultResponse
{
    public int Imported { get; set; }
    public int Failed { get; set; }
    public List<ImportErrorResponse> Errors { get; set; } = new();
}

public class ImportErrorResponse
{
    public string Url { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}

public class ImportResultModel
{
    public int Imported { get; set; }
    public int Failed { get; set; }
    public List<ImportErrorModel> Errors { get; set; } = new();
}

public class ImportErrorModel
{
    public string Url { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}

public class SourceExportItem
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public ContentType Category { get; set; }
    public string? Description { get; set; }
}

public sealed class SourceLoadResult
{
    public required bool IsSuccess { get; init; }
    public required List<SourceItem> Sources { get; init; }
    public string? ErrorMessage { get; init; }

    public static SourceLoadResult Success(List<SourceItem> sources) => new()
    {
        IsSuccess = true,
        Sources = sources
    };

    public static SourceLoadResult Failure(string errorMessage) => new()
    {
        IsSuccess = false,
        Sources = new List<SourceItem>(),
        ErrorMessage = errorMessage
    };
}

internal sealed class NoOpJsRuntime : IJSRuntime
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
    {
        return ValueTask.FromResult(default(TValue)!);
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        return ValueTask.FromResult(default(TValue)!);
    }
}
