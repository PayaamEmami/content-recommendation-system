# AGENTS.md

This file orients AI coding agents and human readers to this repository. It explains what the project is, how it is organized, and the guidance to follow when working here.

## Overview

Content Recommendation System is a personalized learning feed application that ingests content from user-defined sources and ranks recommendations with a hybrid engine. It is built with .NET and Blazor WebAssembly, uses OpenAI for ingestion and embeddings, and deploys primarily on AWS.

## Solution Layout

- `src/Crs.Api` - REST API with JWT authentication
- `src/Crs.Web` - Blazor WebAssembly UI
- `src/Crs.Jobs` - background jobs entrypoint
- `src/Crs.Core` - domain entities and interfaces
- `src/Crs.Infrastructure` - EF Core, AWS integrations, HTML fetching, external integrations
- `src/Crs.Recommendation` - hybrid recommendation engine
- `src/Crs.Llm` - LLM-powered ingestion logic
- `tests/Crs.Tests` - unit and integration tests spanning API, web, jobs, and shared libraries
- `infrastructure/aws` - deployment scripts and AWS infrastructure helpers

## Current Runtime Notes

- **API**: primary deployed runtime is AWS ECS Express Mode.
- **Web**: deployed as Blazor WebAssembly to S3 + CloudFront.
- **Jobs**: primary runtime today is local Windows Task Scheduler via `run-job.ps1`. Optional AWS job infrastructure exists, but it is not the main execution path right now.
- **OpenSearch**: local Docker mode is the current default. AWS OpenSearch Serverless remains optional infrastructure.
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
- Never commit `infrastructure/aws/secrets.env`.

## Verification Requirements For Code Changes

Use `.github/workflows/aws-deploy.yml` as the canonical source for the reusable local verification steps.

### Baseline checks

After any substantive code change, agents should run:

```bash
dotnet restore
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build --verbosity normal
```

These are the baseline checks because they match the workflow's build, compile, and test steps and provide the most reliable local signal that a change did not break the repo.

### Tiered parity checks

Agents should run additional verification when the touched area makes it relevant:

- **Blazor/web build or publish behavior changed**: run `dotnet publish src/Crs.Web/Crs.Web.csproj -c Release -o publish/web`
- **API or jobs container/runtime behavior changed**: run `docker build -t crs-api:latest -f ./src/Crs.Api/Dockerfile .` and/or `docker build -t crs-jobs:latest -f ./src/Crs.Jobs/Dockerfile .`
- **Infrastructure/workflow-only changes**: inspect the affected workflow or script and describe what could and could not be validated locally

### Verification caveats agents must report honestly

- `tests/Crs.Tests` starts a PostgreSQL Testcontainers container, so local tests require a working Docker daemon.
- GitHub Actions also performs AWS-authenticated steps after build/test, including ECR push, artifact handling, and deployment. Do not claim full CI parity unless those authenticated steps were actually run.
- If any expected verification step could not be run, explicitly state:
  - what was run
  - what was not run
  - why it was not run
- For documentation-only changes, say that no executable code changed and therefore code verification was not necessary.

## Useful References

- If a change makes this file inaccurate, incomplete, or missing a new recurring verification/runtime rule, update `AGENTS.md` in the same change so future agents inherit the correct guidance.
- `README.md` - product and deployment overview
- `.github/workflows/aws-deploy.yml` - canonical CI build/test/deploy workflow
- `.github/workflows/aws-infra.yml` - manual AWS infrastructure deployment workflow
- `infrastructure/aws/README.md` - deeper AWS deployment details
