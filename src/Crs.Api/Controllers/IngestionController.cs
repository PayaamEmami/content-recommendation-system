using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Crs.Api.DTOs.Ingestion.Requests;
using Crs.Api.Extensions;
using Crs.Core.Interfaces;
using Crs.Llm.Services;

namespace Crs.Api.Controllers;

/// <summary>
/// Controller for triggering LLM-based content ingestion from sources.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
public class IngestionController : ControllerBase
{
    private readonly IIngestionAgent _ingestionAgent;
    private readonly ISourceIngestionService _sourceIngestionService;
    private readonly ISourceRepository _sourceRepository;
    private readonly ILogger<IngestionController> _logger;

    public IngestionController(
        IIngestionAgent ingestionAgent,
        ISourceIngestionService sourceIngestionService,
        ISourceRepository sourceRepository,
        ILogger<IngestionController> logger)
    {
        _ingestionAgent = ingestionAgent;
        _sourceIngestionService = sourceIngestionService;
        _sourceRepository = sourceRepository;
        _logger = logger;
    }

    /// <summary>
    /// Ingests content from a URL using the LLM agent.
    /// </summary>
    /// <remarks>
    /// Preview-only endpoint: extracts content via the LLM agent but does NOT persist it
    /// to the database or index it in the vector store. For production ingestion of a
    /// configured source, use <c>POST /ingestion/ingest-source/{sourceId}</c>.
    /// </remarks>
    [HttpPost("ingest-url")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> IngestFromUrl(
        [FromBody] IngestUrlRequest request,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out _))
        {
            return BadRequest(new { message = "Invalid URL provided." });
        }

        _logger.LogInformation("Starting ingestion from URL: {Url}", request.Url);

        var result = await _ingestionAgent.IngestFromUrlAsync(
            request.Url,
            sourceId: null,
            cancellationToken);

        if (!result.Success)
        {
            _logger.LogError("Ingestion failed: {Error}", result.ErrorMessage);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = "Ingestion failed",
                error = result.ErrorMessage
            });
        }

        return Ok(new
        {
            success = true,
            totalFound = result.TotalFound,
            newContent = result.NewContent,
            duplicatesSkipped = result.DuplicatesSkipped,
            content = result.Content.Select(r => new
            {
                r.Title,
                r.Url,
                r.Description,
                type = r.Type.ToString()
            })
        });
    }

    /// <summary>
    /// Ingests and saves content from a source by source ID.
    /// </summary>
    /// <remarks>
    /// Runs the same ingestion pipeline as the background <c>SourceIngestionJob</c>:
    /// extracts content, applies URL policy, deduplicates by URL, persists new items,
    /// and indexes them in the vector store.
    /// </remarks>
    [HttpPost("ingest-source/{sourceId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> IngestFromSource(
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId == null)
        {
            return Unauthorized(new { message = "User ID not found in token." });
        }

        var source = await _sourceRepository.GetByIdAsync(sourceId, cancellationToken);
        if (source == null)
        {
            return NotFound(new { message = $"Source with ID {sourceId} not found." });
        }

        if (source.UserId != userId.Value)
        {
            return Forbid();
        }

        _logger.LogInformation(
            "Starting ingestion from source {SourceId}: {SourceUrl}",
            sourceId,
            source.Url);

        var summary = await _sourceIngestionService.IngestSourceAsync(source, cancellationToken);

        if (!summary.Success)
        {
            _logger.LogError("Ingestion failed: {Error}", summary.ErrorMessage);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = "Ingestion failed",
                error = summary.ErrorMessage
            });
        }

        _logger.LogInformation(
            "Successfully ingested {Count} content from source {SourceId}",
            summary.Saved,
            sourceId);

        return Ok(new
        {
            success = true,
            extracted = summary.Extracted,
            saved = summary.Saved,
            duplicates = summary.Duplicates,
            errors = summary.Errors,
            embedded = summary.Embedded
        });
    }
}
