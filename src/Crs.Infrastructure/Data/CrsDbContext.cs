using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Crs.Core.Entities;
using Crs.Infrastructure.Data.Entities;

namespace Crs.Infrastructure.Data;

/// <summary>
/// Entity Framework Core DbContext for the Content Recommendation System.
/// Manages database connections and entity configurations.
/// </summary>
public class CrsDbContext : DbContext, IDataProtectionKeyContext
{
    public CrsDbContext(DbContextOptions<CrsDbContext> options) : base(options)
    {
    }

    // Entity DbSets
    public DbSet<User> Users { get; set; }
    public DbSet<Source> Sources { get; set; }
    public DbSet<Content> Content { get; set; }
    public DbSet<Paper> Papers { get; set; }
    public DbSet<Video> Videos { get; set; }
    public DbSet<BlogPost> BlogPosts { get; set; }
    public DbSet<ContentVote> ContentVotes { get; set; }
    public DbSet<ManualContentFeedback> ManualContentFeedback { get; set; }
    public DbSet<Recommendation> Recommendations { get; set; }
    public DbSet<XConnection> XConnections { get; set; }
    public DbSet<XFollowedAccount> XFollowedAccounts { get; set; }
    public DbSet<XSelectedAccount> XSelectedAccounts { get; set; }
    public DbSet<XPost> XPosts { get; set; }
    public DbSet<XAuthState> XAuthStates { get; set; }
    public DbSet<RefreshToken> RefreshTokens { get; set; }
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; }
    public DbSet<ContentEmbedding> ContentEmbeddings { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasPostgresExtension("vector");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CrsDbContext).Assembly);
    }
}
