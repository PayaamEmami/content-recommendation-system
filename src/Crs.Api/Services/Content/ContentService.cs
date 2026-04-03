using Crs.Api.DTOs.Common;
using Crs.Api.DTOs.Content.Requests;
using Crs.Api.DTOs.Content.Responses;
using Crs.Api.DTOs.Sources.Responses;
using Crs.Core.Entities;
using Crs.Core.Enums;
using Crs.Core.Interfaces;

namespace Crs.Api.Services;

/// <summary>
/// Service for handling content-related operations.
/// </summary>
public class ContentService : IContentService
{
    private readonly IContentRepository _contentRepository;
    private readonly ISourceRepository _sourceRepository;
    private readonly ILogger<ContentService> _logger;

    public ContentService(
        IContentRepository contentRepository,
        ISourceRepository sourceRepository,
        ILogger<ContentService> logger)
    {
        _contentRepository = contentRepository;
        _sourceRepository = sourceRepository;
        _logger = logger;
    }

    public async Task<PagedResponse<ContentResponse>> GetContentAsync(
        int pageNumber,
        int pageSize,
        ContentType? type = null,
        List<Guid>? sourceIds = null,
        CancellationToken cancellationToken = default)
    {
        // Get content (apply type filter if specified)
        IEnumerable<Content> content;

        if (type.HasValue)
        {
            content = await _contentRepository.GetByTypeAsync(type.Value, cancellationToken);
        }
        else
        {
            content = await _contentRepository.GetAllAsync(cancellationToken);
        }

        // Apply source filter if specified
        if (sourceIds != null && sourceIds.Any())
        {
            content = content.Where(r => r.SourceId.HasValue && sourceIds.Contains(r.SourceId.Value));
        }

        // Get total count before pagination
        var totalCount = content.Count();

        // Apply pagination
        var pagedContent = content
            .OrderByDescending(r => r.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new PagedResponse<ContentResponse>
        {
            Items = pagedContent.Select(MapToContentResponse).ToList(),
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize)
        };
    }

    public async Task<ContentResponse?> GetContentByIdAsync(Guid contentId, CancellationToken cancellationToken = default)
    {
        var content = await _contentRepository.GetByIdAsync(contentId, cancellationToken);

        if (content == null)
        {
            return null;
        }

        return MapToContentResponse(content);
    }

    public async Task<ContentResponse> CreateContentAsync(
        CreateContentRequest request,
        CancellationToken cancellationToken = default)
    {
        // Validate source if provided
        if (request.SourceId.HasValue)
        {
            var source = await _sourceRepository.GetByIdAsync(request.SourceId.Value, cancellationToken);
            if (source == null)
            {
                throw new ArgumentException($"Source with ID {request.SourceId} not found");
            }
        }

        // Create content based on type
        Content content = request.ContentType switch
        {
            ContentType.Paper => new Paper(),
            ContentType.Video => new Video(),
            ContentType.BlogPost => new BlogPost(),
            _ => throw new ArgumentException($"Invalid content type: {request.ContentType}")
        };

        // Set common properties
        content.Id = Guid.NewGuid();
        content.Title = request.Title;
        content.Description = request.Description;
        content.Url = request.Url;
        content.SourceId = request.SourceId;
        content.CreatedAt = DateTime.UtcNow;
        content.UpdatedAt = DateTime.UtcNow;

        await _contentRepository.CreateAsync(content, cancellationToken);

        _logger.LogInformation("Created content {ContentId} of type {ContentType}", content.Id, content.Type);

        return MapToContentResponse(content);
    }

    public async Task<ContentResponse> UpdateContentAsync(
        Guid contentId,
        UpdateContentRequest request,
        CancellationToken cancellationToken = default)
    {
        var content = await _contentRepository.GetByIdAsync(contentId, cancellationToken);
        if (content == null)
        {
            throw new KeyNotFoundException($"Content with ID {contentId} not found");
        }

        // Update fields if provided
        if (request.Title != null)
        {
            content.Title = request.Title;
        }

        if (request.Description != null)
        {
            content.Description = request.Description;
        }

        if (request.Url != null)
        {
            content.Url = request.Url;
        }

        // Update source if provided
        if (request.SourceId.HasValue)
        {
            var source = await _sourceRepository.GetByIdAsync(request.SourceId.Value, cancellationToken);
            if (source == null)
            {
                throw new ArgumentException($"Source with ID {request.SourceId} not found");
            }
            content.SourceId = request.SourceId;
        }

        content.UpdatedAt = DateTime.UtcNow;

        await _contentRepository.UpdateAsync(content, cancellationToken);

        _logger.LogInformation("Updated content {ContentId}", contentId);

        return MapToContentResponse(content);
    }

    public async Task DeleteContentAsync(Guid contentId, CancellationToken cancellationToken = default)
    {
        var content = await _contentRepository.GetByIdAsync(contentId, cancellationToken);
        if (content == null)
        {
            throw new KeyNotFoundException($"Content with ID {contentId} not found");
        }

        await _contentRepository.DeleteAsync(contentId, cancellationToken);

        _logger.LogInformation("Deleted content {ContentId}", contentId);
    }

    private static ContentResponse MapToContentResponse(Content content)
    {
        return new ContentResponse
        {
            Id = content.Id,
            Title = content.Title,
            Description = content.Description,
            Url = content.Url,
            Type = content.Type,
            PublishedDate = null,
            CreatedAt = content.CreatedAt,
            UpdatedAt = content.UpdatedAt,
            SourceInfo = content.Source != null ? new SourceResponse
            {
                Id = content.Source.Id,
                UserId = content.Source.UserId,
                Name = content.Source.Name,
                Url = content.Source.Url,
                Description = content.Source.Description,
                Category = content.Source.Category,
                IsActive = content.Source.IsActive,
                CreatedAt = content.Source.CreatedAt,
                UpdatedAt = content.Source.UpdatedAt,
                ContentCount = content.Source.Content?.Count ?? 0
            } : null
        };
    }
}
