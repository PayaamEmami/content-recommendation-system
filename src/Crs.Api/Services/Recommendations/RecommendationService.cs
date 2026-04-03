using Crs.Api.DTOs.Recommendations.Responses;
using Crs.Api.DTOs.Content.Responses;
using Crs.Api.DTOs.Sources.Responses;
using Crs.Core.Enums;
using Crs.Core.Interfaces;

namespace Crs.Api.Services;

/// <summary>
/// Service for handling recommendation operations.
/// </summary>
public class RecommendationService : IRecommendationService
{
  private readonly IRecommendationRepository _recommendationRepository;
  private readonly IUserRepository _userRepository;
  private readonly ILogger<RecommendationService> _logger;

  public RecommendationService(
      IRecommendationRepository recommendationRepository,
      IUserRepository userRepository,
      ILogger<RecommendationService> logger)
  {
    _recommendationRepository = recommendationRepository;
    _userRepository = userRepository;
    _logger = logger;
  }

  public async Task<FeedRecommendationsResponse> GetFeedRecommendationsAsync(
      Guid userId,
      ContentType feedType,
      DateOnly date,
      CancellationToken cancellationToken = default)
  {
    if (!await _userRepository.ExistsAsync(userId, cancellationToken))
    {
      throw new KeyNotFoundException($"User with ID {userId} not found");
    }

    // Get recommendations for this user, feed type, and date
    var recommendations = await _recommendationRepository.GetByUserDateAndTypeAsync(
        userId,
        date,
        feedType,
        cancellationToken);

    var effectiveDate = date;

    // If no recommendations for requested date, fall back to the most recent date
    if (!recommendations.Any())
    {
      var mostRecentDate = await _recommendationRepository.GetMostRecentDateWithRecommendationsAsync(
          userId,
          feedType,
          cancellationToken);

      if (mostRecentDate.HasValue)
      {
        recommendations = await _recommendationRepository.GetByUserDateAndTypeAsync(
            userId,
            mostRecentDate.Value,
            feedType,
            cancellationToken);
        effectiveDate = mostRecentDate.Value;

        _logger.LogInformation(
            "No recommendations for {Date} for feed {FeedType}, using {FallbackDate}",
            date, feedType, mostRecentDate.Value);
      }
    }

    return new FeedRecommendationsResponse
    {
      FeedType = feedType,
      Date = effectiveDate,
      Recommendations = recommendations
            .OrderBy(r => r.Position)
            .Select(r => new RecommendationResponse
            {
              Id = r.Id,
              Content = new ContentResponse
              {
                Id = r.Content.Id,
                Title = r.Content.Title,
                Description = r.Content.Description,
                Url = r.Content.Url,
                Type = r.Content.Type,
                PublishedDate = null,
                CreatedAt = r.Content.CreatedAt,
                UpdatedAt = r.Content.UpdatedAt,
                SourceInfo = r.Content.Source != null ? new SourceResponse
                {
                  Id = r.Content.Source.Id,
                  UserId = r.Content.Source.UserId,
                  Name = r.Content.Source.Name,
                  Url = r.Content.Source.Url,
                  Description = r.Content.Source.Description,
                  Category = r.Content.Source.Category,
                  IsActive = r.Content.Source.IsActive,
                  CreatedAt = r.Content.Source.CreatedAt,
                  UpdatedAt = r.Content.Source.UpdatedAt,
                  ContentCount = r.Content.Source.Content?.Count ?? 0
                } : null
              },
              Position = r.Position,
              Score = r.Score,
              GeneratedAt = r.GeneratedAt
            })
            .ToList()
    };
  }

  public async Task<List<FeedRecommendationsResponse>> GetTodaysRecommendationsAsync(
      Guid userId,
      CancellationToken cancellationToken = default)
  {
    if (!await _userRepository.ExistsAsync(userId, cancellationToken))
    {
      throw new KeyNotFoundException($"User with ID {userId} not found");
    }

    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    var allFeedTypes = Enum.GetValues<ContentType>();

    var feedRecommendations = new List<FeedRecommendationsResponse>();

    foreach (var feedType in allFeedTypes)
    {
      // First try today's recommendations
      var recommendations = await _recommendationRepository.GetByUserDateAndTypeAsync(
          userId,
          today,
          feedType,
          cancellationToken);

      var effectiveDate = today;

      // If no recommendations for today, fall back to the most recent date
      if (!recommendations.Any())
      {
        var mostRecentDate = await _recommendationRepository.GetMostRecentDateWithRecommendationsAsync(
            userId,
            feedType,
            cancellationToken);

        if (mostRecentDate.HasValue)
        {
          recommendations = await _recommendationRepository.GetByUserDateAndTypeAsync(
              userId,
              mostRecentDate.Value,
              feedType,
              cancellationToken);
          effectiveDate = mostRecentDate.Value;

          _logger.LogInformation(
              "No recommendations for today ({Today}) for feed {FeedType}, using {FallbackDate}",
              today, feedType, mostRecentDate.Value);
        }
      }

      if (recommendations.Any())
      {
        feedRecommendations.Add(new FeedRecommendationsResponse
        {
          FeedType = feedType,
          Date = effectiveDate,
          Recommendations = recommendations
                .OrderBy(r => r.Position)
                .Select(r => new RecommendationResponse
                {
                  Id = r.Id,
                  Content = new ContentResponse
                  {
                    Id = r.Content.Id,
                    Title = r.Content.Title,
                    Description = r.Content.Description,
                    Url = r.Content.Url,
                    Type = r.Content.Type,
                    PublishedDate = null,
                    CreatedAt = r.Content.CreatedAt,
                    UpdatedAt = r.Content.UpdatedAt,
                    SourceInfo = r.Content.Source != null ? new SourceResponse
                    {
                      Id = r.Content.Source.Id,
                      UserId = r.Content.Source.UserId,
                      Name = r.Content.Source.Name,
                      Url = r.Content.Source.Url,
                      Description = r.Content.Source.Description,
                      Category = r.Content.Source.Category,
                      IsActive = r.Content.Source.IsActive,
                      CreatedAt = r.Content.Source.CreatedAt,
                      UpdatedAt = r.Content.Source.UpdatedAt,
                      ContentCount = r.Content.Source.Content?.Count ?? 0
                    } : null
                  },
                  Position = r.Position,
                  Score = r.Score,
                  GeneratedAt = r.GeneratedAt
                })
                .ToList()
        });
      }
    }

    return feedRecommendations;
  }
}
