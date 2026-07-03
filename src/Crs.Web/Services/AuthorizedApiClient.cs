using System.Net;
using System.Net.Http.Headers;

namespace Crs.Web.Services;

/// <summary>
/// Wraps an <see cref="HttpClient"/> with bearer-token authentication and transparent
/// 401 handling: it ensures the caller is authenticated, attaches the current access
/// token, and on an unauthorized response attempts a single token refresh + retry
/// before logging the user out.
/// </summary>
public sealed class AuthorizedApiClient
{
    private readonly HttpClient _httpClient;
    private readonly AuthService _authService;

    public AuthorizedApiClient(HttpClient httpClient, AuthService authService)
    {
        _httpClient = httpClient;
        _authService = authService;
    }

    /// <summary>The underlying client, used by services to build requests.</summary>
    public HttpClient Http => _httpClient;

    private void SetAuthHeader()
    {
        if (!string.IsNullOrEmpty(_authService.CurrentState.AccessToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _authService.CurrentState.AccessToken);
        }
    }

    /// <summary>
    /// Executes an authorized request. Returns <c>null</c> when the user is not (and
    /// cannot become) authenticated. On a 401 it refreshes the token once and retries;
    /// if the refresh fails it logs out and returns the original unauthorized response.
    /// </summary>
    public async Task<HttpResponseMessage?> SendAsync(Func<Task<HttpResponseMessage>> requestFactory)
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
}
