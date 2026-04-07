#!/bin/bash
set -e

# CRS Docker Build and Push Script
# Builds Docker images and pushes to ECR

REGION="${AWS_REGION:-us-west-2}"
DEPLOY_ECS_EXPRESS="${DEPLOY_ECS_EXPRESS:-true}"
UPDATE_ECS_TASKS="${UPDATE_ECS_TASKS:-false}"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

log_info() { echo -e "${GREEN}[INFO]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }

to_native_path() {
    local file_path="$1"
    if command -v cygpath >/dev/null 2>&1; then
        cygpath -w "$file_path"
    else
        echo "$file_path"
    fi
}

resolve_ecs_express_service_arn() {
    aws ecs list-services \
        --cluster crs-cluster \
        --region "$REGION" \
        --query "serviceArns[?contains(@, '/crs-api')] | [0]" \
        --output text 2>/dev/null || echo ""
}

wait_for_ecs_express_active() {
    local service_arn="$1"

    for i in {1..60}; do
        STATUS=$(aws ecs describe-express-gateway-service \
            --service-arn "$service_arn" \
            --region "$REGION" \
            --query "service.status.statusCode" \
            --output text 2>/dev/null || echo "UNKNOWN")
        if [ "$STATUS" = "ACTIVE" ]; then
            return 0
        fi

        echo "Current ECS Express status: $STATUS (attempt $i/60)"
        sleep 10
    done

    return 1
}

update_ecs_express_service_image() {
    local image_identifier="$1"
    local service_arn
    service_arn=$(resolve_ecs_express_service_arn)

    if [ -z "$service_arn" ] || [ "$service_arn" = "None" ]; then
        log_error "ECS Express service 'crs-api' not found in cluster crs-cluster"
        return 1
    fi

    local primary_container_file
    primary_container_file=$(mktemp)
    local primary_container_file_native
    primary_container_file_native=$(to_native_path "$primary_container_file")
    aws ecs describe-express-gateway-service \
        --service-arn "$service_arn" \
        --region "$REGION" \
        --query "service.activeConfigurations[0].primaryContainer" \
        --output json > "$primary_container_file"

    local updated_primary_container
    updated_primary_container=$(python - "$primary_container_file_native" "$image_identifier" <<'PY'
import json
import sys
from pathlib import Path

payload = json.loads(Path(sys.argv[1]).read_text())
payload["image"] = sys.argv[2]
print(json.dumps(payload, separators=(",", ":")))
PY
)

    rm -f "$primary_container_file"

    aws ecs update-express-gateway-service \
        --service-arn "$service_arn" \
        --primary-container "$updated_primary_container" \
        --region "$REGION" > /dev/null

    log_info "Submitted ECS Express deployment with image $image_identifier"

    if wait_for_ecs_express_active "$service_arn"; then
        log_info "ECS Express service is ACTIVE"
    else
        log_error "Timed out waiting for ECS Express service to become ACTIVE"
        return 1
    fi
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --deploy-api)
            DEPLOY_ECS_EXPRESS=true
            ;;
        --skip-api-deploy)
            DEPLOY_ECS_EXPRESS=false
            ;;
        --deploy-ecs-express)
            DEPLOY_ECS_EXPRESS=true
            ;;
        --skip-ecs-express)
            DEPLOY_ECS_EXPRESS=false
            ;;
        --update-ecs)
            UPDATE_ECS_TASKS=true
            ;;
        --skip-ecs-update)
            UPDATE_ECS_TASKS=false
            ;;
        *)
            log_error "Unknown argument: $1"
            echo "Usage: $0 [--deploy-api|--skip-api-deploy] [--deploy-ecs-express|--skip-ecs-express] [--update-ecs|--skip-ecs-update]"
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
log_info "Deploy ECS Express after push: $DEPLOY_ECS_EXPRESS"
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
API_IMAGE_TAG=$(git rev-parse --short HEAD 2>/dev/null || echo "manual")
docker tag crs-api:latest $ECR_URI/crs-api:${API_IMAGE_TAG}

log_info "Pushing crs-api to ECR..."
docker push $ECR_URI/crs-api:latest
docker push $ECR_URI/crs-api:${API_IMAGE_TAG}

API_IMAGE_DIGEST=$(aws ecr describe-images \
    --repository-name crs-api \
    --image-ids imageTag=${API_IMAGE_TAG} \
    --query "imageDetails[0].imageDigest" \
    --output text \
    --region "$REGION")
API_IMAGE_IDENTIFIER="${ECR_URI}/crs-api@${API_IMAGE_DIGEST}"

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

# Update API runtimes unless explicitly skipped
if [[ "${DEPLOY_ECS_EXPRESS,,}" == "true" ]]; then
    log_info "Updating ECS Express service..."
    update_ecs_express_service_image "$API_IMAGE_IDENTIFIER"
fi

# Optionally update ECS task definitions
if [[ "${UPDATE_ECS_TASKS,,}" == "true" ]]; then
    log_info "ECS tasks will use the latest image on next scheduled run"
    log_info "To trigger jobs manually, run:"
    log_info "  aws ecs run-task --cluster crs-cluster --task-definition crs-ingestion-task --launch-type FARGATE --network-configuration 'awsvpcConfiguration={subnets=[SUBNET_ID],securityGroups=[SG_ID],assignPublicIp=ENABLED}' --region $REGION"
fi
