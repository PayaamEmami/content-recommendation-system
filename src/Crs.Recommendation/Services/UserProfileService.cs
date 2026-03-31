using Microsoft.Extensions.Logging;
using Crs.Core.Entities;
using Crs.Core.Enums;
using Crs.Core.Interfaces;
using Crs.Recommendation.Models;

namespace Crs.Recommendation.Services;

/// <summary>
/// Builds user interest profiles from votes and manual feedback using embeddings.
/// </summary>
public class UserProfileService : IUserProfileService
{
    private readonly IContentVoteRepository _voteRepository;
    private readonly IManualContentFeedbackRepository _manualContentFeedbackRepository;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILogger<UserProfileService> _logger;

    public UserProfileService(
        IContentVoteRepository voteRepository,
        IManualContentFeedbackRepository manualContentFeedbackRepository,
        IEmbeddingService embeddingService,
        ILogger<UserProfileService> logger)
    {
        _voteRepository = voteRepository;
        _manualContentFeedbackRepository = manualContentFeedbackRepository;
        _embeddingService = embeddingService;
        _logger = logger;
    }

    public async Task<UserInterestProfile> BuildProfileAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var profile = new UserInterestProfile
        {
            UserId = userId,
            LastUpdated = DateTime.UtcNow
        };

        // Get all user votes
        var votes = await _voteRepository.GetByUserAsync(userId, cancellationToken);
        var votesList = votes.ToList();
        var manualFeedback = (await _manualContentFeedbackRepository.GetByUserAsync(userId, cancellationToken)).ToList();

        if (!votesList.Any() && !manualFeedback.Any())
        {
            _logger.LogInformation("No voting history or manual feedback for user {UserId}, returning empty profile", userId);
            return profile;
        }

        profile.TotalInteractions = votesList.Count + manualFeedback.Count;

        // Build user embedding from voted content and manual preference entries.
        await BuildUserEmbeddingAsync(profile, votesList, manualFeedback, cancellationToken);

        _logger.LogInformation(
            "Built profile for user {UserId} with {Interactions} interactions, embedding dimensions: {Dimensions}",
            userId,
            profile.TotalInteractions,
            profile.UserEmbedding?.Length ?? 0);

        return profile;
    }

    /// <summary>
    /// Build user preference embedding from both normal votes and manual preference entries.
    /// </summary>
    private async Task BuildUserEmbeddingAsync(
        UserInterestProfile profile,
        List<Core.Entities.ContentVote> votes,
        List<ManualContentFeedback> manualFeedback,
        CancellationToken cancellationToken)
    {
        try
        {
            var preferenceSignals = votes
                .Select(v => new PreferenceSignal
                {
                    Text = $"{v.Content.Title} {v.Content.Description}".Trim(),
                    Weight = v.VoteType == VoteType.Upvote ? 1.0f : -0.5f
                })
                .Concat(manualFeedback.Select(f => new PreferenceSignal
                {
                    Text = $"{f.Title} {f.Description}".Trim(),
                    Weight = f.VoteType == VoteType.Upvote ? 1.0f : -0.5f
                }))
                .Where(s => !string.IsNullOrWhiteSpace(s.Text))
                .ToList();

            if (!preferenceSignals.Any())
            {
                _logger.LogInformation("No usable preference text for user {UserId}, cannot build embedding", profile.UserId);
                return;
            }

            var texts = preferenceSignals.Select(s => s.Text).ToList();

            var embeddings = await _embeddingService.GenerateEmbeddingsAsync(texts, cancellationToken);

            if (!embeddings.Any())
            {
                _logger.LogWarning("Failed to generate embeddings for user {UserId}", profile.UserId);
                return;
            }

            // Average the embeddings to create user preference vector
            var embeddingsList = embeddings.ToList();
            var dimensions = embeddingsList[0].Length;
            var averageEmbedding = new float[dimensions];

            for (var embeddingIndex = 0; embeddingIndex < embeddingsList.Count; embeddingIndex++)
            {
                var embedding = embeddingsList[embeddingIndex];
                var weight = preferenceSignals[embeddingIndex].Weight;

                for (int i = 0; i < dimensions; i++)
                {
                    averageEmbedding[i] += embedding[i] * weight;
                }
            }

            for (int i = 0; i < dimensions; i++)
            {
                averageEmbedding[i] /= embeddingsList.Count;
            }

            // Normalize the vector (L2 normalization)
            var magnitude = Math.Sqrt(averageEmbedding.Sum(x => x * x));
            if (magnitude > 0)
            {
                for (int i = 0; i < dimensions; i++)
                {
                    averageEmbedding[i] /= (float)magnitude;
                }
            }

            profile.UserEmbedding = averageEmbedding;

            _logger.LogDebug(
                "Built user embedding from {Count} preference signals for user {UserId}",
                preferenceSignals.Count,
                profile.UserId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building user embedding for user {UserId}", profile.UserId);
            // Continue without embedding - other scorers can still work
        }
    }

    private sealed class PreferenceSignal
    {
        public string Text { get; set; } = string.Empty;
        public float Weight { get; set; }
    }

}
