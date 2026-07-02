using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Crs.Api.DTOs.X.Requests;
using Crs.Api.DTOs.X.Responses;
using Crs.Api.Extensions;
using Crs.Api.Services;
using System.Net;

namespace Crs.Api.Controllers;

/// <summary>
/// Handles X account connection and feed operations.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/x")]
[Authorize]
[EnableRateLimiting("api")]
public class XAccountsController : ApiControllerBase
{
    private readonly IXAccountService _xAccountService;
    private readonly ILogger<XAccountsController> _logger;
    private readonly IConfiguration _configuration;

    public XAccountsController(
        IXAccountService xAccountService,
        ILogger<XAccountsController> logger,
        IConfiguration configuration)
    {
        _xAccountService = xAccountService;
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// Creates an authorization URL to connect an X account.
    /// </summary>
    [HttpGet("connect-url")]
    [ProducesResponseType(typeof(XConnectUrlResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectUrl([FromQuery] string? redirectUri, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId, out var unauthorized))
        {
            return unauthorized;
        }

        string? resolvedRedirectUri = null;
        if (!string.IsNullOrWhiteSpace(redirectUri))
        {
            if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var redirectUriParsed))
            {
                return BadRequest("Invalid redirectUri");
            }

            var origin = redirectUriParsed.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            var allowedOrigins = _configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
            var isAllowed = allowedOrigins.Any(allowed =>
                string.Equals(allowed.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase));

            if (!isAllowed)
            {
                return BadRequest("Redirect URI is not allowed");
            }

            resolvedRedirectUri = redirectUri;
        }

        try
        {
            var url = await _xAccountService.CreateConnectUrlAsync(userId, resolvedRedirectUri, cancellationToken);
            return Ok(new XConnectUrlResponse { AuthorizationUrl = url });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Handles the OAuth callback and stores tokens.
    /// </summary>
    [HttpPost("callback")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> HandleCallback([FromBody] XCallbackRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId, out var unauthorized))
        {
            return unauthorized;
        }

        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.State))
        {
            return BadRequest("Missing code or state");
        }

        try
        {
            await _xAccountService.HandleCallbackAsync(userId, request.Code, request.State, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "X callback failed for user {UserId}", userId);
            return BadRequest(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "X callback upstream request failed for user {UserId}", userId);

            if (ex.StatusCode == HttpStatusCode.Forbidden)
            {
                return Problem(
                    title: "X denied profile access",
                    detail: "X authorized the app, but denied access to the authenticated user's profile endpoint. Verify the X app, project, and user-auth access for this client.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            return Problem(
                title: "X connection failed",
                detail: "X authorized the app, but rejected a follow-up API request. Check the API logs for the X response body and verify the app's access tier and user auth permissions.",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>
    /// Disconnects the user's X account and removes all related data.
    /// </summary>
    [HttpDelete("connection")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Disconnect(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId, out var unauthorized))
        {
            return unauthorized;
        }

        await _xAccountService.DisconnectAsync(userId, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Gets followed X accounts, optionally refreshing from X.
    /// </summary>
    [HttpGet("followed-accounts")]
    [ProducesResponseType(typeof(List<XFollowedAccountResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFollowedAccounts([FromQuery] bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(out var userId, out var unauthorized))
        {
            return unauthorized;
        }

        List<Crs.Core.Entities.XFollowedAccount> followed;
        try
        {
            followed = refresh
                ? await _xAccountService.RefreshFollowedAccountsAsync(userId, cancellationToken)
                : await _xAccountService.GetFollowedAccountsAsync(userId, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }

        var response = await BuildFollowedAccountsResponseAsync(userId, followed, cancellationToken);
        return Ok(response);
    }

    /// <summary>
    /// Updates the selected followed accounts used for the X feed.
    /// </summary>
    [HttpPost("selected-accounts")]
    [ProducesResponseType(typeof(List<XFollowedAccountResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateSelectedAccounts([FromBody] XSelectedAccountsRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId, out var unauthorized))
        {
            return unauthorized;
        }

        try
        {
            await _xAccountService.UpdateSelectedAccountsAsync(userId, request.FollowedAccountIds, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }

        var followed = await _xAccountService.GetFollowedAccountsAsync(userId, cancellationToken);
        var response = await BuildFollowedAccountsResponseAsync(userId, followed, cancellationToken);

        _logger.LogInformation("Updated X selected accounts for user {UserId}", userId);
        return Ok(response);
    }

    private async Task<List<XFollowedAccountResponse>> BuildFollowedAccountsResponseAsync(
        Guid userId,
        IEnumerable<Crs.Core.Entities.XFollowedAccount> followed,
        CancellationToken cancellationToken)
    {
        var selected = await _xAccountService.GetSelectedAccountsAsync(userId, cancellationToken);
        var selectedIds = selected.Select(s => s.XFollowedAccountId).ToHashSet();

        return followed
            .Select(account => XFollowedAccountResponse.FromEntity(account, selectedIds.Contains(account.Id)))
            .ToList();
    }

    /// <summary>
    /// Gets the stored X posts feed for the user.
    /// </summary>
    [HttpGet("posts")]
    [ProducesResponseType(typeof(List<XPostResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPosts([FromQuery] int limit = 30, CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(out var userId, out var unauthorized))
        {
            return unauthorized;
        }

        List<Crs.Core.Entities.XPost> posts;
        try
        {
            posts = await _xAccountService.GetPostsAsync(userId, limit, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        var response = posts.Select(XPostResponse.FromEntity).ToList();

        return Ok(response);
    }
}
