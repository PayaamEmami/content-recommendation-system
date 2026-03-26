using System.Text;
using Asp.Versioning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Crs.Api.Configuration;
using Crs.Core.Observability;

namespace Crs.Api.Extensions;

/// <summary>
/// Extension methods for configuring services in the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Configures JWT authentication.
    /// </summary>
    public static IServiceCollection AddJwtAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var jwtSettings = configuration.GetSection("JwtSettings").Get<JwtSettings>()
            ?? throw new InvalidOperationException("JWT settings are not configured");

        services.AddSingleton(jwtSettings);

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = context =>
                {
                    var metrics = context.HttpContext.RequestServices.GetRequiredService<IObservabilityMetrics>();
                    metrics.Increment(
                        "auth.failure.count",
                        context: new MetricContext(
                            Dimensions: new Dictionary<string, string>
                            {
                                ["Operation"] = "jwt",
                                ["Outcome"] = "authentication_failed",
                                ["StatusClass"] = "4xx"
                            },
                            Properties: new Dictionary<string, object?>
                            {
                                ["Error"] = context.Exception.GetType().Name
                            }));

                    return Task.CompletedTask;
                },
                OnChallenge = context =>
                {
                    var metrics = context.HttpContext.RequestServices.GetRequiredService<IObservabilityMetrics>();
                    metrics.Increment(
                        "auth.failure.count",
                        context: new MetricContext(
                            Dimensions: new Dictionary<string, string>
                            {
                                ["Operation"] = "jwt",
                                ["Outcome"] = "challenge",
                                ["StatusClass"] = "4xx"
                            }));

                    return Task.CompletedTask;
                }
            };
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtSettings.Issuer,
                ValidAudience = jwtSettings.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(jwtSettings.SecretKey)),
                ClockSkew = TimeSpan.Zero // Remove default 5 minute tolerance
            };
        });

        return services;
    }

    /// <summary>
    /// Configures API versioning.
    /// </summary>
    public static IServiceCollection AddApiVersioningConfiguration(this IServiceCollection services)
    {
        services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ReportApiVersions = true;
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
        })
        .AddApiExplorer(options =>
        {
            options.GroupNameFormat = "'v'V";
            options.SubstituteApiVersionInUrl = true;
        });

        return services;
    }

    /// <summary>
    /// Configures OpenAPI documentation.
    /// </summary>
    public static IServiceCollection AddOpenApiConfiguration(this IServiceCollection services)
    {
        services.AddOpenApi();
        return services;
    }

    /// <summary>
    /// Configures CORS policy.
    /// </summary>
    public static IServiceCollection AddCorsConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? new[] { "http://localhost:5173" };

        services.AddCors(options =>
        {
            options.AddPolicy("DefaultCorsPolicy", builder =>
            {
                builder.WithOrigins(allowedOrigins)
                    .AllowAnyMethod()
                    .AllowAnyHeader()
                    .AllowCredentials();
            });
        });

        return services;
    }

    /// <summary>
    /// Configures registration settings.
    /// </summary>
    public static IServiceCollection AddRegistrationSettings(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var registrationSettings = configuration.GetSection("Registration").Get<RegistrationSettings>()
            ?? new RegistrationSettings();

        services.AddSingleton(registrationSettings);

        return services;
    }
}
