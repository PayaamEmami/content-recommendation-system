#!/bin/bash
set -euo pipefail

# Build CRS Docker images and push to ECR.
# After push, refresh the Lightsail API stack unless --skip-lightsail is set.

REGION="${AWS_REGION:-us-west-2}"
DEPLOY_LIGHTSAIL="${DEPLOY_LIGHTSAIL:-true}"

RED='\033[0;31m'
GREEN='\033[0;32m'
NC='\033[0m'

log_info() { echo -e "${GREEN}[INFO]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --deploy-lightsail)
            DEPLOY_LIGHTSAIL=true
            ;;
        --skip-lightsail)
            DEPLOY_LIGHTSAIL=false
            ;;
        *)
            log_error "Unknown argument: $1"
            echo "Usage: $0 [--deploy-lightsail|--skip-lightsail]"
            exit 1
            ;;
    esac
    shift
done

ACCOUNT_ID=$(aws sts get-caller-identity --query Account --output text)
ECR_URI="${ACCOUNT_ID}.dkr.ecr.${REGION}.amazonaws.com"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

log_info "AWS Account: $ACCOUNT_ID"
log_info "ECR URI: $ECR_URI"
log_info "Region: $REGION"
log_info "Deploy Lightsail after push: $DEPLOY_LIGHTSAIL"

log_info "Logging into ECR..."
aws ecr get-login-password --region "$REGION" | docker login --username AWS --password-stdin "$ECR_URI"

cd "$SCRIPT_DIR/../.."
log_info "Building from: $(pwd)"

API_IMAGE_TAG=$(git rev-parse --short HEAD 2>/dev/null || echo "manual")

log_info "Building crs-api image..."
docker build -t crs-api:latest -f src/Crs.Api/Dockerfile .
docker tag crs-api:latest "$ECR_URI/crs-api:latest"
docker tag crs-api:latest "$ECR_URI/crs-api:${API_IMAGE_TAG}"

log_info "Pushing crs-api to ECR..."
docker push "$ECR_URI/crs-api:latest"
docker push "$ECR_URI/crs-api:${API_IMAGE_TAG}"

log_info "Building crs-jobs image..."
docker build -t crs-jobs:latest -f src/Crs.Jobs/Dockerfile .
docker tag crs-jobs:latest "$ECR_URI/crs-jobs:latest"
docker tag crs-jobs:latest "$ECR_URI/crs-jobs:${API_IMAGE_TAG}"

log_info "Pushing crs-jobs to ECR..."
docker push "$ECR_URI/crs-jobs:latest"
docker push "$ECR_URI/crs-jobs:${API_IMAGE_TAG}"

log_info "Done! Images pushed to ECR:"
log_info "  - $ECR_URI/crs-api:latest"
log_info "  - $ECR_URI/crs-jobs:latest"

if [[ "$(printf '%s' "$DEPLOY_LIGHTSAIL" | tr '[:upper:]' '[:lower:]')" == "true" ]]; then
    log_info "Refreshing Lightsail API stack..."
    "$SCRIPT_DIR/deploy-lightsail.sh"
fi
