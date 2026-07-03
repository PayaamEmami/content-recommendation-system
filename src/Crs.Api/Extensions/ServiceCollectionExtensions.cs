using System.Net;
using System.Text;
using Asp.Versioning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Crs.Api.Configuration;
using Crs.Api.Services;
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
    /// Registers the API application services, mirroring <c>AddInfrastructure</c> and
    /// <c>AddLlmServices</c> so <c>Program.cs</c> stays declarative.
    /// </summary>
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<ISourceService, SourceService>();
        services.AddScoped<IContentService, ContentService>();
        services.AddScoped<IVoteService, VoteService>();
        services.AddScoped<IPreferenceService, PreferenceService>();
        services.AddScoped<IRecommendationService, RecommendationService>();
        services.AddScoped<IXAccountService, XAccountService>();

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

    /// <summary>
    /// Configures trusted reverse proxy networks for forwarded headers.
    /// </summary>
    public static IServiceCollection AddReverseProxyConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var reverseProxySettings = configuration.GetSection("ReverseProxy").Get<ReverseProxySettings>()
            ?? new ReverseProxySettings();

        services.AddSingleton(reverseProxySettings);
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                                       ForwardedHeaders.XForwardedHost |
                                       ForwardedHeaders.XForwardedProto;
            options.RequireHeaderSymmetry = false;
            options.ForwardLimit = 1;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();

            foreach (var network in reverseProxySettings.KnownNetworks)
            {
                if (string.IsNullOrWhiteSpace(network))
                {
                    continue;
                }

                options.KnownIPNetworks.Add(ParseNetwork(network));
            }
        });

        return services;
    }

    private static System.Net.IPNetwork ParseNetwork(string cidr)
    {
        var segments = cidr.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 ||
            !IPAddress.TryParse(segments[0], out var prefix) ||
            !int.TryParse(segments[1], out var prefixLength))
        {
            throw new InvalidOperationException($"Invalid ReverseProxy:KnownNetworks value '{cidr}'. Expected CIDR notation.");
        }

        try
        {
            return new System.Net.IPNetwork(prefix, prefixLength);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Invalid ReverseProxy:KnownNetworks value '{cidr}'. Expected a valid CIDR network.",
                exception);
        }
    }
}
