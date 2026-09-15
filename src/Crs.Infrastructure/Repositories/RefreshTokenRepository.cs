using System.Data;
using Microsoft.EntityFrameworkCore;
using Crs.Core.Entities;
using Crs.Core.Interfaces;
using Crs.Infrastructure.Data;

namespace Crs.Infrastructure.Repositories;

/// <summary>
/// Implementation of IRefreshTokenRepository.
/// </summary>
public class RefreshTokenRepository : IRefreshTokenRepository
{
    private readonly CrsDbContext _context;

    public RefreshTokenRepository(CrsDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(RefreshToken token, CancellationToken cancellationToken = default)
    {
        _context.RefreshTokens.Add(token);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<RefreshToken?> GetAndRemoveAsync(string token, CancellationToken cancellationToken = default)
    {
        // Rotate under Serializable so two concurrent refreshes cannot both mint
        // new tokens from the same refresh token (multi-tab / multi-instance race).
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            try
            {
                var entity = await _context.RefreshTokens
                    .FirstOrDefaultAsync(x => x.Token == token, cancellationToken);

                if (entity == null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return null;
                }

                _context.RefreshTokens.Remove(entity);
                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return entity;
            }
            catch (DbUpdateException)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
            catch (InvalidOperationException)
            {
                // Serialization conflict from a concurrent consumer of the same token.
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        });
    }

    public async Task RemoveExpiredAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow;
        await _context.RefreshTokens
            .Where(x => x.ExpiresAt <= cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
