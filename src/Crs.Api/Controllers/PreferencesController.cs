using Asp.Versioning;
using Crs.Api.DTOs.Preferences.Requests;
using Crs.Api.DTOs.Preferences.Responses;
using Crs.Api.Extensions;
using Crs.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Crs.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
[Authorize]
[EnableRateLimiting("api")]
public class PreferencesController : ApiControllerBase
{
    private readonly IPreferenceService _preferenceService;

    public PreferencesController(IPreferenceService preferenceService)
    {
        _preferenceService = preferenceService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(List<ManualContentFeedbackResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetPreferences(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId, out var unauthorized))
        {
            return unauthorized;
        }

        var preferences = await _preferenceService.GetManualFeedbackAsync(userId, cancellationToken);
        return Ok(preferences);
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ManualContentFeedbackResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPreference(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId, out var unauthorized))
        {
            return unauthorized;
        }

        var preference = await _preferenceService.GetManualFeedbackByIdAsync(userId, id, cancellationToken);
        if (preference == null)
        {
            return NotFound();
        }

        return Ok(preference);
    }

    [HttpPost]
    [ProducesResponseType(typeof(ManualContentFeedbackResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreatePreference([FromBody] CreateManualContentFeedbackRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId, out var unauthorized))
        {
            return unauthorized;
        }

        var preference = await _preferenceService.CreateManualFeedbackAsync(userId, request, cancellationToken);
        return CreatedAtAction(nameof(GetPreference), new { id = preference.Id }, preference);
    }

    [HttpPut("{id}")]
    [ProducesResponseType(typeof(ManualContentFeedbackResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdatePreference(Guid id, [FromBody] UpdateManualContentFeedbackRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId, out var unauthorized))
        {
            return unauthorized;
        }

        var preference = await _preferenceService.UpdateManualFeedbackAsync(userId, id, request, cancellationToken);
        return Ok(preference);
    }

    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeletePreference(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId, out var unauthorized))
        {
            return unauthorized;
        }

        await _preferenceService.DeleteManualFeedbackAsync(userId, id, cancellationToken);
        return NoContent();
    }
}
