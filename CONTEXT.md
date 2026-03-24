# CONTEXT

This file is a quick orientation guide for AI coding agents working in this repository.

## Project Overview

**Purpose**: Personalized recommendation system that aggregates learning content from user-defined sources using AI-powered recommendations.

**Tech Stack**:

- **Backend**: .NET 10, ASP.NET Core, Entity Framework Core
- **Frontend**: Blazor WebAssembly (S3 + CloudFront)
- **Cloud**: AWS (App Runner, RDS, S3, CloudFront; optional ECS/OpenSearch infrastructure exists but is not the primary job runtime today)
- **AI**: OpenAI API (GPT-5-nano, text-embedding-3-small)

## Architecture

### Core Services

1. **Crs.Api** - REST API with JWT authentication (App Runner)
2. **Crs.Web** - Blazor WebAssembly web UI (S3 + CloudFront)
3. **Crs.Jobs** - Console jobs executed locally via Windows Task Scheduler today; optional ECS/EventBridge infrastructure also exists for AWS deployment:
   - **Primary runtime today**: local scheduled tasks on this PC via `run-job.ps1`
   - **Optional AWS runtime**: ECS task definitions + EventBridge rules created by `infrastructure/aws/deploy.sh` when enabled
4. **Crs.Core** - Domain entities, interfaces
5. **Crs.Infrastructure** - Data access, AWS integrations, HTML fetching
6. **Crs.Recommendation** - Hybrid engine (70% vector, 30% heuristics)
7. **Crs.Llm** - Content ingestion (ChatGPT extracts from HTML)

### AWS Resources

All resources prefixed with `crs-` for clear separation:

- 1 App Runner service (API) - `crs-api`
- 1 S3 bucket + CloudFront (Web) - `crs-web-*`
- 1 ECS Cluster - `crs-cluster` (available for AWS-hosted jobs, not the current primary execution path)
- RDS PostgreSQL - `crs-db`
- ECR repositories - `crs-api`, `crs-jobs`
- EventBridge Scheduler - `crs-cloudfront-invalidation` (configured in `deploy.sh` for daily CloudFront invalidation at 1:00 PM `America/Los_Angeles`)
- Secrets Manager - `crs-secrets/*`
- CloudWatch logs - `/aws/apprunner/crs-api/*` for API, `/crs/*` for ECS jobs
- OpenAI API (direct, not AWS Bedrock)
- AWS OpenSearch Serverless - **not currently deployed** (enable with `ENABLE_OPENSEARCH=true` in `deploy.sh`)

**Default Region**: `us-west-2`
**Content Types**: Paper, Video, BlogPost

## Critical Configuration

### Environment Variables

**CRITICAL**: .NET uses `__` (double underscore) for hierarchical config.

```
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

**Mapping**: `appsettings.json` section `"OpenSearch": { "Endpoint": "..." }` -> env var `OpenSearch__Endpoint`

**Current default**: `OpenSearch:Mode` defaults to `Local` in app settings, so API and jobs expect the local Docker OpenSearch instance unless explicitly configured for AWS.

### Registration

**Toggle user registration**:

1. **API** - Update App Runner environment variables:

```bash
# Get current service ARN
SERVICE_ARN=$(aws apprunner list-services --query "ServiceSummaryList[?ServiceName=='crs-api'].ServiceArn" --output text --region us-west-2)

