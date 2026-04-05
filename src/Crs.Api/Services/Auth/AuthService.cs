using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Crs.Api.Configuration;
using Crs.Api.DTOs.Auth.Requests;
using Crs.Api.DTOs.Auth.Responses;
using Crs.Api.DTOs.Users.Responses;
using Crs.Core.Entities;
using Crs.Core.Interfaces;
using Crs.Core.Observability;

namespace Crs.Api.Services;

/// <summary>
/// Service for handling authentication operations.
/// </summary>
public class AuthService : IAuthService
{
    private readonly IUserRepository _userRepository;
    private readonly IRefreshTokenRepository _refreshTokenRepository;
    private readonly JwtSettings _jwtSettings;
    private readonly RegistrationSettings _registrationSettings;
    private readonly ILogger<AuthService> _logger;
    private readonly IObservabilityMetrics _metrics;
    private readonly PasswordHasher<User> _passwordHasher;

    public AuthService(
        IUserRepository userRepository,
        IRefreshTokenRepository refreshTokenRepository,
        JwtSettings jwtSettings,
        RegistrationSettings registrationSettings,
        ILogger<AuthService> logger,
        IObservabilityMetrics metrics)
    {
        _userRepository = userRepository;
        _refreshTokenRepository = refreshTokenRepository;
        _jwtSettings = jwtSettings;
        _registrationSettings = registrationSettings;
        _logger = logger;
        _metrics = metrics;
        _passwordHasher = new PasswordHasher<User>();
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        using var activity = CrsTelemetry.ActivitySource.StartActivity("auth.login");
        var startedAt = DateTime.UtcNow;
        // Find user by email
        var user = await _userRepository.GetByEmailAsync(request.Email, cancellationToken);
        if (user == null)
        {
            RecordAuthFailure("login", "user_not_found");
            throw new UnauthorizedAccessException("Invalid email or password");
        }

        // Verify password
        var verificationResult = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verificationResult == PasswordVerificationResult.Failed)
        {
            RecordAuthFailure("login", "invalid_password");
            throw new UnauthorizedAccessException("Invalid email or password");
        }

        // Update last login
        user.LastLoginAt = DateTime.UtcNow;
        await _userRepository.UpdateAsync(user, cancellationToken);

        // Generate tokens
        var accessToken = GenerateAccessToken(user);
        var refreshToken = await GenerateAndStoreRefreshTokenAsync(user.Id, cancellationToken);
        RecordAuthSuccess("login", startedAt);

