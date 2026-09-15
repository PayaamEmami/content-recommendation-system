using DotNet.Testcontainers.Builders;

namespace Crs.Tests.Infrastructure;

[TestClass]
public sealed class TestAssemblyHooks
{
    public static bool PostgresContainerAvailable { get; private set; }

    [AssemblyInitialize]
    public static async Task AssemblyInitialize(TestContext context)
    {
        Environment.SetEnvironmentVariable("JwtSettings__SecretKey", "test-secret-key-for-integration-tests-only");
        Environment.SetEnvironmentVariable("JwtSettings__Issuer", "Crs.Api.Tests");
        Environment.SetEnvironmentVariable("JwtSettings__Audience", "Crs.Web.Tests");
        Environment.SetEnvironmentVariable("JwtSettings__ExpirationMinutes", "60");
        Environment.SetEnvironmentVariable("JwtSettings__RefreshTokenExpirationDays", "7");
        Environment.SetEnvironmentVariable("Registration__Enabled", "true");
        Environment.SetEnvironmentVariable("Registration__DisabledMessage", "Registrations disabled");

        try
        {
            await PostgresTestContainerFixture.StartAsync();
            PostgresContainerAvailable = true;
        }
        catch (DockerUnavailableException ex)
        {
            // Unit tests that do not touch Postgres can still run without Docker.
            PostgresContainerAvailable = false;
            context.WriteLine(
                "Skipping Postgres testcontainer startup because Docker is unavailable: {0}",
                ex.Message);
        }
    }

    [AssemblyCleanup]
    public static async Task AssemblyCleanup()
    {
        if (PostgresContainerAvailable)
        {
            await PostgresTestContainerFixture.StopAsync();
        }
    }
}
