using Testcontainers.PostgreSql;

namespace Crs.Tests.Infrastructure;

public static class PostgresTestContainerFixture
{
    private static PostgreSqlContainer? _container;

    public static string ConnectionString =>
        _container?.GetConnectionString()
        ?? throw new InvalidOperationException("Postgres test container is not started.");

    public static async Task StartAsync()
    {
        if (_container != null)
        {
            return;
        }

        _container = new PostgreSqlBuilder("pgvector/pgvector:pg16")
            .WithDatabase("crsdb_test")
            .WithUsername("crsadmin")
            .WithPassword("YourStrong@Passw0rd")
            .Build();

        await _container.StartAsync();
    }

    public static async Task StopAsync()
    {
        if (_container == null)
        {
            return;
        }

        await _container.StopAsync();
        await _container.DisposeAsync();
        _container = null;
    }
}
