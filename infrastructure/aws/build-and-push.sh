#!/bin/bash
set -e

# CRS Docker Build and Push Script
# Builds Docker images and pushes to ECR

REGION="${AWS_REGION:-us-west-2}"
DEPLOY_API="${DEPLOY_API:-true}"
UPDATE_ECS_TASKS="${UPDATE_ECS_TASKS:-false}"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

log_info() { echo -e "${GREEN}[INFO]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --deploy-api)
            DEPLOY_API=true
            ;;
        --skip-api-deploy)
            DEPLOY_API=false
            ;;
        --update-ecs)
            UPDATE_ECS_TASKS=true
            ;;
        --skip-ecs-update)
            UPDATE_ECS_TASKS=false
            ;;
        *)
            log_error "Unknown argument: $1"
            echo "Usage: $0 [--deploy-api|--skip-api-deploy] [--update-ecs|--skip-ecs-update]"
            exit 1
            ;;
    esac
    shift
done

# Get AWS account ID
ACCOUNT_ID=$(aws sts get-caller-identity --query Account --output text)
ECR_URI="${ACCOUNT_ID}.dkr.ecr.${REGION}.amazonaws.com"

log_info "AWS Account: $ACCOUNT_ID"
log_info "ECR URI: $ECR_URI"
log_info "Region: $REGION"
log_info "Deploy API after push: $DEPLOY_API"
log_info "Update ECS tasks after push: $UPDATE_ECS_TASKS"

# Login to ECR
log_info "Logging into ECR..."
aws ecr get-login-password --region $REGION | docker login --username AWS --password-stdin $ECR_URI

# Navigate to project root
cd "$(dirname "$0")/../.."
log_info "Building from: $(pwd)"

# Build and push API image
log_info "Building crs-api image..."
docker build -t crs-api:latest -f src/Crs.Api/Dockerfile .
docker tag crs-api:latest $ECR_URI/crs-api:latest
docker tag crs-api:latest $ECR_URI/crs-api:$(git rev-parse --short HEAD 2>/dev/null || echo "manual")

log_info "Pushing crs-api to ECR..."
docker push $ECR_URI/crs-api:latest
docker push $ECR_URI/crs-api:$(git rev-parse --short HEAD 2>/dev/null || echo "manual")

# Build and push Jobs image
log_info "Building crs-jobs image..."
docker build -t crs-jobs:latest -f src/Crs.Jobs/Dockerfile .
docker tag crs-jobs:latest $ECR_URI/crs-jobs:latest
docker tag crs-jobs:latest $ECR_URI/crs-jobs:$(git rev-parse --short HEAD 2>/dev/null || echo "manual")

log_info "Pushing crs-jobs to ECR..."
docker push $ECR_URI/crs-jobs:latest
docker push $ECR_URI/crs-jobs:$(git rev-parse --short HEAD 2>/dev/null || echo "manual")

log_info "Done! Images pushed to ECR:"
log_info "  - $ECR_URI/crs-api:latest"
log_info "  - $ECR_URI/crs-jobs:latest"

# Update App Runner unless explicitly skipped
if [[ "${DEPLOY_API,,}" == "true" ]]; then
    log_info "Updating App Runner service..."
    SERVICE_ARN=$(aws apprunner list-services --region $REGION --query "ServiceSummaryList[?ServiceName=='crs-api'].ServiceArn" --output text)
    if [ -n "$SERVICE_ARN" ]; then
        aws apprunner start-deployment --service-arn $SERVICE_ARN --region $REGION
        log_info "Deployment started for App Runner service"
    else
        log_error "App Runner service 'crs-api' not found"
    fi
fi

# Optionally update ECS task definitions
if [[ "${UPDATE_ECS_TASKS,,}" == "true" ]]; then
    log_info "ECS tasks will use the latest image on next scheduled run"
    log_info "To trigger jobs manually, run:"
    log_info "  aws ecs run-task --cluster crs-cluster --task-definition crs-ingestion-task --launch-type FARGATE --network-configuration 'awsvpcConfiguration={subnets=[SUBNET_ID],securityGroups=[SG_ID],assignPublicIp=ENABLED}' --region $REGION"
fi
