# CRS - AI Assistant Context

## Project Overview

**Purpose**: Personalized recommendation system that aggregates learning content from user-defined sources using AI-powered recommendations.

**Tech Stack**:

- **Backend**: .NET 10, ASP.NET Core, Entity Framework Core
- **Frontend**: Blazor WebAssembly (S3 + CloudFront)
- **Cloud**: AWS (App Runner, ECS, RDS, OpenSearch, S3)
- **AI**: OpenAI API (GPT-5-nano, text-embedding-3-small)

## Architecture

### Core Services

1. **Crs.Api** - REST API with JWT authentication (App Runner)
2. **Crs.Web** - Blazor WebAssembly web UI (S3 + CloudFront)
3. **Crs.Jobs** - Scheduled jobs (ECS Fargate + EventBridge):
   - **Ingestion Job**: Runs daily at midnight UTC
   - **Feed Generation Job**: Runs daily at 2 AM UTC
4. **Crs.Core** - Domain entities, interfaces
5. **Crs.Infrastructure** - Data access, AWS integrations, HTML fetching
6. **Crs.Recommendation** - Hybrid engine (70% vector, 30% heuristics)
7. **Crs.Llm** - Content ingestion (ChatGPT extracts from HTML)

### AWS Resources

All resources prefixed with `crs-` for clear separation:

- 1 App Runner service (API) - `crs-api`
- 1 S3 bucket + CloudFront (Web) - `crs-web-*`
- 1 ECS Cluster - `crs-cluster`
- RDS PostgreSQL - `crs-db`
- ECR repositories - `crs-api`, `crs-jobs`
- EventBridge Scheduler - `crs-cloudfront-invalidation` (daily CloudFront cache invalidation at 1 PM Pacific)
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
OpenSearch__Endpoint
ConnectionStrings__DefaultConnection
OpenAI__ApiKey
ApiBaseUrl

# Wrong
AWS_OPENSEARCH_ENDPOINT
SQL_CONNECTION_STRING
```

**Mapping**: `appsettings.json` section `"OpenSearch": { "Endpoint": "..." }` → env var `OpenSearch__Endpoint`

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
- Push to `main` → GitHub Actions deploys automatically

## Ingestion Architecture

1. **HtmlFetcherService**: Fetches HTML, removes `<script>`/`<style>` tags
2. **IngestionAgent**: Sends HTML to ChatGPT Chat Completion API → extracts JSON
3. **SourceIngestionJob**: Maps to entities (Paper/Video/BlogPost) → saves to DB → generates embeddings → indexes in OpenSearch

**Important**: Job exits early if no active sources (no OpenAI calls made).

## Job Scheduling

Jobs run locally via **Windows Task Scheduler**, using `run-job.ps1` as a wrapper script that automatically starts Docker Desktop and the OpenSearch container if they aren't running.

- **CRS - Ingestion**: Daily at 12:00 PM Pacific (`dotnet Crs.Jobs.dll ingestion`)
- **CRS - X Ingestion**: Daily at 12:30 PM Pacific (`dotnet Crs.Jobs.dll x-ingestion`)
- **CRS - Feed Generation**: Daily at 1:00 PM Pacific (`dotnet Crs.Jobs.dll feed`)
- **CloudFront Cache Invalidation**: Daily at 2:00 PM Pacific (EventBridge Scheduler, `crs-cloudfront-invalidation`)

The CloudFront invalidation runs as an AWS EventBridge Scheduler (`crs-cloudfront-invalidation`) since it only needs AWS access, not the local environment.

### ECS Fargate (available but not primary)

ECS task definitions and EventBridge rules can be created for the ingestion and feed jobs in AWS when OpenSearch is enabled (`ENABLE_OPENSEARCH=true` in `deploy.sh`). X ingestion is intended to run locally via Windows Task Scheduler, not via AWS. Currently OpenSearch Serverless is not deployed.

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
- **Clean Architecture**: Core → Infrastructure → API/Web/Jobs
- **Repository Pattern**: All data access through interfaces
- **PostgreSQL**: Using PostgreSQL instead of SQL Server (cost-effective)
- **OpenSearch Serverless**: AWS-native vector search

## Important Rules

- **NO standalone markdown files** (no `TROUBLESHOOTING.md`, `DEPLOYMENT_GUIDE.md`, etc.)
- **NEVER auto-commit** - only when user explicitly requests
- Update this file for architectural changes only
