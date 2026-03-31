using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Crs.Core.Entities;
using Crs.Core.Interfaces;
using Crs.Core.Models;
using Crs.Core.Observability;
using Crs.Recommendation.Filters;
using Crs.Recommendation.Models;
using Crs.Recommendation.Scorers;

namespace Crs.Recommendation.Engine;

/// <summary>
/// Hybrid recommendation engine that combines vector similarity with traditional scoring.
/// Primary recommendations come from vector search, with additional heuristic signals layered on top.
/// </summary>
public class RecommendationEngine : IRecommendationEngine
{
    private const double VectorSimilarityWeight = 0.70;
    private const double HeuristicWeight = 0.30;

    private readonly IVectorStore _vectorStore;
    private readonly IContentRepository _contentRepository;
    private readonly CompositeScorer _compositeScorer;
    private readonly IEnumerable<IRecommendationFilter> _filters;
    private readonly ILogger<RecommendationEngine> _logger;
    private readonly IObservabilityMetrics _metrics;

    public RecommendationEngine(
        IVectorStore vectorStore,
        IContentRepository contentRepository,
        CompositeScorer compositeScorer,
        IEnumerable<IRecommendationFilter> filters,
        ILogger<RecommendationEngine> logger,
        IObservabilityMetrics metrics)
    {
        _vectorStore = vectorStore;
        _contentRepository = contentRepository;
        _compositeScorer = compositeScorer;
        _filters = filters;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task<List<ScoredContent>> GenerateRecommendationsAsync(
        RecommendationContext context,
        CancellationToken cancellationToken = default)
    {
        using var activity = CrsTelemetry.ActivitySource.StartActivity("recommendations.generate");
        activity?.SetTag(CrsTelemetry.Tags.UserId, context.UserId.ToString());
        activity?.SetTag(CrsTelemetry.Tags.FeedType, context.FeedType.ToString());

        var startedAt = Stopwatch.StartNew();
        var fallbackCount = 0;

        _logger.LogInformation(
            "Generating {Count} recommendations for user {UserId}, feed type {FeedType}, date {Date}",
            context.Count, context.UserId, context.FeedType, context.Date);

        // Step 1: Get candidates via vector similarity (if user has embedding)
        var scoredCandidates = await GetPrimaryCandidatesAsync(context, cancellationToken);

        if (!scoredCandidates.Any())
        {
            _logger.LogWarning("No candidate content found for user {UserId}", context.UserId);
            startedAt.Stop();
            activity?.SetTag(CrsTelemetry.Tags.ResultCount, 0);
            _metrics.RecordDuration(
                "recommendations.duration",
                startedAt.Elapsed,
                BuildMetricContext(context.FeedType, "empty", 0, 0, 0));
            return new List<ScoredContent>();
        }

        var recommendations = await RankAndSelectAsync(scoredCandidates, context, cancellationToken);
        var usedFallback = false;

        if (recommendations.Count < context.Count)
        {
            usedFallback = true;
            fallbackCount++;
            _logger.LogInformation(
                "Primary recommendation pool produced {Count} items for user {UserId}. Trying older unseen content fallback.",
                recommendations.Count,
                context.UserId);

            recommendations = await BackfillRecommendationsAsync(
                recommendations,
                context,
                allowRecentRecommendations: false,
                includeOlderContent: true,
                "older unseen content",
                cancellationToken);
        }

        if (recommendations.Count < context.Count)
        {
            usedFallback = true;
            fallbackCount++;
            _logger.LogInformation(
                "Older-content fallback produced {Count} items for user {UserId}. Trying recent-repeat fallback.",
                recommendations.Count,
                context.UserId);

            recommendations = await BackfillRecommendationsAsync(
                recommendations,
                context,
                allowRecentRecommendations: true,
                includeOlderContent: false,
                "recent-repeat content",
                cancellationToken);
        }

        _logger.LogInformation(
            "Generated {Count} recommendations for user {UserId}",
            recommendations.Count, context.UserId);
        startedAt.Stop();
        activity?.SetTag(CrsTelemetry.Tags.ResultCount, recommendations.Count);
        activity?.SetTag("crs.fallback_count", fallbackCount);
        _metrics.RecordDuration(
            "recommendations.duration",
            startedAt.Elapsed,
            BuildMetricContext(
                context.FeedType,
                recommendations.Count > 0 ? "success" : "empty",
                scoredCandidates.Count,
                recommendations.Count,
                usedFallback ? 1 : 0));
        _metrics.RecordValue(
            "recommendations.candidates_examined",
            scoredCandidates.Count,
            "Count",
            BuildMetricContext(context.FeedType, "success", scoredCandidates.Count, recommendations.Count, usedFallback ? 1 : 0));
        _metrics.RecordValue(
            "recommendations.results_returned",
            recommendations.Count,
            "Count",
            BuildMetricContext(context.FeedType, "success", scoredCandidates.Count, recommendations.Count, usedFallback ? 1 : 0));
        if (usedFallback)
        {
            _metrics.Increment(
                "recommendations.fallback.count",
                context: BuildMetricContext(context.FeedType, "fallback", scoredCandidates.Count, recommendations.Count, fallbackCount));
        }

        return recommendations;
    }

    /// <summary>
    /// Get candidates using vector similarity search.
    /// </summary>
    private async Task<List<ScoredContent>> GetVectorSimilarityCandidatesAsync(
        RecommendationContext context,
        bool allowRecentRecommendations,
        bool includeOlderContent,
        HashSet<Guid>? additionalExcludedIds,
        CancellationToken cancellationToken)
    {
        try
        {
            var searchRequest = new VectorSearchRequest
            {
                QueryVector = context.UserProfile!.UserEmbedding!,
                TopK = GetVectorCandidateCount(context.Count),
                ContentType = context.FeedType,
                PublishedAfter = includeOlderContent ? null : context.Date.AddDays(-90).ToDateTime(TimeOnly.MinValue),
                ExcludeContentIds = context.SeenContentIds
                    .Union(allowRecentRecommendations ? Enumerable.Empty<Guid>() : context.RecentlyRecommendedIds)
                    .Union(additionalExcludedIds ?? Enumerable.Empty<Guid>())
                    .ToHashSet()
            };

            var searchResults = await _vectorStore.SearchAsync(searchRequest, cancellationToken);

            if (!searchResults.Any())
            {
                _logger.LogWarning(
                    "Vector search returned no results, falling back to traditional candidates");
                return await GetTraditionalCandidatesAsync(
                    context,
                    allowRecentRecommendations,
                    includeOlderContent,
                    additionalExcludedIds,
                    cancellationToken);
            }

            // Load full content entities
            var contentIds = searchResults.Select(r => r.ContentId).ToList();
            var content = await _contentRepository.GetByIdsAsync(contentIds, cancellationToken);
            var contentMap = content.ToDictionary(r => r.Id);

            // Create scored content with vector similarity as the primary score
            var scoredContent = searchResults
                .Where(sr => contentMap.ContainsKey(sr.ContentId))
                .Select(sr => new ScoredContent
                {
                    Content = contentMap[sr.ContentId],
                    Scores = new Dictionary<string, double>
                    {
                        { "vector_similarity", sr.SimilarityScore }
                    },
                    FinalScore = sr.SimilarityScore // Will be adjusted by heuristic scoring
                })
                .ToList();

            _logger.LogInformation("Retrieved {Count} candidates via vector search", scoredContent.Count);
            return scoredContent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error performing vector similarity search, falling back to traditional approach");
            return await GetTraditionalCandidatesAsync(
                context,
                allowRecentRecommendations,
                includeOlderContent,
                additionalExcludedIds,
                cancellationToken);
        }
    }

    /// <summary>
    /// Fallback method: get candidates using traditional approach (fetch recent content by type).
    /// </summary>
    private async Task<List<ScoredContent>> GetTraditionalCandidatesAsync(
        RecommendationContext context,
        bool allowRecentRecommendations,
        bool includeOlderContent,
        HashSet<Guid>? additionalExcludedIds,
        CancellationToken cancellationToken)
    {
        var candidates = await _contentRepository.GetByTypeAsync(context.FeedType, cancellationToken);
        var cutoffDate = context.Date.AddDays(-90).ToDateTime(TimeOnly.MinValue);
        var excludedIds = context.SeenContentIds
            .Union(allowRecentRecommendations ? Enumerable.Empty<Guid>() : context.RecentlyRecommendedIds)
            .Union(additionalExcludedIds ?? Enumerable.Empty<Guid>())
            .ToHashSet();

        var filteredCandidates = candidates
            .Where(candidate => !excludedIds.Contains(candidate.Id))
            .Where(candidate => includeOlderContent
                ? candidate.CreatedAt < cutoffDate
                : candidate.CreatedAt >= cutoffDate)
            .ToList();

        return CreateNeutralCandidates(filteredCandidates);
    }

    private async Task<List<ScoredContent>> GetPrimaryCandidatesAsync(
        RecommendationContext context,
        CancellationToken cancellationToken)
    {
        if (context.UserProfile?.UserEmbedding != null && context.UserProfile.UserEmbedding.Length > 0)
        {
            return await GetVectorSimilarityCandidatesAsync(
                context,
                allowRecentRecommendations: false,
                includeOlderContent: false,
                additionalExcludedIds: null,
                cancellationToken);
        }

        _logger.LogInformation("No user embedding available, falling back to traditional candidate fetching");
        return await GetTraditionalCandidatesAsync(
            context,
            allowRecentRecommendations: false,
            includeOlderContent: false,
            additionalExcludedIds: null,
            cancellationToken);
    }

    private async Task<List<ScoredContent>> RankAndSelectAsync(
        List<ScoredContent> candidates,
        RecommendationContext context,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Found {Count} candidate content", candidates.Count);

        var scoredCandidates = await ApplyHeuristicScoringAsync(candidates, context, cancellationToken);

        var filteredCandidates = scoredCandidates;
        foreach (var filter in _filters)
        {
            filteredCandidates = await filter.FilterAsync(
                filteredCandidates,
                context,
                cancellationToken);

            _logger.LogDebug(
                "After {FilterName}: {Count} candidates remaining",
                filter.GetType().Name, filteredCandidates.Count);
        }

        return filteredCandidates
            .OrderByDescending(sr => sr.FinalScore)
            .Take(context.Count)
            .ToList();
    }

    private async Task<List<ScoredContent>> BackfillRecommendationsAsync(
        List<ScoredContent> existingRecommendations,
        RecommendationContext context,
        bool allowRecentRecommendations,
        bool includeOlderContent,
        string fallbackName,
        CancellationToken cancellationToken)
    {
        var remainingCount = context.Count - existingRecommendations.Count;
        if (remainingCount <= 0)
        {
            return existingRecommendations;
        }

        var excludedIds = existingRecommendations
            .Select(recommendation => recommendation.Content.Id)
            .ToHashSet();

        List<ScoredContent> fallbackCandidates;
        if (context.UserProfile?.UserEmbedding != null && context.UserProfile.UserEmbedding.Length > 0)
        {
            fallbackCandidates = await GetVectorSimilarityCandidatesAsync(
                context,
                allowRecentRecommendations,
                includeOlderContent,
                excludedIds,
                cancellationToken);
        }
        else
        {
            fallbackCandidates = await GetTraditionalCandidatesAsync(
                context,
                allowRecentRecommendations,
                includeOlderContent,
                excludedIds,
                cancellationToken);
        }

        if (!fallbackCandidates.Any())
        {
            _logger.LogInformation(
                "No {FallbackName} candidates found for user {UserId}",
                fallbackName,
                context.UserId);
            return existingRecommendations;
        }

        var fallbackContext = CloneContext(
            context,
            remainingCount,
            allowRecentRecommendations ? new HashSet<Guid>() : context.RecentlyRecommendedIds);
        var supplementalRecommendations = await RankAndSelectAsync(
            fallbackCandidates,
            fallbackContext,
            cancellationToken);

        _logger.LogInformation(
            "Added {Count} recommendations for user {UserId} from {FallbackName}",
            supplementalRecommendations.Count,
            context.UserId,
            fallbackName);

        return existingRecommendations
            .Concat(supplementalRecommendations)
            .Take(context.Count)
            .ToList();
    }

    private static RecommendationContext CloneContext(
        RecommendationContext context,
        int count,
        HashSet<Guid> recentlyRecommendedIds)
    {
        return new RecommendationContext
        {
            UserId = context.UserId,
            FeedType = context.FeedType,
            Date = context.Date,
            Count = count,
            UserProfile = context.UserProfile,
            SeenContentIds = new HashSet<Guid>(context.SeenContentIds),
            RecentlyRecommendedIds = new HashSet<Guid>(recentlyRecommendedIds)
        };
    }

    private static int GetVectorCandidateCount(int requestedCount)
    {
        return Math.Max(100, requestedCount * 25);
    }

    private static List<ScoredContent> CreateNeutralCandidates(IEnumerable<Content> contentItems)
    {
        return contentItems
            .Select(content => new ScoredContent
            {
                Content = content,
                Scores = new Dictionary<string, double>
                {
                    { "vector_similarity", 0.5 }
                },
                FinalScore = 0.5
            })
            .ToList();
    }

    /// <summary>
    /// Apply additional heuristic scoring (recency and source affinity) on top of vector similarity.
    /// </summary>
    private async Task<List<ScoredContent>> ApplyHeuristicScoringAsync(
        List<ScoredContent> candidates,
        RecommendationContext context,
        CancellationToken cancellationToken)
    {
        // Score each content using the composite scorer (recency and source affinity)
        var heuristicScored = await _compositeScorer.ScoreContentAsync(
            candidates.Select(c => c.Content).ToList(),
            context,
            cancellationToken);

        // Merge heuristic scores with vector similarity scores
        var heuristicScoreMap = heuristicScored.ToDictionary(sr => sr.Content.Id);

        foreach (var candidate in candidates)
        {
            if (heuristicScoreMap.TryGetValue(candidate.Content.Id, out var heuristicScore))
            {
                // Merge all scores
                foreach (var kvp in heuristicScore.Scores)
                {
                    candidate.Scores[kvp.Key] = kvp.Value;
                }

                // Keep semantic relevance primary while letting the heuristic blend
                // strongly favor freshness through the recency scorer itself.
                var vectorScore = candidate.Scores.TryGetValue("vector_similarity", out var vs) ? vs : 0.5;
                var heuristicFinalScore = heuristicScore.FinalScore;

                candidate.FinalScore =
                    (vectorScore * VectorSimilarityWeight) +
                    (heuristicFinalScore * HeuristicWeight);
            }
        }

        return candidates;
    }

    private static MetricContext BuildMetricContext(
        Core.Enums.ContentType feedType,
        string outcome,
        int candidates,
        int results,
        int fallbackCount)
    {
        return new MetricContext(
            Dimensions: new Dictionary<string, string>
            {
                ["FeedType"] = feedType.ToString(),
                ["Operation"] = "recommendations.generate",
                ["Outcome"] = outcome
            },
            Properties: new Dictionary<string, object?>
            {
                ["Candidates"] = candidates,
                ["Results"] = results,
                ["FallbackCount"] = fallbackCount
            });
    }
}
