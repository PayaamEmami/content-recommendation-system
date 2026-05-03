using Crs.Api.DTOs.Recommendations.Responses;
using Crs.Api.DTOs.Content.Responses;
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

    return await BuildFeedRecommendationsAsync(userId, feedType, date, cancellationToken);
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
    var feedRecommendations = new List<FeedRecommendationsResponse>();

    foreach (var feedType in Enum.GetValues<ContentType>())
    {
      var feed = await BuildFeedRecommendationsAsync(userId, feedType, today, cancellationToken);
      if (feed.Recommendations.Count > 0)
      {
        feedRecommendations.Add(feed);
      }
    }

    return feedRecommendations;
  }

  /// <summary>
  /// Builds the feed-recommendations response for a single (user, feedType, date) tuple,
  /// falling back to the most recent date with recommendations if the requested date has none.
  /// Skips the user-existence check; callers are expected to validate that once up front.
  /// </summary>
  private async Task<FeedRecommendationsResponse> BuildFeedRecommendationsAsync(
      Guid userId,
      ContentType feedType,
      DateOnly date,
      CancellationToken cancellationToken)
  {
    var recommendations = await _recommendationRepository.GetByUserDateAndTypeAsync(
        userId,
        date,
        feedType,
        cancellationToken);

    var effectiveDate = date;

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
              Content = ContentResponse.FromEntity(r.Content),
              Position = r.Position,
              Score = r.Score,
              GeneratedAt = r.GeneratedAt
            })
            .ToList()
    };
  }
}