        return new LoginResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = DateTime.UtcNow.AddMinutes(_jwtSettings.ExpirationMinutes),
            User = MapToUserResponse(user)
        };
    }

    public async Task<LoginResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        using var activity = CrsTelemetry.ActivitySource.StartActivity("auth.register");
        var startedAt = DateTime.UtcNow;
        // Check if registrations are enabled (defense in depth)
        if (!_registrationSettings.Enabled)
        {
            _logger.LogWarning("Registration attempt rejected at service layer - registrations are disabled");
            RecordAuthFailure("register", "registrations_disabled", startedAt);
            throw new InvalidOperationException(_registrationSettings.DisabledMessage);
        }

        // Check if user already exists
        var existingUser = await _userRepository.GetByEmailAsync(request.Email, cancellationToken);
        if (existingUser != null)
        {
            RecordAuthFailure("register", "duplicate_email", startedAt);
            throw new ArgumentException("A user with this email already exists");
        }

        // Create new user
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = request.Email,
            DisplayName = request.DisplayName,
            CreatedAt = DateTime.UtcNow,
            LastLoginAt = DateTime.UtcNow
        };

        // Hash password
        user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);

        await _userRepository.CreateAsync(user, cancellationToken);

        _logger.LogInformation("New user registered");

        // Generate tokens
        var accessToken = GenerateAccessToken(user);
        var refreshToken = await GenerateAndStoreRefreshTokenAsync(user.Id, cancellationToken);
        RecordAuthSuccess("register", startedAt);

        return new LoginResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = DateTime.UtcNow.AddMinutes(_jwtSettings.ExpirationMinutes),
            User = MapToUserResponse(user)
        };
    }

    public async Task<RefreshTokenResponse> RefreshTokenAsync(
        RefreshTokenRequest request,
        CancellationToken cancellationToken = default)
    {
        using var activity = CrsTelemetry.ActivitySource.StartActivity("auth.refresh");
        var startedAt = DateTime.UtcNow;
        await _refreshTokenRepository.RemoveExpiredAsync(cancellationToken);

        var tokenEntity = await _refreshTokenRepository.GetAndRemoveAsync(request.RefreshToken, cancellationToken);
        if (tokenEntity == null)
        {
            RecordAuthFailure("refresh", "invalid_refresh_token", startedAt);
            throw new UnauthorizedAccessException("Invalid refresh token");
        }

        if (tokenEntity.ExpiresAt < DateTime.UtcNow)
        {
            RecordAuthFailure("refresh", "expired_refresh_token", startedAt);
            throw new UnauthorizedAccessException("Refresh token has expired");
        }

        var user = await _userRepository.GetByIdAsync(tokenEntity.UserId, cancellationToken);
        if (user == null)
        {
            RecordAuthFailure("refresh", "user_not_found", startedAt);
            throw new UnauthorizedAccessException("User not found");
        }

        var accessToken = GenerateAccessToken(user);
        var refreshToken = await GenerateAndStoreRefreshTokenAsync(user.Id, cancellationToken);
        RecordAuthSuccess("refresh", startedAt);

        return new RefreshTokenResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = DateTime.UtcNow.AddMinutes(_jwtSettings.ExpirationMinutes)
        };
    }

    public Task<Guid?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.UTF8.GetBytes(_jwtSettings.SecretKey);

            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = _jwtSettings.Issuer,
                ValidAudience = _jwtSettings.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(key)
            };

            var principal = tokenHandler.ValidateToken(token, validationParameters, out _);
            var userIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (Guid.TryParse(userIdClaim, out var userId))
            {
                return Task.FromResult<Guid?>(userId);
            }

            return Task.FromResult<Guid?>(null);
        }
        catch
        {
            return Task.FromResult<Guid?>(null);
        }
    }

    private string GenerateAccessToken(User user)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(_jwtSettings.SecretKey);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, user.DisplayName ?? user.Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(_jwtSettings.ExpirationMinutes),
            Issuer = _jwtSettings.Issuer,
            Audience = _jwtSettings.Audience,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha256Signature)
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    private async Task<string> GenerateAndStoreRefreshTokenAsync(Guid userId, CancellationToken cancellationToken)
    {
        var refreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var expiresAt = DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays);

        await _refreshTokenRepository.AddAsync(new RefreshToken
        {
            Token = refreshToken,
            UserId = userId,
            ExpiresAt = expiresAt
        }, cancellationToken);

        return refreshToken;
    }

    private static UserResponse MapToUserResponse(User user)
    {
        return new UserResponse
        {
            Id = user.Id,
            Email = user.Email,
            DisplayName = user.DisplayName,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt
        };
    }

    private void RecordAuthFailure(string operation, string outcome, DateTime? startedAt = null)
    {
        _metrics.Increment(
            "auth.failure.count",
            context: new MetricContext(
                Dimensions: new Dictionary<string, string>
                {
                    ["Operation"] = operation,
                    ["Outcome"] = outcome,
                    ["StatusClass"] = "4xx"
                }));

        if (startedAt.HasValue)
        {
            _metrics.RecordDuration(
                "auth.request.duration",
                DateTime.UtcNow - startedAt.Value,
                new MetricContext(
                    Dimensions: new Dictionary<string, string>
                    {
                        ["Operation"] = operation,
                        ["Outcome"] = outcome,
                        ["StatusClass"] = "4xx"
                    }));
        }
    }

    private void RecordAuthSuccess(string operation, DateTime startedAt)
    {
        _metrics.Increment(
            "auth.success.count",
            context: new MetricContext(
                Dimensions: new Dictionary<string, string>
                {
                    ["Operation"] = operation,
                    ["Outcome"] = "success",
                    ["StatusClass"] = "2xx"
                }));
        _metrics.RecordDuration(
            "auth.request.duration",
            DateTime.UtcNow - startedAt,
            new MetricContext(
                Dimensions: new Dictionary<string, string>
                {
                    ["Operation"] = operation,
                    ["Outcome"] = "success",
                    ["StatusClass"] = "2xx"
                }));
    }
}

