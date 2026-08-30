# AGENTS.md

This file orients AI coding agents and human readers to this repository. It explains what the project is, how it is organized, and the guidance to follow when working here.

## Overview

Content Recommendation System is a personalized learning feed application that ingests content from user-defined sources and ranks recommendations with a hybrid engine. It is built with .NET and Blazor WebAssembly, uses OpenAI for ingestion and embeddings, and deploys primarily on AWS.

## Technology Stack

| Layer          | Technology                                                       |
| -------------- | ---------------------------------------------------------------- |
| Frontend       | Blazor WebAssembly, .NET 10                                      |
| Backend        | ASP.NET Core, C#                                                 |
| Database       | PostgreSQL (EF Core)                                             |
| Vector Search  | OpenSearch                                                           |
| AI             | OpenAI                                                               |
| Jobs           | .NET Worker Service; local `scripts/run-job.sh`                      |
| Infrastructure | AWS Lightsail, S3, CloudFront, ECR, Docker Compose                   |
| Auth           | JWT                                                                  |
| CI/CD          | GitHub Actions                                                       |

## Infrastructure

Production runs on AWS with a cheap always-on Lightsail API host and a static Blazor frontend:

- **S3 + CloudFront** host the Blazor WebAssembly frontend
- **Lightsail** (`crs-lightsail` + `crs-lightsail-ip`) runs Postgres, OpenSearch (Local mode), the API, and Caddy HTTPS
- **Local jobs** (`scripts/run-job.sh`; Git Bash or WSL on Windows) write to Lightsail Postgres + OpenSearch
- **ECR** stores API container images pulled by Lightsail

See [`infrastructure/aws/README.md`](infrastructure/aws/README.md) for deploy and day-to-day ops.

## Repository Structure

```
├── src/
│   ├── Crs.Api/              # REST API with JWT authentication
│   ├── Crs.Web/              # Blazor WebAssembly UI
│   ├── Crs.Jobs/             # Background job processor
│   ├── Crs.Core/             # Domain entities and interfaces
│   ├── Crs.Infrastructure/   # EF Core, AWS integrations, vector store
│   ├── Crs.Recommendation/   # Hybrid recommendation engine
│   └── Crs.Llm/              # LLM-powered ingestion logic
├── tests/
│   └── Crs.Tests/            # Unit and integration tests
├── infrastructure/
│   └── aws/                  # Lightsail API runtime, ECR, S3/CloudFront deploy
├── docker-compose.yml        # Local Postgres, OpenSearch, and optional full stack
├── .github/workflows/        # CI and CD GitHub Actions pipelines
└── scripts/                  # Local job runner (`run-job.sh`)
```

## Architecture

At a high level, CRS is composed of:

### Blazor WebAssembly Frontend

- Client-side interactive web UI hosted on S3 + CloudFront
- Multiple feed types (Papers, Videos, Blogs)
- User flows for browsing personalized feeds and managing sources
- Responsive design with Dark/Light theme support
- Offline-capable with local storage for auth persistence

### .NET Backend + REST API

- Central application layer (business logic, validation, orchestration)
- JWT-based authentication with refresh tokens
- API versioning and rate limiting
- REST endpoints for authentication, source management, content, voting, and recommendations

### Data Ingestion Layer

- Pulls content from user-configured sources (RSS/Atom, video, newsletters)
- LLM agent extracts and categorizes content from URLs
- Generates embeddings and indexes content in OpenSearch

### Recommendation Engine

Hybrid scoring combines:

- **Vector similarity** (70% weight) using OpenAI embeddings and OpenSearch
- **Heuristic signals** (30% weight) with recency dominant inside heuristics
- Diversity, deduplication, and personalization filters

### Background Jobs

Jobs are implemented in `Crs.Jobs`:

- **Source Ingestion** — Pull new content, embed, and index
- **Feed Generation** — Pre-generate personalized feeds per user and content type
- **X Ingestion** — Sync posts from connected X accounts

AWS job schedules are optional/legacy. Locally, jobs run via `scripts/run-job.sh` (or `dotnet run`) and should target the Lightsail Postgres + OpenSearch endpoints.

## Current Runtime Notes

- **API**: primary deployed runtime is Lightsail Compose (`crs-lightsail`) behind Caddy HTTPS.
- **Web**: deployed as Blazor WebAssembly to S3 + CloudFront.
- **Jobs**: primary runtime is local `scripts/run-job.sh`, writing to Lightsail (not starting local OpenSearch when `OpenSearch__Endpoint` is remote).
- **OpenSearch**: Local mode on the Lightsail box (and optionally local Docker for pure offline dev).
- **Recommendation engine**: hybrid scoring with 70% vector similarity and 30% heuristics, with recency dominant inside the heuristic portion.

## Critical Config Conventions

- .NET hierarchical configuration uses `__` in environment variables.

```text
# Correct
OpenSearch__Mode
OpenSearch__Endpoint
ConnectionStrings__DefaultConnection
OpenAI__ApiKey
ApiBaseUrl

# Wrong
AWS_OPENSEARCH_ENDPOINT
SQL_CONNECTION_STRING
```

- Mapping example: `appsettings.json` key `OpenSearch:Endpoint` becomes env var `OpenSearch__Endpoint`.
- `OpenSearch:Mode` defaults to `Local`, so API and jobs expect the local Docker-backed OpenSearch path unless explicitly configured otherwise.
- Never commit `infrastructure/aws/secrets.env` or `infrastructure/aws/.env`.

## Agent Verification Checklist

Before handing work back, run the narrowest useful checks during iteration, then run the broader checks needed to prove the final change is safe.

Standard repo-level verification:

```bash
dotnet restore
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build --verbosity normal
```

Use additional checks when the touched area makes it relevant:

- **Blazor/web build or publish behavior changed**: `dotnet publish src/Crs.Web/Crs.Web.csproj -c Release -o publish/web`
- **API or jobs container/runtime behavior changed**: `docker build -t crs-api:latest -f ./src/Crs.Api/Dockerfile .` and/or `docker build -t crs-jobs:latest -f ./src/Crs.Jobs/Dockerfile .`
- **Infrastructure/workflow-only changes**: inspect the affected workflow or script and describe what could and could not be validated locally

Notes:

- CI currently runs `dotnet restore`, `dotnet build`, and `dotnet test` via `.github/workflows/aws-deploy.yml`
- `tests/Crs.Tests` starts a PostgreSQL Testcontainers container, so local tests require a working Docker daemon
- GitHub Actions also performs AWS-authenticated steps after build/test. Do not claim full CI parity unless those steps were actually run
- If you cannot run a needed check because of missing credentials, services, or environment, say so explicitly
- For documentation-only changes, say that no executable code changed and therefore code verification was not necessary

## Production Notes

- Production deployment is driven by `.github/workflows/aws-deploy.yml`
- See the Infrastructure section above and [`infrastructure/aws/README.md`](infrastructure/aws/README.md) for deploy and day-to-day ops

## Maintenance

Coding agents should update this file as part of the same change whenever any of the following become stale:

- Technology stack or infrastructure layout
- Repo shape or package ownership
- Required environment variables or config conventions
- Verification commands or CI expectations
- Major product capabilities that affect how agents should reason about changes
