# Crs.Recommendation

The **Recommendation Engine** for the CRS (Content Recommendation System) project.

## Overview

This project contains the core recommendation logic that generates personalized learning content recommendations based on user preferences, interaction history, and content metadata.

## Architecture

### Recommendation Engine

The system uses a **hybrid recommendation approach** combining semantic similarity with traditional content-based signals:

```text
User Embedding (from upvotes) -> Vector Search -> Heuristic Scoring -> Filtering -> Ranked Recommendations
                                      |
                                      v
                            Postgres (pgvector)
```

**Primary Signal (70% weight)**: Vector similarity using embeddings
**Secondary Signals (30% weight)**: Heuristics, with recency as the dominant heuristic signal

### Components

#### 1. **Models**

- `UserInterestProfile` - User preference representation
  - `UserEmbedding` - Aggregated embedding vector from upvoted content (primary)
- `ScoredContent` - Content with calculated recommendation scores
- `RecommendationContext` - Context for generating recommendations

#### 2. **Engine**

- `RecommendationEngine` - Main orchestrator:
  1. **Vector Search Phase**: Get candidates via semantic similarity using user embedding
  2. **Heuristic Scoring Phase**: Apply traditional signals (recency and source affinity)
  3. **Filtering Phase**: Remove duplicates, ensure diversity
  4. **Ranking Phase**: Combine scores (70% vector + 30% heuristic) and sort

#### 3. **Scorers** (Heuristic Signals)

- `RecencyScorer` (80% of heuristic weight) - Faster freshness decay with a non-zero floor for older content
- `SourceAffinityScorer` (20% of heuristic weight) - Uses smoothed source-level vote history to boost or soften candidates from that source
- `CompositeScorer` - Combines heuristic scorers into weighted score

#### 4. **Filters**

- `SeenContentFilter` - Removes already-seen and recently-recommended content
- `DiversityFilter` - Ensures source diversity, prevents over-representation

#### 5. **Services**

- `UserProfileService` - Builds user profiles:
  - Generates user embedding by averaging embeddings of upvoted content
- `FeedGenerator` - Generates and persists daily recommendation feeds

## How It Works

### Daily Feed Generation

1. **Build User Profile**:
   - Aggregate embeddings of all upvoted content -> User embedding vector
2. **Vector Search**: Query Postgres/pgvector for semantically similar content
   - Uses user embedding as query vector
   - Applies filters: content type, recency (90 days), exclude seen/recommended
   - Returns top candidates with similarity scores
3. **Heuristic Scoring**: Apply traditional signals to vector candidates
   - Recency: Faster decay favoring newer content while keeping older items eligible
   - Source affinity: Use smoothed source-level vote history to boost or soften candidates from that source
4. **Combine Scores**: Hybrid ranking
   - 70% weight on vector similarity
   - 30% weight on combined heuristic signals
5. **Apply Filters**: Remove seen content, ensure diversity
6. **Rank & Select**: Sort by final score and select top N
7. **Persist**: Save recommendations to database

### Scoring Algorithm

Each content receives a hybrid score:

```text
Final Score = (VectorSimilarity x 0.70) + (HeuristicScore x 0.30)

where HeuristicScore = (RecencyScore x 0.8) + (SourceAffinityScore x 0.2)
```

- **VectorSimilarity**: Cosine similarity between user embedding and content embedding
- **RecencyScore**: Exponential decay with a floor (`0.15 + 0.85 x e^(-age/14 days)`)
- **SourceAffinityScore**: Smoothed source sentiment from historical votes for that source

## Usage

### Register Services

```csharp
builder.Services.AddRecommendationEngine();
```

### Generate Recommendations

```csharp
// Inject IFeedGenerator
var feedGenerator = serviceProvider.GetRequiredService<IFeedGenerator>();

// Generate recommendations for a specific feed
var recommendations = await feedGenerator.GenerateFeedAsync(
    userId: userId,
    feedType: ContentType.Paper,
    date: DateOnly.FromDateTime(DateTime.UtcNow),
    count: 5
);

// Or generate all feeds at once
var allRecommendations = await feedGenerator.GenerateAllFeedsAsync(
    userId: userId,
    date: DateOnly.FromDateTime(DateTime.UtcNow)
);
```

## Dependencies

The recommendation engine integrates with:

- **PostgreSQL pgvector** (via `IVectorStore`) - Semantic similarity search
- **OpenAI** (via `IEmbeddingService`) - Text embedding generation
- **PostgreSQL** (via EF Core repositories) - Persistence

## Cold Start Problem

The engine handles cold start gracefully:

1. **No User History**: Returns neutral scores, prioritizes recent content
2. **Few Interactions**: Gradually builds profile as user votes
3. **No Content**: Returns empty list with appropriate logging

## Configuration

Default settings:

- Feed count: 5 recommendations per feed
- Candidate window: Last 90 days
- Diversity limit: Max 3 content per source
- Recent recommendations window: Last 7 days
- Recency decay window: 14 days
- Minimum recency score: 0.15

These can be adjusted in the respective scorer/filter implementations.

## Configuration

The engine requires:

- Postgres with pgvector (via `IVectorStore`)
- OpenAI embeddings service (via `IEmbeddingService`)
- Database with user votes and content

See `Crs.Infrastructure` for configuration details.

## Testing

See `Crs.Tests` project for unit and integration tests.
