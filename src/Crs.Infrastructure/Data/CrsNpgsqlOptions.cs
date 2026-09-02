using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace Crs.Infrastructure.Data;

/// <summary>
/// Shared Npgsql + pgvector DbContext setup for API, jobs, and tests.
/// </summary>
public static class CrsNpgsqlOptions
{
    public static DbContextOptionsBuilder UseCrsNpgsql(
        this DbContextOptionsBuilder options,
        string connectionString)
    {
        return options.UseNpgsql(
            connectionString,
            npgsqlOptions =>
            {
                npgsqlOptions.EnableRetryOnFailure();
                npgsqlOptions.UseVector();
            });
    }

    public static DbContextOptionsBuilder<TContext> UseCrsNpgsql<TContext>(
        this DbContextOptionsBuilder<TContext> options,
        string connectionString)
        where TContext : DbContext
    {
        return options.UseNpgsql(
            connectionString,
            npgsqlOptions =>
            {
                npgsqlOptions.EnableRetryOnFailure();
                npgsqlOptions.UseVector();
            });
    }
}
