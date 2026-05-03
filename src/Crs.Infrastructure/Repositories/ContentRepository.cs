using Microsoft.EntityFrameworkCore;
using Crs.Core.Entities;
using Crs.Core.Enums;
using Crs.Core.Exceptions;
using Crs.Core.Interfaces;
using Crs.Infrastructure.Data;

namespace Crs.Infrastructure.Repositories;

/// <summary>
/// Implementation of IContentRepository using Entity Framework Core.
/// </summary>
public class ContentRepository : IContentRepository
{
    private readonly CrsDbContext _context;

    public ContentRepository(CrsDbContext context)
    {
        _context = context;
    }

    public async Task<Content?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Content
            .Include(r => r.Source)
            .Include(r => r.Votes)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    }

    public async Task<IEnumerable<Content>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default)
    {
        var idList = ids.ToList();
        return await _context.Content
            .Where(r => idList.Contains(r.Id))
            .Include(r => r.Source)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Content>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Content
            .Include(r => r.Source)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Content>> GetByTypeAsync(ContentType type, CancellationToken cancellationToken = default)
    {
        return await _context.Content
            .Where(r => r.Type == type)
            .Include(r => r.Source)
            .ToListAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<Content> Items, int TotalCount)> GetPagedAsync(
        int pageNumber,
        int pageSize,
        ContentType? type,
        IReadOnlyCollection<Guid>? sourceIds,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Content> query = _context.Content;

        if (type.HasValue)
        {
            query = query.Where(c => c.Type == type.Value);
        }

        if (sourceIds is { Count: > 0 })
        {
            query = query.Where(c => c.SourceId.HasValue && sourceIds.Contains(c.SourceId.Value));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Include(c => c.Source)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task<IEnumerable<Content>> GetByTopicAsync(Guid topicId, CancellationToken cancellationToken = default)
    {
        // Topics no longer exist - return empty list for backward compatibility
        return await Task.FromResult(Enumerable.Empty<Content>());
    }

    public async Task<IEnumerable<Content>> GetByTopicsAsync(IEnumerable<Guid> topicIds, CancellationToken cancellationToken = default)
    {
        // Topics no longer exist - return empty list for backward compatibility
        return await Task.FromResult(Enumerable.Empty<Content>());
    }

    public async Task<Content> CreateAsync(Content content, CancellationToken cancellationToken = default)
    {
        _context.Content.Add(content);
        await _context.SaveChangesAsync(cancellationToken);
        return content;
    }

    public async Task<Content> UpdateAsync(Content content, CancellationToken cancellationToken = default)
    {
        _context.Content.Update(content);
        await _context.SaveChangesAsync(cancellationToken);
        return content;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var content = await _context.Content.FindAsync(new object[] { id }, cancellationToken);
        if (content != null)
        {
            _context.Content.Remove(content);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Content.AnyAsync(r => r.Id == id, cancellationToken);
    }

    public async Task<bool> ExistsByUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        return await _context.Content.AnyAsync(r => r.Url == url, cancellationToken);
    }

    public async Task AddAsync(Content content, CancellationToken cancellationToken = default)
    {
        _context.Content.Add(content);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Detach so the change tracker doesn't retry the failed insert on the next SaveChanges.
            _context.Content.Remove(content);
            throw new DuplicateContentException(content.Url, ex);
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        var inner = ex.InnerException?.Message;
        if (string.IsNullOrEmpty(inner))
        {
            return false;
        }

        return inner.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
            || inner.Contains("unique constraint", StringComparison.OrdinalIgnoreCase)
            || inner.Contains("UNIQUE", StringComparison.Ordinal);
    }
}

