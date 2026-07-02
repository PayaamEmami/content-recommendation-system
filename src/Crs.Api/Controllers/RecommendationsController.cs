using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Crs.Api.DTOs.Recommendations.Responses;
using Crs.Api.Extensions;
using Crs.Api.Services;
using Crs.Core.Enums;

namespace Crs.Api.Controllers;

/// <summary>
/// Handles recommendation-related operations.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
[Authorize]
[EnableRateLimiting("api")]
public class RecommendationsController : ApiControllerBase
{
    private readonly IRecommendationService _recommendationService;
    private readonly ILogger<RecommendationsController> _logger;

    public RecommendationsController(
        IRecommendationService recommendationService,
        ILogger<RecommendationsController> logger)
    {
        _recommendationService = recommendationService;
        _logger = logger;
    }

    /// <summary>
    /// Gets today's recommendations across all feed types.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of recommendations grouped by feed type.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(List<FeedRecommendationsResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTodaysRecommendations(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId, out var unauthorized))
        {
            return unauthorized;
        }

        var recommendations = await _recommendationService.GetTodaysRecommendationsAsync(
            userId,
            cancellationToken);

        return Ok(recommendations);
    }

    /// <summary>
    /// Gets recommendations for a specific feed type.
    /// </summary>
    /// <param name="feedType">The feed type (Papers, Videos, etc.).</param>
    /// <param name="date">Optional date (defaults to today).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Recommendations for the specified feed type and date.</returns>
    [HttpGet("{feedType}")]
    [ProducesResponseType(typeof(FeedRecommendationsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetFeedRecommendations(
        ContentType feedType,
        [FromQuery] DateOnly? date = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(out var userId, out var unauthorized))
        {
            return unauthorized;
        }

        var targetDate = date ?? DateOnly.FromDateTime(DateTime.UtcNow);

        var recommendations = await _recommendationService.GetFeedRecommendationsAsync(
            userId,
            feedType,
            targetDate,
            cancellationToken);

        return Ok(recommendations);
    }
}