# Update registration setting (requires service update)
```

2. **Web** - Update in `src/Crs.Web/wwwroot/appsettings.json` (requires redeploy):

```json
"Registration": { "Enabled": true }
```

### Files

- `infrastructure/aws/secrets.env` - NEVER commit (contains secrets)
- Push to `main` -> GitHub Actions deploys automatically

## Ingestion Architecture

1. **HtmlFetcherService**: Fetches HTML, removes `<script>`/`<style>` tags
2. **IngestionAgent**: Sends HTML to ChatGPT Chat Completion API -> extracts JSON
3. **SourceIngestionJob**: Maps to entities (Paper/Video/BlogPost) -> saves to DB -> generates embeddings -> indexes in OpenSearch (local Docker by default, AWS Serverless when explicitly configured)

**Important**: Job exits early if no active sources (no OpenAI calls made).

## Job Scheduling

Jobs currently run locally via **Windows Task Scheduler**, using `run-job.ps1` as a wrapper script. The wrapper automatically starts Docker Desktop and the local OpenSearch container if they are not already running, then executes `dotnet run --project src/Crs.Jobs -- <job-name>`.

Use **UTC as the canonical schedule** in this document so DST changes do not require doc updates. This PC's scheduled tasks are:

- **CRS - Ingestion**: Daily at **19:00 UTC** (currently 12:00 PM in `America/Los_Angeles`), action: `powershell.exe ... run-job.ps1 -JobName ingestion`
- **CRS - X Ingestion**: Daily at **19:30 UTC** (currently 12:30 PM in `America/Los_Angeles`), action: `powershell.exe ... run-job.ps1 -JobName x-ingestion`
- **CRS - Feed Generation**: Daily at **20:00 UTC** (currently 1:00 PM in `America/Los_Angeles`), action: `powershell.exe ... run-job.ps1 -JobName feed`
- **CloudFront Cache Invalidation**: Configured in AWS Scheduler at **1:00 PM `America/Los_Angeles`** via `crs-cloudfront-invalidation`; AWS handles DST automatically because the schedule has an explicit timezone

The CloudFront invalidation runs as an AWS EventBridge Scheduler (`crs-cloudfront-invalidation`) since it only needs AWS access, not the local environment.

Task Scheduler triggers use 'Synchronize across time zones' to stay aligned with UTC year-round. This file records the intended UTC execution times and does not need adjustment for seasonal local time changes.

### ECS Fargate (available but not primary)

ECS task definitions and EventBridge rules can be created for the ingestion and feed jobs in AWS when OpenSearch is enabled (`ENABLE_OPENSEARCH=true` in `deploy.sh`). Those AWS schedules are currently optional infrastructure, not the active production execution path. In the current script they are created as **weekly Sunday** schedules:

- **Ingestion**: Sunday at 00:00 UTC
- **Feed**: Sunday at 02:00 UTC

X ingestion is still intended to run locally via Windows Task Scheduler, not via AWS. OpenSearch Serverless is not currently deployed.

Tasks run hidden (no terminal window). Manage via PowerShell:

```powershell
# View task status
Get-ScheduledTask -TaskName "CRS*" | Get-ScheduledTaskInfo

# Disable/enable a task
Disable-ScheduledTask -TaskName "CRS - Ingestion"
Enable-ScheduledTask -TaskName "CRS - Ingestion"

# Remove a task
Unregister-ScheduledTask -TaskName "CRS - Ingestion" -Confirm:$false
```

## Bulk Import

**API**: `POST /api/v1/sources/bulk-import`
**Format**:

```json
{
  "sources": [
    {
      "name": "...",
      "url": "...",
      "category": "Paper|Video|BlogPost",
      "description": "..."
    }
  ]
}
```

## Development

**Available Tools**: AWS CLI (`aws`) and GitHub CLI (`gh`) are available for automation and deployment tasks.

### Infrastructure Scripts

Helper scripts in `infrastructure/aws/`:

- **deploy.sh** - Deploys all AWS infrastructure
- **build-and-push.sh** - Builds and pushes Docker images to ECR
- **deploy-web.sh** - Builds and deploys Blazor to S3

See `infrastructure/aws/README.md` for detailed usage.

```bash
# Recommended deployment order
cd infrastructure/aws
./deploy.sh
./build-and-push.sh
./deploy-web.sh

# Migrations (local development)
dotnet ef migrations add Name --project src/Crs.Infrastructure --startup-project src/Crs.Api
dotnet ef database update --project src/Crs.Infrastructure --startup-project src/Crs.Api

# View API logs
SERVICE_ARN=$(aws apprunner list-services --query "ServiceSummaryList[?ServiceName=='crs-api'].ServiceArn" --output text --region us-west-2)
SERVICE_ID=$(aws apprunner describe-service --service-arn "$SERVICE_ARN" --query 'Service.ServiceId' --output text --region us-west-2)
aws logs tail /aws/apprunner/crs-api/$SERVICE_ID/application --follow --region us-west-2
```

## Key Decisions

- **Hybrid Recommendations**: 70% vector similarity, 30% heuristics
- **HTML-First Ingestion**: Fetch HTML ourselves, send to ChatGPT
- **Chat Completion API**: Standard API
- **Clean Architecture**: Core -> Infrastructure -> API/Web/Jobs
- **Repository Pattern**: All data access through interfaces
- **OpenSearch**: Local Docker mode is the current default; AWS OpenSearch Serverless remains an optional deployment target

## Important Rules

- **NO standalone markdown files** (no `TROUBLESHOOTING.md`, `DEPLOYMENT_GUIDE.md`, etc.)
- **NEVER auto-commit** - only when user explicitly requests
- Update this file as needed whenever behavior, configuration, infrastructure, or workflows change
