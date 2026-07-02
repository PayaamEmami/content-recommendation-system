using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Crs.Api.DTOs.Common;
using Crs.Api.DTOs.Content.Requests;
using Crs.Api.DTOs.Content.Responses;
using Crs.Api.DTOs.Votes.Requests;
using Crs.Api.DTOs.Votes.Responses;
using Crs.Api.Extensions;
using Crs.Api.Services;
using Crs.Core.Enums;

namespace Crs.Api.Controllers;

/// <summary>
/// Handles content-related operations (CRUD).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
[Authorize]
[EnableRateLimiting("api")]
public class ContentController : ApiControllerBase
{
    private readonly IContentService _contentService;
    private readonly IVoteService _voteService;
    private readonly ILogger<ContentController> _logger;

    public ContentController(
        IContentService contentService,
        IVoteService voteService,
        ILogger<ContentController> logger)
    {
        _contentService = contentService;
        _voteService = voteService;
        _logger = logger;
    }

    /// <summary>
    /// Gets paginated content with optional filtering.
    /// </summary>
    /// <param name="pageNumber">Page number (default: 1).</param>
    /// <param name="pageSize">Page size (default: 20).</param>
    /// <param name="type">Filter by content type.</param>
    /// <param name="topicIds">Filter by topic IDs (comma-separated).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paginated list of content.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<ContentResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetContent(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] ContentType? type = null,
        [FromQuery] string? topicIds = null,
        CancellationToken cancellationToken = default)
    {
        // Validate pagination parameters
        if (pageNumber < 1)
        {
            return BadRequest("Page number must be greater than 0");
        }

        if (pageSize < 1 || pageSize > 100)
        {
            return BadRequest("Page size must be between 1 and 100");
        }

        // Parse topic IDs if provided
        List<Guid>? topicIdList = null;
        if (!string.IsNullOrWhiteSpace(topicIds))
        {
            try
            {
                topicIdList = topicIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(Guid.Parse)
                    .ToList();
            }
            catch
            {
                return BadRequest("Invalid topic IDs format");
            }
        }

        var content = await _contentService.GetContentAsync(
            pageNumber,
            pageSize,
            type,
            topicIdList,
            cancellationToken);

        return Ok(content);
    }

    /// <summary>
    /// Gets a specific content item by ID.
    /// </summary>
    /// <param name="id">The content item's ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The content information.</returns>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ContentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetContentById(Guid id, CancellationToken cancellationToken)
    {
        var content = await _contentService.GetContentByIdAsync(id, cancellationToken);

        if (content == null)
        {
            return NotFound();
        }

        return Ok(content);
    }

    /// <summary>
    /// Creates a new content item.
    /// </summary>
    /// <param name="request">The content information.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created content.</returns>
    [HttpPost]
    [ProducesResponseType(typeof(ContentResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateContent(
        [FromBody] CreateContentRequest request,
        CancellationToken cancellationToken)
    {
        var content = await _contentService.CreateContentAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetContentById), new { id = content.Id }, content);
    }

    /// <summary>
    /// Updates an existing content item.
    /// </summary>
    /// <param name="id">The content item's ID.</param>
    /// <param name="request">The updated content information.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated content.</returns>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(ContentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateContent(
        Guid id,
        [FromBody] UpdateContentRequest request,
        CancellationToken cancellationToken)
    {
        var content = await _contentService.UpdateContentAsync(id, request, cancellationToken);
        return Ok(content);
    }

    /// <summary>
    /// Deletes a content item.
    /// </summary>
    /// <param name="id">The content item's ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteContent(Guid id, CancellationToken cancellationToken)
    {
        await _contentService.DeleteContentAsync(id, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Casts or updates a vote on a content item.
    /// </summary>
    /// <param name="id">The content item's ID.</param>
    /// <param name="request">The vote information.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The vote information.</returns>
    [HttpPost("{id}/vote")]
    [ProducesResponseType(typeof(VoteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> VoteOnContent(
        Guid id,
        [FromBody] VoteRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId, out var unauthorized))
        {
            return unauthorized;
        }

        var vote = await _voteService.VoteOnContentAsync(userId, id, request, cancellationToken);
        return Ok(vote);
    }

    /// <summary>
    /// Removes a vote from a content item.
    /// </summary>
    /// <param name="id">The content item's ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpDelete("{id}/vote")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveVote(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId, out var unauthorized))
        {
            return unauthorized;
        }

        await _voteService.RemoveVoteAsync(userId, id, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Gets the current user's vote on a content item.
    /// </summary>
    /// <param name="id">The content item's ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The vote information if exists.</returns>
    [HttpGet("{id}/vote")]
    [ProducesResponseType(typeof(VoteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetVoteOnContent(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId, out var unauthorized))
        {
            return unauthorized;
        }

        var vote = await _voteService.GetUserVoteOnContentAsync(userId, id, cancellationToken);

        if (vote == null)
        {
            return NotFound();
        }

        return Ok(vote);
    }
}
