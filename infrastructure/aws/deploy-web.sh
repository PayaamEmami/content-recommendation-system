#!/bin/bash
set -e

# CRS Web Deployment Script
# Builds Blazor WebAssembly and deploys to S3

REGION="${AWS_REGION:-us-west-2}"
RUM_APP_MONITOR_NAME="${RUM_APP_MONITOR_NAME:-crs-web}"
RUM_REGION="${RUM_REGION:-$REGION}"
RUM_SOURCE_MAPS_PREFIX="${RUM_SOURCE_MAPS_PREFIX:-rum-source-maps}"
RUM_SESSION_SAMPLE_RATE="${RUM_SESSION_SAMPLE_RATE:-0.1}"
RUM_ALLOW_COOKIES="${RUM_ALLOW_COOKIES:-true}"
RUM_ENABLE_XRAY="${RUM_ENABLE_XRAY:-true}"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
NC='\033[0m'

log_info() { echo -e "${GREEN}[INFO]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }

update_api_base_url() {
    local file_path="$1"
    local api_url="$2"

    if [ ! -f "$file_path" ]; then
        return
    fi

    sed -i "s|\"ApiBaseUrl\": \".*\"|\"ApiBaseUrl\": \"${api_url}\"|" "$file_path" 2>/dev/null || \
    sed -i '' "s|\"ApiBaseUrl\": \".*\"|\"ApiBaseUrl\": \"${api_url}\"|" "$file_path"
}

update_web_observability_config() {
    local file_path="$1"
    local api_url="$2"
    local rum_enabled="$3"
    local rum_app_monitor_id="$4"
    local rum_app_monitor_name="$5"
    local rum_region="$6"
    local rum_identity_pool_id="$7"
    local rum_guest_role_arn="$8"
    local rum_session_sample_rate="$9"
    local rum_enable_xray="${10}"
    local rum_allow_cookies="${11}"

    if [ ! -f "$file_path" ]; then
        return
    fi

    python - "$file_path" "$api_url" "$rum_enabled" "$rum_app_monitor_id" "$rum_app_monitor_name" "$rum_region" "$rum_identity_pool_id" "$rum_guest_role_arn" "$rum_session_sample_rate" "$rum_enable_xray" "$rum_allow_cookies" <<'PY'
import json
import sys
from pathlib import Path

file_path = Path(sys.argv[1])
api_url = sys.argv[2]
rum_enabled = sys.argv[3].lower() == "true"
rum_app_monitor_id = sys.argv[4]
rum_app_monitor_name = sys.argv[5]
rum_region = sys.argv[6]
rum_identity_pool_id = sys.argv[7]
rum_guest_role_arn = sys.argv[8]
rum_session_sample_rate = float(sys.argv[9])
rum_enable_xray = sys.argv[10].lower() == "true"
rum_allow_cookies = sys.argv[11].lower() == "true"

payload = json.loads(file_path.read_text())
payload["ApiBaseUrl"] = api_url
payload.setdefault("Observability", {})
payload["Observability"]["Rum"] = {
    "Enabled": rum_enabled,
    "AppMonitorId": rum_app_monitor_id,
    "AppMonitorName": rum_app_monitor_name,
    "Region": rum_region,
    "IdentityPoolId": rum_identity_pool_id,
    "GuestRoleArn": rum_guest_role_arn,
    "SessionSampleRate": rum_session_sample_rate,
    "EnableXRay": rum_enable_xray,
    "AllowCookies": rum_allow_cookies,
    "Telemetries": ["errors", "performance", "http"]
}

file_path.write_text(json.dumps(payload, indent=2) + "\n")
PY
}

# Get AWS account ID
ACCOUNT_ID=$(aws sts get-caller-identity --query Account --output text)
BUCKET_NAME="crs-web-${ACCOUNT_ID}"

log_info "Deploying to S3 bucket: $BUCKET_NAME"

# Navigate to project root
cd "$(dirname "$0")/../.."

# Resolve current App Runner API URL so the published web config points at the live API.
SERVICE_ARN=$(aws apprunner list-services --query "ServiceSummaryList[?ServiceName=='crs-api'].ServiceArn" --output text --region $REGION 2>/dev/null || echo "")
if [ -z "$SERVICE_ARN" ] || [ "$SERVICE_ARN" = "None" ]; then
    log_error "Could not find App Runner service 'crs-api' in region $REGION."
    exit 1
fi

API_URL=$(aws apprunner describe-service --service-arn "$SERVICE_ARN" --query 'Service.ServiceUrl' --output text --region $REGION)
API_BASE_URL="https://${API_URL}"
log_info "Using API base URL: ${API_BASE_URL}"

RUM_ENABLED="false"
RUM_APP_MONITOR_ID=""
RUM_IDENTITY_POOL_ID=""
RUM_GUEST_ROLE_ARN=""

if aws rum get-app-monitor --name "$RUM_APP_MONITOR_NAME" --region "$RUM_REGION" > /tmp/crs-rum-monitor.json 2>/dev/null; then
    RUM_ENABLED="true"
    RUM_APP_MONITOR_ID=$(python - <<'PY'
import json
from pathlib import Path
payload = json.loads(Path('/tmp/crs-rum-monitor.json').read_text())
print(payload.get('AppMonitor', {}).get('Id', ''))
PY
)
    RUM_IDENTITY_POOL_ID=$(python - <<'PY'
import json
from pathlib import Path
payload = json.loads(Path('/tmp/crs-rum-monitor.json').read_text())
print(payload.get('AppMonitor', {}).get('AppMonitorConfiguration', {}).get('IdentityPoolId', ''))
PY
)
    RUM_GUEST_ROLE_ARN=$(python - <<'PY'
import json
from pathlib import Path
payload = json.loads(Path('/tmp/crs-rum-monitor.json').read_text())
print(payload.get('AppMonitor', {}).get('AppMonitorConfiguration', {}).get('GuestRoleArn', ''))
PY
)
    log_info "Using CloudWatch RUM app monitor: ${RUM_APP_MONITOR_NAME}"
else
    log_info "No CloudWatch RUM app monitor named ${RUM_APP_MONITOR_NAME} found. Frontend RUM stays disabled."
fi

# Build Blazor WebAssembly
log_info "Building Blazor WebAssembly..."
dotnet publish src/Crs.Web/Crs.Web.csproj -c Release -o publish/web

log_info "Updating published web config with current API URL..."
update_web_observability_config "publish/web/wwwroot/appsettings.json" "$API_BASE_URL" "$RUM_ENABLED" "$RUM_APP_MONITOR_ID" "$RUM_APP_MONITOR_NAME" "$RUM_REGION" "$RUM_IDENTITY_POOL_ID" "$RUM_GUEST_ROLE_ARN" "$RUM_SESSION_SAMPLE_RATE" "$RUM_ENABLE_XRAY" "$RUM_ALLOW_COOKIES"
update_web_observability_config "publish/web/wwwroot/appsettings.Production.json" "$API_BASE_URL" "$RUM_ENABLED" "$RUM_APP_MONITOR_ID" "$RUM_APP_MONITOR_NAME" "$RUM_REGION" "$RUM_IDENTITY_POOL_ID" "$RUM_GUEST_ROLE_ARN" "$RUM_SESSION_SAMPLE_RATE" "$RUM_ENABLE_XRAY" "$RUM_ALLOW_COOKIES"
update_web_observability_config "publish/web/wwwroot/appsettings.Development.json" "$API_BASE_URL" "false" "" "" "$RUM_REGION" "" "" "$RUM_SESSION_SAMPLE_RATE" "false" "false"

# Sync to S3
log_info "Uploading to S3..."
aws s3 sync publish/web/wwwroot s3://$BUCKET_NAME --delete --region $REGION

if ls publish/web/wwwroot/_framework/*.map >/dev/null 2>&1; then
    log_info "Uploading source maps for CloudWatch RUM..."
    aws s3 sync publish/web/wwwroot/_framework "s3://$BUCKET_NAME/${RUM_SOURCE_MAPS_PREFIX}/_framework" \
        --exclude "*" \
        --include "*.map" \
        --region $REGION
fi

# Set cache headers and content types for static assets
log_info "Setting cache headers..."
aws s3 cp s3://$BUCKET_NAME/ s3://$BUCKET_NAME/ \
    --recursive \
    --exclude "*" \
    --include "*.js" \
    --metadata-directive REPLACE \
    --cache-control "max-age=31536000" \
    --content-type "application/javascript" \
    --region $REGION

aws s3 cp s3://$BUCKET_NAME/ s3://$BUCKET_NAME/ \
    --recursive \
    --exclude "*" \
    --include "*.css" \
    --metadata-directive REPLACE \
    --cache-control "max-age=31536000" \
    --content-type "text/css" \
    --region $REGION

aws s3 cp s3://$BUCKET_NAME/ s3://$BUCKET_NAME/ \
    --recursive \
    --exclude "*" \
    --include "*.woff2" \
    --metadata-directive REPLACE \
    --cache-control "max-age=31536000" \
    --content-type "font/woff2" \
    --region $REGION

aws s3 cp s3://$BUCKET_NAME/ s3://$BUCKET_NAME/ \
    --recursive \
    --exclude "*" \
    --include "*.wasm" \
    --metadata-directive REPLACE \
    --cache-control "max-age=31536000" \
    --content-type "application/wasm" \
    --region $REGION

# HTML files should not be cached as aggressively
aws s3 cp s3://$BUCKET_NAME/index.html s3://$BUCKET_NAME/index.html \
    --metadata-directive REPLACE \
    --cache-control "max-age=300" \
    --content-type "text/html" \
    --region $REGION

WEB_URL="http://${BUCKET_NAME}.s3-website-${REGION}.amazonaws.com"
log_info "Deployment complete!"
log_info "Web URL: $WEB_URL"
log_info "Published API base URL: $API_BASE_URL"

# Optional: Invalidate CloudFront cache if distribution exists
DIST_ID=$(aws cloudfront list-distributions --query "DistributionList.Items[?contains(Origins.Items[].DomainName, '${BUCKET_NAME}')].Id" --output text 2>/dev/null || echo "")
if [ -n "$DIST_ID" ] && [ "$DIST_ID" != "None" ]; then
    log_info "Invalidating CloudFront cache..."
    aws cloudfront create-invalidation --distribution-id $DIST_ID --paths "/*"
fi
