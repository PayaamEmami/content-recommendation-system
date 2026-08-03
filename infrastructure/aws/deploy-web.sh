#!/bin/bash
set -e

# CRS Web Deployment Script
# Builds Blazor WebAssembly and deploys to S3

REGION="${AWS_REGION:-us-west-2}"
API_URL_SOURCE="${API_URL_SOURCE:-lightsail}"
API_BASE_URL_EXPLICIT="${API_BASE_URL_EXPLICIT:-}"
STATIC_IP_NAME="${STATIC_IP_NAME:-crs-lightsail-ip}"
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

resolve_python_bin() {
    if command -v python3 >/dev/null 2>&1 && python3 -c 'import json' >/dev/null 2>&1; then
        echo python3
        return
    fi
    if command -v python >/dev/null 2>&1 && python -c 'import json' >/dev/null 2>&1; then
        echo python
        return
    fi
    log_error "python3 (or python) with the json module is required."
    exit 1
}

PYTHON_BIN="$(resolve_python_bin)"

normalize_api_base_url() {
    local value="$1"

    if [[ "$value" == http://* || "$value" == https://* ]]; then
        echo "$value"
    else
        echo "https://$value"
    fi
}

resolve_lightsail_api_base_url() {
    local static_ip
    static_ip=$(aws lightsail get-static-ip \
        --region "$REGION" \
        --static-ip-name "$STATIC_IP_NAME" \
        --query 'staticIp.ipAddress' \
        --output text 2>/dev/null || echo "")
    if [ -z "$static_ip" ] || [ "$static_ip" = "None" ]; then
        log_error "Could not resolve Lightsail static IP '${STATIC_IP_NAME}'."
        exit 1
    fi

    normalize_api_base_url "${static_ip}.sslip.io"
}

resolve_api_base_url() {
    local api_url_source
    api_url_source="$(printf '%s' "$API_URL_SOURCE" | tr '[:upper:]' '[:lower:]')"
    case "$api_url_source" in
        lightsail)
            resolve_lightsail_api_base_url
            ;;
        explicit)
            if [ -z "$API_BASE_URL_EXPLICIT" ]; then
                log_error "API_BASE_URL_EXPLICIT must be set when API_URL_SOURCE=explicit."
                exit 1
            fi
            normalize_api_base_url "$API_BASE_URL_EXPLICIT"
            ;;
        *)
            log_error "Unsupported API_URL_SOURCE '$API_URL_SOURCE'. Use lightsail or explicit."
            exit 1
            ;;
    esac
}

to_native_path() {
    local file_path="$1"
    if command -v cygpath >/dev/null 2>&1; then
        cygpath -w "$file_path"
    else
        echo "$file_path"
    fi
}

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

    $PYTHON_BIN - "$file_path" "$api_url" "$rum_enabled" "$rum_app_monitor_id" "$rum_app_monitor_name" "$rum_region" "$rum_identity_pool_id" "$rum_guest_role_arn" "$rum_session_sample_rate" "$rum_enable_xray" "$rum_allow_cookies" <<'PY'
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

API_BASE_URL=$(resolve_api_base_url)
log_info "Using API base URL from ${API_URL_SOURCE}: ${API_BASE_URL}"

RUM_ENABLED="false"
RUM_APP_MONITOR_ID=""
RUM_IDENTITY_POOL_ID=""
RUM_GUEST_ROLE_ARN=""

if aws rum get-app-monitor --name "$RUM_APP_MONITOR_NAME" --region "$RUM_REGION" > /tmp/crs-rum-monitor.json 2>/dev/null; then
    RUM_ENABLED="true"
    RUM_MONITOR_FILE=$(to_native_path /tmp/crs-rum-monitor.json)
    export RUM_MONITOR_FILE
    RUM_APP_MONITOR_ID=$($PYTHON_BIN - <<'PY'
import json
from pathlib import Path
from os import environ
payload = json.loads(Path(environ['RUM_MONITOR_FILE']).read_text())
print(payload.get('AppMonitor', {}).get('Id', ''))
PY
)
    RUM_IDENTITY_POOL_ID=$($PYTHON_BIN - <<'PY'
import json
from pathlib import Path
from os import environ
payload = json.loads(Path(environ['RUM_MONITOR_FILE']).read_text())
print(payload.get('AppMonitor', {}).get('AppMonitorConfiguration', {}).get('IdentityPoolId', ''))
PY
)
    RUM_GUEST_ROLE_ARN=$($PYTHON_BIN - <<'PY'
import json
from pathlib import Path
from os import environ
payload = json.loads(Path(environ['RUM_MONITOR_FILE']).read_text())
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

# Set cache headers and content types for static assets.
# Fingerprinted files (contain a hash in the filename) get immutable 1-year cache.
# Non-fingerprinted framework files (blazor.webassembly.js, dotnet.js) get short
# cache so redeployments with new fingerprinted assets take effect immediately.
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

# Non-fingerprinted framework files must use short cache so that redeployments
# with new content-hashed assets are picked up without waiting for the old
# bootloader scripts to expire. These files reference fingerprinted filenames
# that change every publish; a stale copy causes 404s for the new assets.
for NON_FP_FILE in "_framework/blazor.webassembly.js" "_framework/dotnet.js"; do
    if aws s3api head-object --bucket "$BUCKET_NAME" --key "$NON_FP_FILE" --region "$REGION" > /dev/null 2>&1; then
        aws s3 cp "s3://$BUCKET_NAME/$NON_FP_FILE" "s3://$BUCKET_NAME/$NON_FP_FILE" \
            --metadata-directive REPLACE \
            --cache-control "no-cache" \
            --content-type "application/javascript" \
            --region $REGION
    fi
done

# HTML files should not be cached as aggressively
aws s3 cp s3://$BUCKET_NAME/index.html s3://$BUCKET_NAME/index.html \
    --metadata-directive REPLACE \
    --cache-control "no-cache" \
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
