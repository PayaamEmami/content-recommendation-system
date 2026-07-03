using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Crs.Api.DTOs.Sources.Requests;
using Crs.Api.Extensions;
using Crs.Api.Services;
using Crs.Core.Enums;

namespace Crs.Api.Controllers;

/// <summary>
/// Controller for managing user sources (RSS feeds, YouTube channels, etc.).
/// </summary>
[Authorize]
[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
public class SourcesController : ApiControllerBase
{
    private readonly ISourceService _sourceService;
    private readonly ILogger<SourcesController> _logger;

    public SourcesController(ISourceService sourceService, ILogger<SourcesController> logger)
    {
        _sourceService = sourceService;
        _logger = logger;
    }

    /// <summary>
    /// Gets a source by ID.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSourceById(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId, out var unauthorized))
        {
            return unauthorized;
        }

        var source = await _sourceService.GetSourceByIdAsync(id, cancellationToken);
        if (source == null)
        {
            return NotFound(new { message = $"Source with ID {id} not found." });
        }

        if (source.UserId != userId)
        {
            return Forbid();
        }

        return Ok(source);
    }

    /// <summary>
    /// Gets all sources for the current user.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetUserSources(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId, out var unauthorized))
        {
            return unauthorized;
        }

        _logger.LogInformation("GetUserSources: Fetching sources for user {UserId}", userId);
        var sources = await _sourceService.GetUserSourcesAsync(userId, cancellationToken);
        _logger.LogInformation("GetUserSources: Returning {Count} sources for user {UserId}", sources.Count, userId);
        return Ok(sources);
    }

    /// <summary>
    /// Gets all active sources for the current user.
    /// </summary>
    [HttpGet("active")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetActiveUserSources(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId, out var unauthorized))
        {
            return unauthorized;
        }

        var sources = await _sourceService.GetActiveUserSourcesAsync(userId, cancellationToken);
        return Ok(sources);
    }

    /// <summary>
    /// Gets sources by category.
    /// </summary>
    [HttpGet("category/{category}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSourcesByCategory(ContentType category, CancellationToken cancellationToken)
    {
        var sources = await _sourceService.GetSourcesByCategoryAsync(category, cancellationToken);
        return Ok(sources);
    }

    /// <summary>
    /// Creates a new source.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateSource([FromBody] CreateSourceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (!TryGetUserId(out var userId, out var unauthorized))
            {
                return unauthorized;
            }

            var source = await _sourceService.CreateSourceAsync(userId, request, cancellationToken);
            return CreatedAtAction(nameof(GetSourceById), new { id = source.Id }, source);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Updates an existing source.
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateSource(Guid id, [FromBody] UpdateSourceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var source = await _sourceService.UpdateSourceAsync(id, request, cancellationToken);
            return Ok(source);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Deletes a source.
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteSource(Guid id, CancellationToken cancellationToken)
    {
        await _sourceService.DeleteSourceAsync(id, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Bulk imports multiple sources from JSON.
    /// </summary>
    [HttpPost("bulk-import")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> BulkImportSources([FromBody] BulkImportSourcesRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId, out var unauthorized))
        {
            return unauthorized;
        }

        _logger.LogInformation("BulkImportSources: Starting bulk import of {Count} sources for user {UserId}", request.Sources.Count, userId);
        var result = await _sourceService.BulkImportSourcesAsync(userId, request, cancellationToken);
        _logger.LogInformation("BulkImportSources: Completed - {Imported} imported, {Failed} failed for user {UserId}", result.Imported, result.Failed, userId);
        return Ok(result);
    }
}

