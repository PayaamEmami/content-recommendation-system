using Crs.Core.Entities;
using Crs.Core.Enums;

namespace Crs.Core.Interfaces;

/// <summary>
/// Repository interface for Content entity operations.
/// </summary>
public interface IContentRepository
{
    Task<Content?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IEnumerable<Content>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default);
    Task<IEnumerable<Content>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IEnumerable<Content>> GetByTypeAsync(ContentType type, CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<Content> Items, int TotalCount)> GetPagedAsync(
        int pageNumber,
        int pageSize,
        ContentType? type,
        IReadOnlyCollection<Guid>? sourceIds,
        CancellationToken cancellationToken = default);
    Task<IEnumerable<Content>> GetByTopicAsync(Guid topicId, CancellationToken cancellationToken = default);
    Task<IEnumerable<Content>> GetByTopicsAsync(IEnumerable<Guid> topicIds, CancellationToken cancellationToken = default);
    Task<Content> CreateAsync(Content content, CancellationToken cancellationToken = default);
    Task<Content> UpdateAsync(Content content, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> ExistsByUrlAsync(string url, CancellationToken cancellationToken = default);
    Task AddAsync(Content content, CancellationToken cancellationToken = default);
}

