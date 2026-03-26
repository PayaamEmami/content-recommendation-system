using Microsoft.Extensions.Logging.Abstractions;
using Crs.Api.Configuration;
using Crs.Api.DTOs.Auth.Requests;
using Crs.Api.Services;
using Crs.Core.Observability;
using Crs.Tests.Unit.Infrastructure;

namespace Crs.Tests.Unit.Api;

[TestClass]
public sealed class AuthServiceTests
{
    private static JwtSettings CreateJwtSettings()
    {
        return new JwtSettings
        {
            SecretKey = "test-secret-key-for-unit-tests-only",
            Issuer = "Crs.Api.Tests",
            Audience = "Crs.Web.Tests",
            ExpirationMinutes = 60,
            RefreshTokenExpirationDays = 7
        };
    }

    [TestMethod]
    public async Task RegisterAsync_WhenEnabled_ReturnsTokensAndUser()
    {
        var userRepository = new InMemoryUserRepository();
        var registrationSettings = new RegistrationSettings { Enabled = true };
        var authService = new AuthService(
            userRepository,
            new InMemoryRefreshTokenRepository(),
            CreateJwtSettings(),
            registrationSettings,
            NullLogger<AuthService>.Instance,
            NullObservabilityMetrics.Instance);

        var request = new RegisterRequest
        {
            Email = $"test-{Guid.NewGuid():N}@example.com",
            Password = "Password123!",
            DisplayName = "Unit User"
        };

        var response = await authService.RegisterAsync(request);

        Assert.IsFalse(string.IsNullOrWhiteSpace(response.AccessToken));
        Assert.IsFalse(string.IsNullOrWhiteSpace(response.RefreshToken));
        Assert.AreEqual(request.Email, response.User.Email);
    }

    [TestMethod]
    public async Task LoginAsync_WithValidCredentials_ReturnsTokens()
    {
        var userRepository = new InMemoryUserRepository();
        var registrationSettings = new RegistrationSettings { Enabled = true };
        var authService = new AuthService(
            userRepository,
            new InMemoryRefreshTokenRepository(),
            CreateJwtSettings(),
            registrationSettings,
            NullLogger<AuthService>.Instance,
            NullObservabilityMetrics.Instance);

        var registerRequest = new RegisterRequest
        {
            Email = $"test-{Guid.NewGuid():N}@example.com",
            Password = "Password123!",
            DisplayName = "Unit User"
        };

        await authService.RegisterAsync(registerRequest);

        var loginResponse = await authService.LoginAsync(new LoginRequest
        {
            Email = registerRequest.Email,
            Password = registerRequest.Password
        });

        Assert.IsFalse(string.IsNullOrWhiteSpace(loginResponse.AccessToken));
        Assert.IsFalse(string.IsNullOrWhiteSpace(loginResponse.RefreshToken));
    }

    [TestMethod]
    public async Task RefreshTokenAsync_ReturnsNewTokens()
    {
        var userRepository = new InMemoryUserRepository();
        var registrationSettings = new RegistrationSettings { Enabled = true };
        var authService = new AuthService(
            userRepository,
            new InMemoryRefreshTokenRepository(),
            CreateJwtSettings(),
            registrationSettings,
            NullLogger<AuthService>.Instance,
            NullObservabilityMetrics.Instance);

        var registerRequest = new RegisterRequest
        {
            Email = $"test-{Guid.NewGuid():N}@example.com",
            Password = "Password123!",
            DisplayName = "Unit User"
        };

        var registerResponse = await authService.RegisterAsync(registerRequest);

        var refreshResponse = await authService.RefreshTokenAsync(new RefreshTokenRequest
        {
            RefreshToken = registerResponse.RefreshToken
        });

        Assert.IsFalse(string.IsNullOrWhiteSpace(refreshResponse.AccessToken));
        Assert.IsFalse(string.IsNullOrWhiteSpace(refreshResponse.RefreshToken));
    }

    [TestMethod]
    public async Task RegisterAsync_WhenDisabled_Throws()
    {
        var userRepository = new InMemoryUserRepository();
        var registrationSettings = new RegistrationSettings
        {
            Enabled = false,
            DisabledMessage = "Registrations disabled"
        };

        var authService = new AuthService(
            userRepository,
            new InMemoryRefreshTokenRepository(),
            CreateJwtSettings(),
            registrationSettings,
            NullLogger<AuthService>.Instance,
            NullObservabilityMetrics.Instance);

        var request = new RegisterRequest
        {
            Email = $"test-{Guid.NewGuid():N}@example.com",
            Password = "Password123!",
            DisplayName = "Unit User"
        };

        try
        {
            await authService.RegisterAsync(request);
            Assert.Fail("Expected InvalidOperationException to be thrown.");
        }
        catch (InvalidOperationException)
        {
            // Expected
        }
    }
}
