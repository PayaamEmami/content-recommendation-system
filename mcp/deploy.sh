#!/usr/bin/env bash
# Deploy the CRS MCP server as a Lambda Function URL in us-west-2.
# Usage:
#   MCP_API_KEY_SHA256=... CRS_API_BASE_URL=https://....sslip.io \
#   CRS_EMAIL=... CRS_PASSWORD=... ./mcp/deploy.sh
set -euo pipefail

FUNCTION_NAME="${FUNCTION_NAME:-crs-mcp-server}"
REGION="${AWS_REGION:-us-west-2}"
RUNTIME="python3.12"
HANDLER="mcp_server.lambda_handler"
TIMEOUT=120
MEMORY_SIZE=256
ROLE_NAME="${CRS_MCP_LAMBDA_ROLE_NAME:-crs-mcp-lambda-execution-role}"
STATEMENT_ID="FunctionURLAllowPublicAccess"
CORS_CONFIG='AllowCredentials=false,AllowHeaders=["authorization","content-type","mcp-protocol-version"],AllowMethods=["POST"],AllowOrigins=["*"],ExposeHeaders=[],MaxAge=3600'

PROFILE_ARGS=()
if [[ -n "${AWS_CLI_PROFILE:-}" ]]; then
  PROFILE_ARGS=(--profile "$AWS_CLI_PROFILE")
fi

: "${MCP_API_KEY_SHA256:?Set MCP_API_KEY_SHA256 to the hex digest from mcp/create_api_key.py}"
: "${CRS_API_BASE_URL:?Set CRS_API_BASE_URL to the public Crs.Api HTTPS origin}"
: "${CRS_EMAIL:?Set CRS_EMAIL to the CRS account the MCP should act as}"
: "${CRS_PASSWORD:?Set CRS_PASSWORD to that account's password}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TEMP_DIR="$(mktemp -d)"
cleanup() { rm -rf "$TEMP_DIR"; }
trap cleanup EXIT

log() { printf '[crs-mcp-deploy] %s\n' "$*"; }

ACCOUNT_ID="$(aws sts get-caller-identity "${PROFILE_ARGS[@]}" --query Account --output text)"
ROLE_ARN="${CRS_MCP_LAMBDA_ROLE_ARN:-arn:aws:iam::${ACCOUNT_ID}:role/${ROLE_NAME}}"

if ! aws iam get-role --role-name "$ROLE_NAME" "${PROFILE_ARGS[@]}" >/dev/null 2>&1; then
  log "Creating IAM role ${ROLE_NAME}"
  TRUST_FILE="$TEMP_DIR/trust.json"
  cat > "$TRUST_FILE" <<'EOF'
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Principal": { "Service": "lambda.amazonaws.com" },
      "Action": "sts:AssumeRole"
    }
  ]
}
EOF
  aws iam create-role \
    --role-name "$ROLE_NAME" \
    --assume-role-policy-document "file://${TRUST_FILE}" \
    "${PROFILE_ARGS[@]}" \
    --no-cli-pager \
    --output json >/dev/null
  aws iam attach-role-policy \
    --role-name "$ROLE_NAME" \
    --policy-arn arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole \
    "${PROFILE_ARGS[@]}"
  sleep 10
fi

cp "$SCRIPT_DIR/mcp_server.py" "$TEMP_DIR/"
cp "$SCRIPT_DIR/mcp_tools.py" "$TEMP_DIR/"
cp "$SCRIPT_DIR/crs_client.py" "$TEMP_DIR/"
cp "$SCRIPT_DIR/auth.py" "$TEMP_DIR/"

(
  cd "$TEMP_DIR"
  zip -rq mcp_server.zip mcp_server.py mcp_tools.py crs_client.py auth.py
)

ENV_FILE="$TEMP_DIR/env.json"
python3 - "$ENV_FILE" <<'PY'
import json
import os
import sys

