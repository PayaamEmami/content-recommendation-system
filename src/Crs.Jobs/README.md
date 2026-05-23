# Crs.Jobs

Background worker service for CRS. Handles scheduled data ingestion and feed generation tasks.

## Purpose

Jobs runs as a scheduled container task that orchestrates periodic tasks for content aggregation and recommendation generation. It's a headless .NET Worker Service with no UI or HTTP endpoints.

## Scheduled Tasks

### Source Ingestion Job

- **Schedule:** Runs every 24 hours
- **Purpose:** Pulls new content from all active user-configured sources
- **Steps:**
  1. Fetch active sources from database
  2. For each source, use LLM agent to extract content
  3. Generate embeddings for new content
  4. Index content in OpenSearch vector store
  5. Save new content to database

### Feed Generation Job

- **Schedule:** Run on-demand via `run-feed.cmd`
- **Purpose:** Pre-generates personalized recommendation feeds for all users
- **Steps:**
  1. For each user and content type (Papers, Videos, Blogs, etc.):
  2. Build user interest profile from voting history
  3. Run hybrid recommendation engine (vector similarity + heuristics)
  4. Apply diversity and deduplication filters
  5. Save top N recommendations to database for fast retrieval

## Dependencies

- **Crs.Core:** Domain models and interfaces
- **Crs.Infrastructure:** Database access, OpenAI, OpenSearch
- **Crs.Recommendation:** Hybrid recommendation engine
- **Crs.Llm:** LLM-based content ingestion agent

## Configuration

Jobs requires the same configuration as the API (database connection, OpenAI, OpenSearch) plus job scheduling settings (cron expressions).

See `appsettings.json.example` for required configuration values.

## Local prerequisites (Windows)

- **Docker Desktop + WSL2** running (for local OpenSearch).
- **OpenSearch container** running (see `docker-compose.yml`):
  - `docker compose up -d opensearch`
- **Environment variables**:
  - `OpenAI__ApiKey` (used for both embeddings + LLM)
  - DB connection string reachable from your machine
  - `OpenSearch__Mode=Local` and `OpenSearch__Endpoint=http://localhost:9200`

## Running jobs locally

From the repo root:

```bash
# Source ingestion
dotnet run --project src/Crs.Jobs -- ingestion

# Feed generation
dotnet run --project src/Crs.Jobs -- feed

# X post ingestion
dotnet run --project src/Crs.Jobs -- x-ingestion
```

## Reindexing embeddings

Rebuild vector embeddings and reindex all content:

```bash
dotnet run --project src/Crs.Jobs -- reindex
```

## Deployment

Jobs is deployed as a scheduled container task:

- **Local:** `dotnet run` or Docker container
- **AWS:** ECS Fargate task triggered by EventBridge Scheduler

The service is triggered on a schedule via AWS EventBridge.
