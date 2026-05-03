using Crs.Api.DTOs.Sources.Requests;
using Crs.Api.DTOs.Sources.Responses;
using Crs.Core.Entities;
using Crs.Core.Enums;
using Crs.Core.Interfaces;

namespace Crs.Api.Services;

/// <summary>
/// Service for managing sources.
/// </summary>
public class SourceService : ISourceService
{
    private readonly ISourceRepository _sourceRepository;
    private readonly IUserRepository _userRepository;

    public SourceService(ISourceRepository sourceRepository, IUserRepository userRepository)
    {
        _sourceRepository = sourceRepository;
        _userRepository = userRepository;
    }

    public async Task<SourceResponse?> GetSourceByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var source = await _sourceRepository.GetByIdAsync(id, cancellationToken);
        return source == null ? null : MapToResponse(source);
    }

    public async Task<List<SourceResponse>> GetUserSourcesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var sources = await _sourceRepository.GetByUserIdAsync(userId, cancellationToken);
        return sources.Select(MapToResponse).ToList();
    }

    public async Task<List<SourceResponse>> GetActiveUserSourcesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var sources = await _sourceRepository.GetActiveByUserIdAsync(userId, cancellationToken);
        return sources.Select(MapToResponse).ToList();
    }

    public async Task<List<SourceResponse>> GetSourcesByCategoryAsync(ContentType category, CancellationToken cancellationToken = default)
    {
        var sources = await _sourceRepository.GetByCategoryAsync(category, cancellationToken);
        return sources.Select(MapToResponse).ToList();
    }

    public async Task<SourceResponse> CreateSourceAsync(Guid userId, CreateSourceRequest request, CancellationToken cancellationToken = default)
    {
        // Verify user exists
        var user = await _userRepository.GetByIdAsync(userId, cancellationToken);
        if (user == null)
        {
            throw new KeyNotFoundException($"User with ID {userId} not found.");
        }

        // Check if URL already exists for this user
        var urlExists = await _sourceRepository.UrlExistsForUserAsync(userId, request.Url, cancellationToken);
        if (urlExists)
        {
            throw new InvalidOperationException($"A source with URL '{request.Url}' already exists for this user.");
        }

        var source = new Source
        {
            UserId = userId,
            Name = request.Name,
            Url = request.Url,
            Description = request.Description,
            Category = request.Category,
            IsActive = request.IsActive
        };

        var createdSource = await _sourceRepository.AddAsync(source, cancellationToken);
        return MapToResponse(createdSource);
    }

    public async Task<SourceResponse> UpdateSourceAsync(Guid id, UpdateSourceRequest request, CancellationToken cancellationToken = default)
    {
        var source = await _sourceRepository.GetByIdAsync(id, cancellationToken);
        if (source == null)
        {
            throw new KeyNotFoundException($"Source with ID {id} not found.");
        }

        // Update only provided fields
        if (request.Name != null)
        {
            source.Name = request.Name;
        }

        if (request.Url != null)
        {
            // Check if the new URL already exists for this user (excluding current source)
            var urlExists = await _sourceRepository.UrlExistsForUserAsync(source.UserId, request.Url, cancellationToken);
            if (urlExists && source.Url != request.Url)
            {
                throw new InvalidOperationException($"A source with URL '{request.Url}' already exists for this user.");
            }
            source.Url = request.Url;
        }

        if (request.Description != null)
        {
            source.Description = request.Description;
        }

        if (request.Category.HasValue)
        {
            source.Category = request.Category.Value;
        }

        if (request.IsActive.HasValue)
        {
            source.IsActive = request.IsActive.Value;
        }

        await _sourceRepository.UpdateAsync(source, cancellationToken);
        return MapToResponse(source);
    }

    public async Task DeleteSourceAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var source = await _sourceRepository.GetByIdAsync(id, cancellationToken);
        if (source == null)
        {
            throw new KeyNotFoundException($"Source with ID {id} not found.");
        }

        await _sourceRepository.DeleteAsync(id, cancellationToken);
    }

    public async Task<BulkImportResult> BulkImportSourcesAsync(Guid userId, BulkImportSourcesRequest request, CancellationToken cancellationToken = default)
    {
        // Verify user exists
        var user = await _userRepository.GetByIdAsync(userId, cancellationToken);
        if (user == null)
        {
            throw new KeyNotFoundException($"User with ID {userId} not found.");
        }

        var result = new BulkImportResult();

        foreach (var item in request.Sources)
        {
            try
            {
                // Check if source already exists for this user
                var urlExists = await _sourceRepository.UrlExistsForUserAsync(userId, item.Url, cancellationToken);
                if (urlExists)
                {
                    result.Failed++;
                    result.Errors.Add(new BulkImportError
                    {
                        Url = item.Url,
                        Error = "Source with this URL already exists"
                    });
                    continue;
                }

                // Create the source
                var source = new Source
                {
                    UserId = userId,
                    Name = item.Name,
                    Url = item.Url,
                    Description = item.Description,
                    Category = item.Category,
                    IsActive = true
                };

                await _sourceRepository.AddAsync(source, cancellationToken);
                result.Imported++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add(new BulkImportError
                {
                    Url = item.Url,
                    Error = ex.Message
                });
            }
        }

        return result;
    }

    private static SourceResponse MapToResponse(Source source)
    {
        return new SourceResponse
        {
            Id = source.Id,
            UserId = source.UserId,
            Name = source.Name,
            Url = source.Url,
            Description = source.Description,
            Category = source.Category,
            IsActive = source.IsActive,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
            ContentCount = source.Content?.Count ?? 0
        };
    }
}