path = sys.argv[1]
payload = {
    "Variables": {
        "MCP_API_KEY_SHA256": os.environ["MCP_API_KEY_SHA256"],
        "CRS_API_BASE_URL": os.environ["CRS_API_BASE_URL"],
        "CRS_EMAIL": os.environ["CRS_EMAIL"],
        "CRS_PASSWORD": os.environ["CRS_PASSWORD"],
    }
}
with open(path, "w", encoding="utf-8") as handle:
    json.dump(payload, handle)
PY

if aws lambda get-function --function-name "$FUNCTION_NAME" --region "$REGION" "${PROFILE_ARGS[@]}" >/dev/null 2>&1; then
  log "Updating function code..."
  aws lambda update-function-code \
    --function-name "$FUNCTION_NAME" \
    --zip-file "fileb://${TEMP_DIR}/mcp_server.zip" \
    --region "$REGION" \
    "${PROFILE_ARGS[@]}" \
    --no-cli-pager \
    --output json >/dev/null
  aws lambda wait function-updated-v2 \
    --function-name "$FUNCTION_NAME" \
    --region "$REGION" \
    "${PROFILE_ARGS[@]}"
else
  log "Creating function..."
  aws lambda create-function \
    --function-name "$FUNCTION_NAME" \
    --runtime "$RUNTIME" \
    --role "$ROLE_ARN" \
    --handler "$HANDLER" \
    --timeout "$TIMEOUT" \
    --memory-size "$MEMORY_SIZE" \
    --environment "file://${ENV_FILE}" \
    --zip-file "fileb://${TEMP_DIR}/mcp_server.zip" \
    --region "$REGION" \
    "${PROFILE_ARGS[@]}" \
    --no-cli-pager \
    --output json >/dev/null
  aws lambda wait function-active-v2 \
    --function-name "$FUNCTION_NAME" \
    --region "$REGION" \
    "${PROFILE_ARGS[@]}"
fi

aws lambda update-function-configuration \
  --function-name "$FUNCTION_NAME" \
  --handler "$HANDLER" \
  --runtime "$RUNTIME" \
  --timeout "$TIMEOUT" \
  --memory-size "$MEMORY_SIZE" \
  --environment "file://${ENV_FILE}" \
  --region "$REGION" \
  "${PROFILE_ARGS[@]}" \
  --no-cli-pager \
  --output json >/dev/null

aws lambda wait function-updated-v2 \
  --function-name "$FUNCTION_NAME" \
  --region "$REGION" \
  "${PROFILE_ARGS[@]}"

if ! aws lambda get-function-url-config \
  --function-name "$FUNCTION_NAME" \
  --region "$REGION" \
  "${PROFILE_ARGS[@]}" >/dev/null 2>&1; then
  aws lambda create-function-url-config \
    --function-name "$FUNCTION_NAME" \
    --auth-type NONE \
    --cors "$CORS_CONFIG" \
    --region "$REGION" \
    "${PROFILE_ARGS[@]}" \
    --no-cli-pager \
    --output json >/dev/null
fi

aws lambda update-function-url-config \
  --function-name "$FUNCTION_NAME" \
  --auth-type NONE \
  --cors "$CORS_CONFIG" \
  --region "$REGION" \
  "${PROFILE_ARGS[@]}" \
  --no-cli-pager \
  --output json >/dev/null

if ! aws lambda get-policy \
  --function-name "$FUNCTION_NAME" \
  --region "$REGION" \
  "${PROFILE_ARGS[@]}" 2>/dev/null | grep -q "$STATEMENT_ID"; then
  aws lambda add-permission \
    --function-name "$FUNCTION_NAME" \
    --statement-id "$STATEMENT_ID" \
    --action lambda:InvokeFunctionUrl \
    --principal '*' \
    --function-url-auth-type NONE \
    --region "$REGION" \
    "${PROFILE_ARGS[@]}" \
    --no-cli-pager \
    --output json >/dev/null
fi

FUNCTION_URL="$(aws lambda get-function-url-config \
  --function-name "$FUNCTION_NAME" \
  --region "$REGION" \
  "${PROFILE_ARGS[@]}" \
  --query FunctionUrl \
  --output text)"

log "Update complete."
printf 'MCP endpoint: %s\n' "$FUNCTION_URL"
printf 'Mint a key with: python mcp/create_api_key.py\n'
