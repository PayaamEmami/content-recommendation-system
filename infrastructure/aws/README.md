# CRS AWS Infrastructure

This directory contains all AWS infrastructure and deployment scripts for the CRS project.

## Prerequisites

1. **AWS CLI** installed and configured:
   ```bash
   aws configure
   ```

2. **Docker** installed and running

3. **.NET 10 SDK** installed

## Quick Start

### 1. Create secrets file

Create `infrastructure/aws/secrets.env` with your secrets:

```bash
# Database password (create a strong password)
DB_PASSWORD=YourStrongPassword123!

# OpenAI API Key (from https://platform.openai.com/api-keys)
OpenAI__ApiKey=sk-your-openai-key

# JWT Secret (64+ characters)
JWT_SECRET=your-very-long-jwt-secret-key-at-least-64-characters-for-security

# Optional X OAuth settings for X connect and token refresh
X__ClientId=your-x-client-id
X__ClientSecret=your-x-client-secret
X__RedirectUri=https://your-web-host/x/callback
```

The deploy script reads `infrastructure/aws/secrets.env` as the single source of truth for application secrets and runtime configuration.

### 2. Deploy infrastructure

```bash
cd infrastructure/aws
chmod +x deploy.sh
./deploy.sh
```

Optional flags:
- `ENABLE_OPENSEARCH=true ./deploy.sh` creates OpenSearch (default is skipped) and enables AWS ingestion/feed schedules.
- `RUM_IDENTITY_POOL_ID=... RUM_GUEST_ROLE_ARN=... ./deploy.sh` creates or updates the optional CloudWatch RUM monitor for the Blazor frontend.

This creates all AWS resources:
- VPC and networking (`crs-vpc`, `crs-subnet-*`)
- ECR repositories (`crs-api`, `crs-jobs`)
- RDS PostgreSQL (`crs-db`)
- S3 bucket for web hosting (`crs-web-*`)
- OpenSearch Serverless (`crs-search`)
- App Runner service (`crs-api`)
- ECS cluster and scheduled tasks (`crs-cluster`)
- IAM roles and policies (`crs-*-role`)
- CloudWatch log groups (`/aws/apprunner/crs-api/*` for API, `/crs/*` for ECS jobs, local job forwarding, and agents)
- CloudWatch dashboards and alarms for API health, job health, dependencies, and frontend observability
- Optional CloudWatch RUM app monitor (`crs-web`) when the required RUM auth inputs are provided

### 3. Build and push Docker images

```bash
chmod +x build-and-push.sh
./build-and-push.sh
```

`build-and-push.sh` starts an App Runner deployment for `crs-api` after pushing by default. Use `--skip-api-deploy` if you only want to publish the image, or `--update-ecs` if you also want the jobs guidance printed for ECS consumers.

### 4. Deploy web frontend

```bash
chmod +x deploy-web.sh
./deploy-web.sh
```

If a CloudWatch RUM app monitor named `crs-web` exists, `deploy-web.sh` updates the published Blazor `appsettings*.json` with the RUM monitor metadata and uploads `_framework/*.map` source maps to the configured S3 prefix for stack-trace deobfuscation.

Recommended deployment order after updating code or config:

```bash
cd infrastructure/aws
./deploy.sh
./build-and-push.sh
./deploy-web.sh
```

## Resource Naming

All resources are prefixed with `crs-` for clear separation from other projects:

| Resource Type | Name Pattern |
|--------------|--------------|
| VPC | `crs-vpc` |
| Subnets | `crs-subnet-1`, `crs-subnet-2` |
| Security Groups | `crs-api-sg`, `crs-rds-sg` |
| ECR Repositories | `crs-api`, `crs-jobs` |
| RDS Instance | `crs-db` |
| S3 Bucket | `crs-web-{account-id}` |
| App Runner | `crs-api` |
| ECS Cluster | `crs-cluster` |
| ECS Tasks | `crs-ingestion-task`, `crs-feed-task` |
| EventBridge Rules | `crs-ingestion-schedule`, `crs-feed-schedule` |
| OpenSearch | `crs-search` |
| Secrets | `crs-secrets/*` |
| Log Groups | `/aws/apprunner/crs-api/*` for API, `/crs/*` for ECS jobs |
| IAM Roles | `crs-*-role` |

## Observability

### What is provisioned

