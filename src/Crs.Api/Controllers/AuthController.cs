using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Crs.Api.Configuration;
using Crs.Api.DTOs.Auth.Requests;
using Crs.Api.DTOs.Auth.Responses;
using Crs.Api.Services;

namespace Crs.Api.Controllers;

/// <summary>
/// Handles authentication operations (login, registration, token refresh).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
[EnableRateLimiting("auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly ILogger<AuthController> _logger;
    private readonly RegistrationSettings _registrationSettings;

    public AuthController(
        IAuthService authService,
        ILogger<AuthController> logger,
        RegistrationSettings registrationSettings)
    {
        _authService = authService;
        _logger = logger;
        _registrationSettings = registrationSettings;
    }

    /// <summary>
    /// Gets the registration status.
    /// </summary>
    /// <returns>Registration status information.</returns>
    [HttpGet("registration-status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetRegistrationStatus()
    {
        return Ok(new
        {
            enabled = _registrationSettings.Enabled,
            message = _registrationSettings.Enabled ? null : _registrationSettings.DisabledMessage
        });
    }

    /// <summary>
    /// Authenticates a user and returns JWT tokens.
    /// </summary>
    /// <param name="request">The login credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Access token, refresh token, and user information.</returns>
    [HttpPost("login")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Login attempt submitted");

        var response = await _authService.LoginAsync(request, cancellationToken);
        return Ok(response);
    }

    /// <summary>
    /// Registers a new user account.
    /// </summary>
    /// <param name="request">The registration information.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Access token, refresh token, and user information.</returns>
    [HttpPost("register")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Register(
        [FromBody] RegisterRequest request,
        CancellationToken cancellationToken)
    {
        // Check if registrations are enabled
        if (!_registrationSettings.Enabled)
        {
            _logger.LogWarning("Registration attempt rejected - registrations are disabled");
            return Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Registration Disabled",
                detail: _registrationSettings.DisabledMessage);
        }

        _logger.LogInformation("Registration attempt submitted");

        var response = await _authService.RegisterAsync(request, cancellationToken);
        return Ok(response);
    }

    /// <summary>
    /// Refreshes an expired access token using a refresh token.
    /// </summary>
    /// <param name="request">The refresh token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>New access token and refresh token.</returns>
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(RefreshTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RefreshToken(
        [FromBody] RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _authService.RefreshTokenAsync(request, cancellationToken);
        return Ok(response);
    }
}
