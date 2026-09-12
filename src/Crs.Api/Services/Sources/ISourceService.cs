using Crs.Api.DTOs.Sources.Requests;
using Crs.Api.DTOs.Sources.Responses;
using Crs.Core.Enums;

namespace Crs.Api.Services;

/// <summary>
/// Service interface for managing sources.
/// </summary>
public interface ISourceService
{
    /// <summary>
    /// Gets a source by ID.
    /// </summary>
    Task<SourceResponse?> GetSourceByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all sources for a specific user.
    /// </summary>
    Task<List<SourceResponse>> GetUserSourcesAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all active sources for a specific user.
    /// </summary>
    Task<List<SourceResponse>> GetActiveUserSourcesAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets sources by category for a specific user.
    /// </summary>
    Task<List<SourceResponse>> GetSourcesByCategoryAsync(Guid userId, ContentType category, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new source.
    /// </summary>
    Task<SourceResponse> CreateSourceAsync(Guid userId, CreateSourceRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing source owned by the user.
    /// </summary>
    Task<SourceResponse> UpdateSourceAsync(Guid userId, Guid id, UpdateSourceRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a source owned by the user.
    /// </summary>
    Task DeleteSourceAsync(Guid userId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk imports multiple sources.
    /// </summary>
    Task<BulkImportResult> BulkImportSourcesAsync(Guid userId, BulkImportSourcesRequest request, CancellationToken cancellationToken = default);
}