- **CloudWatch Logs**: App Runner application logs, ECS task logs, local Windows job forwarding (`/crs/local-jobs`), Windows host event logs (`/crs/windows-host`), and collector/agent logs.
- **CloudWatch Metrics**: Application custom metrics under `CRS/Application`, host metrics under `CRS/Host`, and frontend metrics under `AWS/RUM`.
- **X-Ray**: App Runner tracing plus OTLP/X-Ray export for ECS and local jobs.
- **Dashboards**: `crs-platform-overview`, `crs-api-observability`, `crs-jobs-observability`, and `crs-dependency-frontend-observability`.
- **Alarms**: API 5xx and latency, readiness failures, dependency spikes, local job wrapper failures, local job missing heartbeat, and frontend JavaScript errors.

### Local Windows jobs host

The production scheduled jobs currently run on Windows Task Scheduler, not ECS. To forward those logs, metrics, and traces into AWS:

```powershell
cd infrastructure\aws\cloudwatch-agent
powershell.exe -ExecutionPolicy Bypass -File .\install-windows.ps1 -Region us-west-2
```

This applies `windows-config.json`, which collects:
- Structured job logs from `C:\ProgramData\CRS\observability\jobs\*\*.jsonl`
- Windows Application/System warning+error events
- CPU, memory, and disk host metrics
- OTLP traces on `127.0.0.1:4317` and `127.0.0.1:4318`

`run-job.ps1` sets the local OTLP exporter endpoint to `http://127.0.0.1:4317` by default and emits EMF-compatible job wrapper metrics such as `job.host.heartbeat`, `job.wrapper.success.count`, and `job.wrapper.failure.count`.

## Manual Operations

### Trigger scheduled jobs manually

```bash
# Run ingestion job
aws ecs run-task \
  --cluster crs-cluster \
  --task-definition crs-ingestion-task \
  --launch-type FARGATE \
  --network-configuration 'awsvpcConfiguration={subnets=[SUBNET_ID],securityGroups=[SG_ID],assignPublicIp=ENABLED}' \
  --region us-west-2

# Run feed generation job
aws ecs run-task \
  --cluster crs-cluster \
  --task-definition crs-feed-task \
  --launch-type FARGATE \
  --network-configuration 'awsvpcConfiguration={subnets=[SUBNET_ID],securityGroups=[SG_ID],assignPublicIp=ENABLED}' \
  --region us-west-2

# API logs
SERVICE_ARN=$(aws apprunner list-services --query "ServiceSummaryList[?ServiceName=='crs-api'].ServiceArn" --output text --region us-west-2)
SERVICE_ID=$(aws apprunner describe-service --service-arn "$SERVICE_ARN" --query 'Service.ServiceId' --output text --region us-west-2)
aws logs tail /aws/apprunner/crs-api/$SERVICE_ID/application --follow --region us-west-2

# Job logs
aws logs tail /crs/ingestion --follow --region us-west-2
aws logs tail /crs/feed --follow --region us-west-2
aws logs tail /crs/local-jobs --follow --region us-west-2

# RUM monitor details
aws rum get-app-monitor --name crs-web --region us-west-2
```

### Update App Runner service

```bash
# Trigger new deployment
SERVICE_ARN=$(aws apprunner list-services --query "ServiceSummaryList[?ServiceName=='crs-api'].ServiceArn" --output text --region us-west-2)
aws apprunner start-deployment --service-arn $SERVICE_ARN --region us-west-2
```

### Connect to RDS

```bash
# Get RDS endpoint
aws rds describe-db-instances --db-instance-identifier crs-db --query 'DBInstances[0].Endpoint.Address' --output text --region us-west-2

# Connect with psql
psql -h <endpoint> -U crsadmin -d crsdb
```

## GitHub Actions Secrets

For CI/CD, add these secrets to your GitHub repository:

| Secret | Description |
|--------|-------------|
| `AWS_ACCESS_KEY_ID` | AWS access key |
| `AWS_SECRET_ACCESS_KEY` | AWS secret key |
| `SQL_ADMIN_PASSWORD` | RDS master password for `crs-db` |
| `SQL_ADMIN_USERNAME` | RDS master username (optional, defaults to `crsadmin`) |
| `OpenAI__ApiKey` | OpenAI API key for ingestion and embeddings |
| `JWT_SECRET_KEY` | JWT signing secret (64+ chars) |
| `X__CLIENT_ID` | X OAuth client ID for connect and refresh flows |
| `X__CLIENT_SECRET` | X OAuth client secret |
| `X__REDIRECT_URI` | X OAuth redirect URI, e.g. `https://your-web-host/x/callback` |
| `RUM_IDENTITY_POOL_ID` | Optional Cognito identity pool for CloudWatch RUM |
| `RUM_GUEST_ROLE_ARN` | Optional guest role ARN used by CloudWatch RUM |
