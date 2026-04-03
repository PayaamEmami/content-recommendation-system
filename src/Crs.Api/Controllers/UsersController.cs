using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Crs.Api.DTOs.Users.Requests;
using Crs.Api.DTOs.Users.Responses;
using Crs.Api.DTOs.Votes.Responses;
using Crs.Api.Extensions;
using Crs.Api.Services;

namespace Crs.Api.Controllers;

/// <summary>
/// Handles user-related operations (profile management, topics).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
[Authorize]
[EnableRateLimiting("api")]
public class UsersController : ControllerBase
{
    private readonly IUserService _userService;
    private readonly IVoteService _voteService;
    private readonly ILogger<UsersController> _logger;

    public UsersController(
        IUserService userService,
        IVoteService voteService,
        ILogger<UsersController> logger)
    {
        _userService = userService;
        _voteService = voteService;
        _logger = logger;
    }

    /// <summary>
    /// Gets the current authenticated user's profile.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The user's detailed profile information.</returns>
    [HttpGet("me")]
    [ProducesResponseType(typeof(UserDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCurrentUser(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        var user = await _userService.GetUserByIdAsync(userId.Value, cancellationToken);
        if (user == null)
        {
            return NotFound();
        }

        return Ok(user);
    }

    /// <summary>
    /// Gets a user by their ID.
    /// </summary>
    /// <param name="id">The user's ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The user's detailed profile information.</returns>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(UserDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUserById(Guid id, CancellationToken cancellationToken)
    {
        var user = await _userService.GetUserByIdAsync(id, cancellationToken);
        if (user == null)
        {
            return NotFound();
        }

        return Ok(user);
    }

    /// <summary>
    /// Updates the current user's profile.
    /// </summary>
    /// <param name="request">The updated profile information.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated user information.</returns>
    [HttpPatch("me")]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateCurrentUser(
        [FromBody] UpdateUserRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        var user = await _userService.UpdateUserAsync(userId.Value, request, cancellationToken);
        return Ok(user);
    }

    /// <summary>
    /// Updates a specific user's profile (admin operation or same user).
    /// </summary>
    /// <param name="id">The user's ID.</param>
    /// <param name="request">The updated profile information.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated user information.</returns>
    [HttpPatch("{id}")]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateUser(
        Guid id,
        [FromBody] UpdateUserRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = User.GetUserId();
        if (currentUserId == null)
        {
            return Unauthorized();
        }

        // Only allow users to update their own profile (in a real system, admins could update any)
        if (currentUserId.Value != id)
        {
            return Forbid();
        }

        var user = await _userService.UpdateUserAsync(id, request, cancellationToken);
        return Ok(user);
    }

    /// <summary>
    /// Gets the current user's votes on content.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of the user's votes.</returns>
    [HttpGet("me/votes")]
    [ProducesResponseType(typeof(List<VoteResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetCurrentUserVotes(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        var votes = await _voteService.GetUserVotesAsync(userId.Value, cancellationToken);
        return Ok(votes);
    }

    /// <summary>
    /// Gets the current user's vote history with content details.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of vote history items.</returns>
    [HttpGet("me/vote-history")]
    [ProducesResponseType(typeof(List<VoteHistoryItemResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetCurrentUserVoteHistory(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        var history = await _voteService.GetUserVoteHistoryAsync(userId.Value, cancellationToken);
        return Ok(history);
    }
}
