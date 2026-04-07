#!/bin/bash
set -e

# CRS AWS Deployment Script
# This script deploys all CRS infrastructure to AWS
# All resources are prefixed with 'crs-' for clear separation

# Prevent Git Bash on Windows from converting paths
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

# Configuration
REGION="${AWS_REGION:-us-west-2}"
ENVIRONMENT="${ENVIRONMENT:-dev}"
PREFIX="crs"
PROJECT_TAG="CRS"
ENABLE_OPENSEARCH="${ENABLE_OPENSEARCH:-false}"
METRICS_NAMESPACE="${METRICS_NAMESPACE:-CRS/Application}"
TRACE_SAMPLE_RATIO="${TRACE_SAMPLE_RATIO:-0.25}"
API_SERVICE_NAME="${API_SERVICE_NAME:-crs-api}"
JOBS_SERVICE_NAME="${JOBS_SERVICE_NAME:-crs-jobs}"
ADOT_COLLECTOR_IMAGE="${ADOT_COLLECTOR_IMAGE:-public.ecr.aws/aws-observability/aws-otel-collector:latest}"
LOG_RETENTION_DAYS="${LOG_RETENTION_DAYS:-30}"
DEPLOY_ECS_EXPRESS="${DEPLOY_ECS_EXPRESS:-true}"
API_CLUSTER_NAME="${API_CLUSTER_NAME:-${PREFIX}-cluster}"
API_EXPRESS_SERVICE_NAME="${API_EXPRESS_SERVICE_NAME:-${PREFIX}-api}"
API_REVERSE_PROXY_NETWORK="${API_REVERSE_PROXY_NETWORK:-10.1.0.0/16}"
API_EXPRESS_LOG_GROUP="${API_EXPRESS_LOG_GROUP:-/crs/api}"
API_EXPRESS_LOG_STREAM_PREFIX="${API_EXPRESS_LOG_STREAM_PREFIX:-ecs-express}"
OTEL_COLLECTOR_SERVICE_NAME="${OTEL_COLLECTOR_SERVICE_NAME:-${PREFIX}-otel-collector}"
OTEL_COLLECTOR_TASK_FAMILY="${OTEL_COLLECTOR_TASK_FAMILY:-${PREFIX}-otel-collector-task}"
OTEL_COLLECTOR_CONTAINER_NAME="${OTEL_COLLECTOR_CONTAINER_NAME:-aws-otel-collector}"
OTEL_COLLECTOR_NAMESPACE_NAME="${OTEL_COLLECTOR_NAMESPACE_NAME:-crs.internal}"
OTEL_COLLECTOR_DISCOVERY_SERVICE_NAME="${OTEL_COLLECTOR_DISCOVERY_SERVICE_NAME:-otel-collector}"
OTEL_COLLECTOR_ENDPOINT="${OTEL_COLLECTOR_ENDPOINT:-http://otel-collector.crs.internal:4317}"
RUM_APP_MONITOR_NAME="${RUM_APP_MONITOR_NAME:-${PREFIX}-web}"
RUM_SESSION_SAMPLE_RATE="${RUM_SESSION_SAMPLE_RATE:-0.1}"
RUM_ALLOW_COOKIES="${RUM_ALLOW_COOKIES:-true}"
RUM_ENABLE_XRAY="${RUM_ENABLE_XRAY:-true}"
RUM_CW_LOGS_ENABLED="${RUM_CW_LOGS_ENABLED:-true}"
RUM_SOURCE_MAPS_PREFIX="${RUM_SOURCE_MAPS_PREFIX:-rum-source-maps}"
RUM_IDENTITY_POOL_ID="${RUM_IDENTITY_POOL_ID:-}"
RUM_GUEST_ROLE_ARN="${RUM_GUEST_ROLE_ARN:-}"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

log_info() { echo -e "${GREEN}[INFO]${NC} $1"; }
log_warn() { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }

to_native_path() {
    local file_path="$1"
    if command -v cygpath >/dev/null 2>&1; then
        cygpath -w "$file_path"
    else
        echo "$file_path"
    fi
}

require_config_value() {
    local name="$1"
    local value="$2"
    local location="$3"
    local placeholder="${4:-}"

    if [ -z "$value" ] || { [ -n "$placeholder" ] && [ "$value" = "$placeholder" ]; }; then
        log_error "Please set ${name} in ${location}"
        exit 1
    fi
}

resolve_secrets_file() {
    if [ -f "${REPO_ROOT}/secrets.env" ]; then
        echo "${REPO_ROOT}/secrets.env"
    elif [ -f "${SCRIPT_DIR}/secrets.env" ]; then
        echo "${SCRIPT_DIR}/secrets.env"
    else
        echo "${REPO_ROOT}/secrets.env"
    fi
}

ensure_inline_role_policy() {
    local role_name="$1"
    local policy_name="$2"
    local policy_file="$3"

    aws iam put-role-policy \
        --role-name "$role_name" \
        --policy-name "$policy_name" \
        --policy-document "$(cat "$policy_file")" > /dev/null
}

ensure_managed_role_policy() {
    local role_name="$1"
    local policy_arn="$2"

    local attached_policy
    attached_policy=$(aws iam list-attached-role-policies \
        --role-name "$role_name" \
        --query "AttachedPolicies[?PolicyArn=='${policy_arn}'].PolicyArn | [0]" \
        --output text 2>/dev/null || echo "")

    if [ -z "$attached_policy" ] || [ "$attached_policy" = "None" ]; then
        aws iam attach-role-policy \
            --role-name "$role_name" \
            --policy-arn "$policy_arn" > /dev/null
    fi
}

ensure_secret_string_exists() {
    local secret_name="$1"
    local secret_value="$2"

    if [ -z "$secret_value" ]; then
        return
    fi

    if aws secretsmanager describe-secret --secret-id "$secret_name" --region "$REGION" > /dev/null 2>&1; then
        return
    fi

    aws secretsmanager create-secret \
        --name "$secret_name" \
        --secret-string "$secret_value" \
        --tags Key=Project,Value=${PROJECT_TAG} \
        --region "$REGION" > /dev/null
    log_info "Created secret: $secret_name"
}

get_secret_arn() {
    local secret_name="$1"

    aws secretsmanager describe-secret \
        --secret-id "$secret_name" \
        --query 'ARN' \
        --output text \
        --region "$REGION" 2>/dev/null || echo ""
}

resolve_ecs_express_service_arn() {
    aws ecs list-services \
        --cluster "$API_CLUSTER_NAME" \
        --region "$REGION" \
        --query "serviceArns[?contains(@, '/${API_EXPRESS_SERVICE_NAME}')] | [0]" \
        --output text 2>/dev/null || echo ""
}

resolve_ecs_express_endpoint() {
    local service_arn="$1"

    aws ecs describe-express-gateway-service \
        --service-arn "$service_arn" \
        --region "$REGION" \
        --query 'service.activeConfigurations[0].ingressPaths[0].endpoint' \
        --output text 2>/dev/null || echo ""
}

wait_for_ecs_express_status() {
    local service_arn="$1"
    local desired_status="${2:-ACTIVE}"
    local attempts="${3:-60}"

    for ((i = 1; i <= attempts; i++)); do
        local current_status
        current_status=$(aws ecs describe-express-gateway-service \
            --service-arn "$service_arn" \
            --region "$REGION" \
            --query 'service.status.statusCode' \
            --output text 2>/dev/null || echo "UNKNOWN")

        if [ "$current_status" = "$desired_status" ]; then
            return 0
        fi

        echo -n "."
        sleep 10
    done

    echo ""
    return 1
}

build_default_api_runtime_image_config() {
    local destination_file="$1"
    local connection_string="$2"
    local destination_file_native
    destination_file_native=$(to_native_path "$destination_file")

    python - "$destination_file_native" "$connection_string" "$OpenAI__ApiKey" "$JWT_SECRET" "$X__ClientId" "$X__ClientSecret" "$X__RedirectUri" "$WEB_URL" "${CF_URL:-$WEB_URL}" "$ENVIRONMENT" "$API_SERVICE_NAME" "$METRICS_NAMESPACE" "$TRACE_SAMPLE_RATIO" "$API_REVERSE_PROXY_NETWORK" "$OTEL_COLLECTOR_ENDPOINT" "$OPENSEARCH_ENDPOINT" <<'PY'
import json
import sys
from pathlib import Path

(
    destination,
    connection_string,
    openai_api_key,
    jwt_secret,
    x_client_id,
    x_client_secret,
    x_redirect_uri,
    web_url,
    cloudfront_url,
    environment_name,
    service_name,
    metrics_namespace,
    trace_sample_ratio,
    reverse_proxy_network,
    otel_endpoint,
    opensearch_endpoint,
) = sys.argv[1:]

env = {
    "ASPNETCORE_ENVIRONMENT": "Production",
    "ConnectionStrings__DefaultConnection": connection_string,
    "OpenAI__ApiKey": openai_api_key,
    "Embedding__ModelName": "text-embedding-3-small",
    "Embedding__Dimensions": "1536",
    "JwtSettings__SecretKey": jwt_secret,
    "JwtSettings__ExpirationMinutes": "60",
    "Cors__AllowedOrigins__0": web_url,
    "Cors__AllowedOrigins__1": cloudfront_url,
    "Registration__Enabled": "true",
    "Observability__Environment": environment_name,
    "Observability__ExecutionEnvironment": "aws",
    "Observability__ServiceName": service_name,
    "Observability__ServiceNamespace": "crs",
    "Observability__MetricsNamespace": metrics_namespace,
    "Observability__TraceSampleRatio": trace_sample_ratio,
    "Observability__EnableSensitiveBodyLogging": "false",
    "OTEL_EXPORTER_OTLP_ENDPOINT": otel_endpoint,
    "OTEL_EXPORTER_OTLP_PROTOCOL": "grpc",
    "OTEL_METRICS_EXPORTER": "none",
    "OTEL_LOGS_EXPORTER": "none",
    "OTEL_PROPAGATORS": "xray",
    "ReverseProxy__KnownNetworks__0": reverse_proxy_network,
}

if x_client_id:
    env["X__ClientId"] = x_client_id
if x_client_secret:
    env["X__ClientSecret"] = x_client_secret
if x_redirect_uri:
    env["X__RedirectUri"] = x_redirect_uri
if opensearch_endpoint:
    env["OpenSearch__Endpoint"] = opensearch_endpoint
    env["OpenSearch__IndexName"] = "crs-content"
    env["OpenSearch__EmbeddingDimensions"] = "1536"

payload = {
    "Port": "8080",
    "RuntimeEnvironmentVariables": env,
    "RuntimeEnvironmentSecrets": {},
}

Path(destination).write_text(json.dumps(payload))
PY
}

build_runtime_image_config_from_ecs_express_container() {
    local destination_file="$1"
    local primary_container_file="$2"
    local destination_file_native
    local primary_container_file_native
    destination_file_native=$(to_native_path "$destination_file")
    primary_container_file_native=$(to_native_path "$primary_container_file")

    python - "$destination_file_native" "$primary_container_file_native" <<'PY'
import json
import sys
from pathlib import Path

destination, primary_container_path = sys.argv[1:]
payload = json.loads(Path(primary_container_path).read_text())
env = {
    entry["name"]: entry.get("value", "")
    for entry in (payload.get("environment") or [])
    if entry.get("name")
}
result = {
    "Port": str(payload.get("containerPort", "8080")),
    "RuntimeEnvironmentVariables": env,
    "RuntimeEnvironmentSecrets": {},
}
Path(destination).write_text(json.dumps(result))
PY
}

build_api_primary_container_json() {
    local image_identifier="$1"

    python - "${API_RUNTIME_IMAGE_CONFIG_FILE_NATIVE:-$API_RUNTIME_IMAGE_CONFIG_FILE}" "$image_identifier" "$API_EXPRESS_LOG_GROUP" "$API_EXPRESS_LOG_STREAM_PREFIX" "$CONNECTION_STRING_SECRET_ARN" "$OPENAI_API_KEY_SECRET_ARN" "$JWT_SECRET_SECRET_ARN" "$X_CLIENT_SECRET_SECRET_ARN" "$OTEL_COLLECTOR_ENDPOINT" "$API_REVERSE_PROXY_NETWORK" <<'PY'
import json
import sys
from pathlib import Path

(
    config_file,
    image_identifier,
    log_group,
    log_stream_prefix,
    connection_secret_arn,
    openai_secret_arn,
    jwt_secret_arn,
    x_secret_arn,
    otel_endpoint,
    reverse_proxy_network,
) = sys.argv[1:]

payload = json.loads(Path(config_file).read_text())
env = dict(payload.get("RuntimeEnvironmentVariables") or {})

secret_map = {
    "ConnectionStrings__DefaultConnection": connection_secret_arn,
    "OpenAI__ApiKey": openai_secret_arn,
    "JwtSettings__SecretKey": jwt_secret_arn,
    "X__ClientSecret": x_secret_arn,
}

for secret_key in secret_map:
    env.pop(secret_key, None)

env["OTEL_EXPORTER_OTLP_ENDPOINT"] = otel_endpoint
env["OTEL_EXPORTER_OTLP_PROTOCOL"] = "grpc"
env["ReverseProxy__KnownNetworks__0"] = reverse_proxy_network

environment = [{"name": key, "value": str(value)} for key, value in sorted(env.items()) if value is not None]
secrets = [{"name": key, "valueFrom": arn} for key, arn in secret_map.items() if arn]

container = {
    "image": image_identifier,
    "containerPort": int(payload.get("Port", "8080")),
    "awsLogsConfiguration": {
        "logGroup": log_group,
        "logStreamPrefix": log_stream_prefix,
    },
    "environment": environment,
    "secrets": secrets,
}

print(json.dumps(container, separators=(",", ":")))
PY
}

prepare_api_runtime_configuration() {
    log_info "Preparing API runtime configuration..."

    API_RUNTIME_IMAGE_CONFIG_FILE=$(mktemp)
    API_RUNTIME_IMAGE_CONFIG_FILE_NATIVE=$(to_native_path "$API_RUNTIME_IMAGE_CONFIG_FILE")
    local connection_string
    connection_string="Host=${RDS_ENDPOINT};Database=crsdb;Username=${DB_USERNAME};Password=${DB_PASSWORD}"
    local ecs_express_service_arn
    ecs_express_service_arn=$(resolve_ecs_express_service_arn)

    if [ -n "$ecs_express_service_arn" ] && [ "$ecs_express_service_arn" != "None" ]; then
        local ecs_primary_container_file
        ecs_primary_container_file=$(mktemp)
        aws ecs describe-express-gateway-service \
            --service-arn "$ecs_express_service_arn" \
            --region "$REGION" \
            --query 'service.activeConfigurations[0].primaryContainer' \
            --output json > "$ecs_primary_container_file"
        build_runtime_image_config_from_ecs_express_container "$API_RUNTIME_IMAGE_CONFIG_FILE" "$ecs_primary_container_file"
        API_IMAGE_IDENTIFIER=$(aws ecs describe-express-gateway-service \
            --service-arn "$ecs_express_service_arn" \
            --region "$REGION" \
            --query 'service.activeConfigurations[0].primaryContainer.image' \
            --output text)
        rm -f "$ecs_primary_container_file"
        log_info "Using live ECS Express runtime configuration as the deployment seed"
    else
        build_default_api_runtime_image_config "$API_RUNTIME_IMAGE_CONFIG_FILE" "$connection_string"
        API_IMAGE_IDENTIFIER="${ECR_URI}/crs-api:latest"
        log_warn "ECS Express service not found. Falling back to deploy.sh runtime defaults for API configuration."
    fi

    ensure_secret_string_exists "${PREFIX}-secrets/openai-api-key" "$OpenAI__ApiKey"
    ensure_secret_string_exists "${PREFIX}-secrets/jwt-secret" "$JWT_SECRET"
    ensure_secret_string_exists "${PREFIX}-secrets/connection-string" "$connection_string"
    ensure_secret_string_exists "${PREFIX}-secrets/x-client-secret" "$X__ClientSecret"

    OPENAI_API_KEY_SECRET_ARN=$(get_secret_arn "${PREFIX}-secrets/openai-api-key")
    JWT_SECRET_SECRET_ARN=$(get_secret_arn "${PREFIX}-secrets/jwt-secret")
    CONNECTION_STRING_SECRET_ARN=$(get_secret_arn "${PREFIX}-secrets/connection-string")
    X_CLIENT_SECRET_SECRET_ARN=$(get_secret_arn "${PREFIX}-secrets/x-client-secret")

    export API_RUNTIME_IMAGE_CONFIG_FILE API_RUNTIME_IMAGE_CONFIG_FILE_NATIVE API_IMAGE_IDENTIFIER
    export OPENAI_API_KEY_SECRET_ARN JWT_SECRET_SECRET_ARN CONNECTION_STRING_SECRET_ARN X_CLIENT_SECRET_SECRET_ARN
}

# Check AWS CLI is configured
check_aws_cli() {
    log_info "Checking AWS CLI configuration..."
    if ! aws sts get-caller-identity &> /dev/null; then
        log_error "AWS CLI is not configured. Run 'aws configure' first."
        exit 1
    fi
    ACCOUNT_ID=$(aws sts get-caller-identity --query Account --output text)
    log_info "Using AWS Account: $ACCOUNT_ID"
}

# Create VPC and networking
create_vpc() {
    log_info "Creating VPC and networking..."

    # Check if VPC already exists
    VPC_ID=$(aws ec2 describe-vpcs --filters "Name=tag:Name,Values=${PREFIX}-vpc" --query 'Vpcs[0].VpcId' --output text --region $REGION 2>/dev/null || echo "None")

    if [ "$VPC_ID" != "None" ] && [ -n "$VPC_ID" ]; then
        log_info "VPC already exists: $VPC_ID"
    else
        # Create VPC
        VPC_ID=$(aws ec2 create-vpc \
            --cidr-block 10.1.0.0/16 \
            --tag-specifications "ResourceType=vpc,Tags=[{Key=Name,Value=${PREFIX}-vpc},{Key=Project,Value=${PROJECT_TAG}}]" \
            --query 'Vpc.VpcId' \
            --output text \
            --region $REGION)
        log_info "Created VPC: $VPC_ID"

        # Enable DNS hostnames
        aws ec2 modify-vpc-attribute --vpc-id $VPC_ID --enable-dns-hostnames '{"Value":true}' --region $REGION
    fi

    # Create Internet Gateway
    IGW_ID=$(aws ec2 describe-internet-gateways --filters "Name=tag:Name,Values=${PREFIX}-igw" --query 'InternetGateways[0].InternetGatewayId' --output text --region $REGION 2>/dev/null || echo "None")

    if [ "$IGW_ID" = "None" ] || [ -z "$IGW_ID" ]; then
        IGW_ID=$(aws ec2 create-internet-gateway \
            --tag-specifications "ResourceType=internet-gateway,Tags=[{Key=Name,Value=${PREFIX}-igw},{Key=Project,Value=${PROJECT_TAG}}]" \
            --query 'InternetGateway.InternetGatewayId' \
            --output text \
            --region $REGION)
        aws ec2 attach-internet-gateway --vpc-id $VPC_ID --internet-gateway-id $IGW_ID --region $REGION
        log_info "Created Internet Gateway: $IGW_ID"
    fi

    # Create public subnets in 2 AZs (required for RDS and load balancing)
    SUBNET_1_ID=$(aws ec2 describe-subnets --filters "Name=tag:Name,Values=${PREFIX}-subnet-1" --query 'Subnets[0].SubnetId' --output text --region $REGION 2>/dev/null || echo "None")

    if [ "$SUBNET_1_ID" = "None" ] || [ -z "$SUBNET_1_ID" ]; then
        SUBNET_1_ID=$(aws ec2 create-subnet \
            --vpc-id $VPC_ID \
            --cidr-block 10.1.1.0/24 \
            --availability-zone ${REGION}a \
            --tag-specifications "ResourceType=subnet,Tags=[{Key=Name,Value=${PREFIX}-subnet-1},{Key=Project,Value=${PROJECT_TAG}}]" \
            --query 'Subnet.SubnetId' \
            --output text \
            --region $REGION)
        aws ec2 modify-subnet-attribute --subnet-id $SUBNET_1_ID --map-public-ip-on-launch --region $REGION
        log_info "Created Subnet 1: $SUBNET_1_ID"
    fi

    SUBNET_2_ID=$(aws ec2 describe-subnets --filters "Name=tag:Name,Values=${PREFIX}-subnet-2" --query 'Subnets[0].SubnetId' --output text --region $REGION 2>/dev/null || echo "None")

    if [ "$SUBNET_2_ID" = "None" ] || [ -z "$SUBNET_2_ID" ]; then
        SUBNET_2_ID=$(aws ec2 create-subnet \
            --vpc-id $VPC_ID \
            --cidr-block 10.1.2.0/24 \
            --availability-zone ${REGION}b \
            --tag-specifications "ResourceType=subnet,Tags=[{Key=Name,Value=${PREFIX}-subnet-2},{Key=Project,Value=${PROJECT_TAG}}]" \
            --query 'Subnet.SubnetId' \
            --output text \
            --region $REGION)
        aws ec2 modify-subnet-attribute --subnet-id $SUBNET_2_ID --map-public-ip-on-launch --region $REGION
        log_info "Created Subnet 2: $SUBNET_2_ID"
    fi

    # Create Route Table and associate
    RTB_ID=$(aws ec2 describe-route-tables --filters "Name=tag:Name,Values=${PREFIX}-rtb" --query 'RouteTables[0].RouteTableId' --output text --region $REGION 2>/dev/null || echo "None")

    if [ "$RTB_ID" = "None" ] || [ -z "$RTB_ID" ]; then
        RTB_ID=$(aws ec2 create-route-table \
            --vpc-id $VPC_ID \
            --tag-specifications "ResourceType=route-table,Tags=[{Key=Name,Value=${PREFIX}-rtb},{Key=Project,Value=${PROJECT_TAG}}]" \
            --query 'RouteTable.RouteTableId' \
            --output text \
            --region $REGION)
        aws ec2 create-route --route-table-id $RTB_ID --destination-cidr-block 0.0.0.0/0 --gateway-id $IGW_ID --region $REGION
        aws ec2 associate-route-table --subnet-id $SUBNET_1_ID --route-table-id $RTB_ID --region $REGION
        aws ec2 associate-route-table --subnet-id $SUBNET_2_ID --route-table-id $RTB_ID --region $REGION
        log_info "Created Route Table: $RTB_ID"
    fi

    # Export for other functions
    export VPC_ID SUBNET_1_ID SUBNET_2_ID
}

# Create Security Groups
create_security_groups() {
    log_info "Creating Security Groups..."

    # API Security Group
    API_SG_ID=$(aws ec2 describe-security-groups --filters "Name=tag:Name,Values=${PREFIX}-api-sg" "Name=vpc-id,Values=$VPC_ID" --query 'SecurityGroups[0].GroupId' --output text --region $REGION 2>/dev/null || echo "None")

    if [ "$API_SG_ID" = "None" ] || [ -z "$API_SG_ID" ]; then
        API_SG_ID=$(aws ec2 create-security-group \
            --group-name "${PREFIX}-api-sg" \
            --description "Security group for CRS API" \
            --vpc-id $VPC_ID \
            --tag-specifications "ResourceType=security-group,Tags=[{Key=Name,Value=${PREFIX}-api-sg},{Key=Project,Value=${PROJECT_TAG}}]" \
            --query 'GroupId' \
            --output text \
            --region $REGION)
        aws ec2 authorize-security-group-ingress --group-id $API_SG_ID --protocol tcp --port 8080 --cidr 0.0.0.0/0 --region $REGION
        aws ec2 authorize-security-group-ingress --group-id $API_SG_ID --protocol tcp --port 443 --cidr 0.0.0.0/0 --region $REGION
        log_info "Created API Security Group: $API_SG_ID"
    fi

    # RDS Security Group
    RDS_SG_ID=$(aws ec2 describe-security-groups --filters "Name=tag:Name,Values=${PREFIX}-rds-sg" "Name=vpc-id,Values=$VPC_ID" --query 'SecurityGroups[0].GroupId' --output text --region $REGION 2>/dev/null || echo "None")

    if [ "$RDS_SG_ID" = "None" ] || [ -z "$RDS_SG_ID" ]; then
        RDS_SG_ID=$(aws ec2 create-security-group \
            --group-name "${PREFIX}-rds-sg" \
            --description "Security group for CRS RDS" \
            --vpc-id $VPC_ID \
            --tag-specifications "ResourceType=security-group,Tags=[{Key=Name,Value=${PREFIX}-rds-sg},{Key=Project,Value=${PROJECT_TAG}}]" \
            --query 'GroupId' \
            --output text \
            --region $REGION)
        # Allow PostgreSQL from API security group
        aws ec2 authorize-security-group-ingress --group-id $RDS_SG_ID --protocol tcp --port 5432 --source-group $API_SG_ID --region $REGION
        # Allow from anywhere for initial setup (you can restrict this later)
        aws ec2 authorize-security-group-ingress --group-id $RDS_SG_ID --protocol tcp --port 5432 --cidr 0.0.0.0/0 --region $REGION
        log_info "Created RDS Security Group: $RDS_SG_ID"
    fi

    API_EXPRESS_SG_ID=$(aws ec2 describe-security-groups --filters "Name=tag:Name,Values=${PREFIX}-api-express-sg" "Name=vpc-id,Values=$VPC_ID" --query 'SecurityGroups[0].GroupId' --output text --region $REGION 2>/dev/null || echo "None")

    if [ "$API_EXPRESS_SG_ID" = "None" ] || [ -z "$API_EXPRESS_SG_ID" ]; then
        API_EXPRESS_SG_ID=$(aws ec2 create-security-group \
            --group-name "${PREFIX}-api-express-sg" \
            --description "Security group for CRS ECS Express API tasks" \
            --vpc-id $VPC_ID \
            --tag-specifications "ResourceType=security-group,Tags=[{Key=Name,Value=${PREFIX}-api-express-sg},{Key=Project,Value=${PROJECT_TAG}}]" \
            --query 'GroupId' \
            --output text \
            --region $REGION)
        log_info "Created ECS Express API Security Group: $API_EXPRESS_SG_ID"
    fi

    aws ec2 authorize-security-group-ingress --group-id $API_EXPRESS_SG_ID --protocol tcp --port 8080 --cidr 10.1.0.0/16 --region $REGION 2>/dev/null || true
    aws ec2 authorize-security-group-ingress --group-id $RDS_SG_ID --protocol tcp --port 5432 --source-group $API_EXPRESS_SG_ID --region $REGION 2>/dev/null || true

    OTEL_COLLECTOR_SG_ID=$(aws ec2 describe-security-groups --filters "Name=tag:Name,Values=${PREFIX}-otel-collector-sg" "Name=vpc-id,Values=$VPC_ID" --query 'SecurityGroups[0].GroupId' --output text --region $REGION 2>/dev/null || echo "None")

    if [ "$OTEL_COLLECTOR_SG_ID" = "None" ] || [ -z "$OTEL_COLLECTOR_SG_ID" ]; then
        OTEL_COLLECTOR_SG_ID=$(aws ec2 create-security-group \
            --group-name "${PREFIX}-otel-collector-sg" \
            --description "Security group for CRS OTEL collector" \
            --vpc-id $VPC_ID \
            --tag-specifications "ResourceType=security-group,Tags=[{Key=Name,Value=${PREFIX}-otel-collector-sg},{Key=Project,Value=${PROJECT_TAG}}]" \
            --query 'GroupId' \
            --output text \
            --region $REGION)
        log_info "Created OTEL collector Security Group: $OTEL_COLLECTOR_SG_ID"
    fi

    aws ec2 authorize-security-group-ingress --group-id $OTEL_COLLECTOR_SG_ID --protocol tcp --port 4317 --source-group $API_EXPRESS_SG_ID --region $REGION 2>/dev/null || true

    export API_SG_ID RDS_SG_ID API_EXPRESS_SG_ID OTEL_COLLECTOR_SG_ID
}

# Create ECR Repositories
create_ecr() {
    log_info "Creating ECR Repositories..."

    for REPO in "crs-api" "crs-jobs"; do
        if ! aws ecr describe-repositories --repository-names $REPO --region $REGION &> /dev/null; then
            aws ecr create-repository \
                --repository-name $REPO \
                --image-scanning-configuration scanOnPush=true \
                --tags Key=Project,Value=${PROJECT_TAG} \
                --region $REGION
            log_info "Created ECR repository: $REPO"
        else
            log_info "ECR repository already exists: $REPO"
        fi
    done

    ECR_URI="${ACCOUNT_ID}.dkr.ecr.${REGION}.amazonaws.com"
    export ECR_URI
}

# Create Secrets in Secrets Manager
create_secrets() {
    log_info "Creating Secrets Manager secrets..."

    # Check if secrets file exists (handle both running from repo root and from aws directory)
    SECRETS_FILE=$(resolve_secrets_file)
    if [ ! -f "$SECRETS_FILE" ]; then
        log_warn "Secrets file not found. Creating template at secrets.env"
        SECRETS_FILE="secrets.env"
        cat > "$SECRETS_FILE" << 'EOF'
# CRS Secrets Configuration
# Fill in these values and re-run the deploy script

# Database
DB_PASSWORD=your-strong-password-here
SQL_ADMIN_USERNAME=crsadmin

# OpenAI API Key (get from https://platform.openai.com/api-keys)
OpenAI__ApiKey=sk-your-openai-key

# JWT Secret (generate a random 64+ character string)
JWT_SECRET=your-jwt-secret-key-minimum-64-characters-long-for-security

# Optional X OAuth settings. You can also provide these as local environment variables
# before running deploy.sh so they never live on disk.
X__ClientId=
X__ClientSecret=
X__RedirectUri=

# OpenSearch (will be auto-populated after creation)
OPENSEARCH_ENDPOINT=
EOF
        log_error "Please fill in the secrets in $SECRETS_FILE and run this script again."
        exit 1
    fi

    log_info "Loading application secrets from $SECRETS_FILE"
    source "$SECRETS_FILE"

    DB_USERNAME="${SQL_ADMIN_USERNAME:-crsadmin}"

    require_config_value "DB_PASSWORD" "$DB_PASSWORD" "$SECRETS_FILE" "your-strong-password-here"
    require_config_value "OpenAI__ApiKey" "$OpenAI__ApiKey" "$SECRETS_FILE" "sk-your-openai-key"
    require_config_value "JWT_SECRET" "$JWT_SECRET" "$SECRETS_FILE" "your-jwt-secret-key-minimum-64-characters-long-for-security"

    if { [ -n "$X__ClientId" ] || [ -n "$X__ClientSecret" ] || [ -n "$X__RedirectUri" ]; } &&
       { [ -z "$X__ClientId" ] || [ -z "$X__ClientSecret" ] || [ -z "$X__RedirectUri" ]; }; then
        log_error "Set X__ClientId, X__ClientSecret, and X__RedirectUri together in $SECRETS_FILE."
        exit 1
    fi

    if [ -z "$X__ClientId" ]; then
        log_warn "X OAuth settings were not provided. X connect and X token refresh will stay unconfigured until you set X__ClientId, X__ClientSecret, and X__RedirectUri."
    fi

    # Create or update secrets
    for SECRET_NAME in "${PREFIX}-secrets/db-password" "${PREFIX}-secrets/openai-api-key" "${PREFIX}-secrets/jwt-secret"; do
        if ! aws secretsmanager describe-secret --secret-id $SECRET_NAME --region $REGION &> /dev/null; then
            case $SECRET_NAME in
                *db-password)
                    aws secretsmanager create-secret --name $SECRET_NAME --secret-string "$DB_PASSWORD" --tags Key=Project,Value=${PROJECT_TAG} --region $REGION
                    ;;
                *openai-api-key)
                    aws secretsmanager create-secret --name $SECRET_NAME --secret-string "$OpenAI__ApiKey" --tags Key=Project,Value=${PROJECT_TAG} --region $REGION
                    ;;
                *jwt-secret)
                    aws secretsmanager create-secret --name $SECRET_NAME --secret-string "$JWT_SECRET" --tags Key=Project,Value=${PROJECT_TAG} --region $REGION
                    ;;
            esac
            log_info "Created secret: $SECRET_NAME"
        else
            log_info "Secret already exists: $SECRET_NAME"
        fi
    done

    export DB_PASSWORD OpenAI__ApiKey JWT_SECRET DB_USERNAME X__ClientId X__ClientSecret X__RedirectUri
}

# Create RDS PostgreSQL
create_rds() {
    log_info "Creating RDS PostgreSQL instance..."

    # Create DB Subnet Group
    if ! aws rds describe-db-subnet-groups --db-subnet-group-name ${PREFIX}-db-subnet --region $REGION &> /dev/null; then
        aws rds create-db-subnet-group \
            --db-subnet-group-name ${PREFIX}-db-subnet \
            --db-subnet-group-description "CRS Database Subnet Group" \
            --subnet-ids $SUBNET_1_ID $SUBNET_2_ID \
            --tags Key=Project,Value=${PROJECT_TAG} \
            --region $REGION
        log_info "Created DB Subnet Group: ${PREFIX}-db-subnet"
    fi

    # Create RDS instance
    if ! aws rds describe-db-instances --db-instance-identifier ${PREFIX}-db --region $REGION &> /dev/null; then
        aws rds create-db-instance \
            --db-instance-identifier ${PREFIX}-db \
            --db-instance-class db.t3.micro \
            --engine postgres \
            --engine-version 15 \
            --master-username "$DB_USERNAME" \
            --master-user-password "$DB_PASSWORD" \
            --allocated-storage 20 \
            --vpc-security-group-ids $RDS_SG_ID \
            --db-subnet-group-name ${PREFIX}-db-subnet \
            --db-name crsdb \
            --publicly-accessible \
            --backup-retention-period 7 \
            --storage-encrypted \
            --tags Key=Project,Value=${PROJECT_TAG} \
            --region $REGION
        log_info "Creating RDS instance: ${PREFIX}-db (this takes 5-10 minutes...)"

        # Wait for RDS to be available
        log_info "Waiting for RDS instance to be available..."
        aws rds wait db-instance-available --db-instance-identifier ${PREFIX}-db --region $REGION
        log_info "RDS instance is now available!"
    else
        log_info "RDS instance already exists: ${PREFIX}-db"
    fi

    # Get RDS endpoint
    RDS_ENDPOINT=$(aws rds describe-db-instances \
        --db-instance-identifier ${PREFIX}-db \
        --query 'DBInstances[0].Endpoint.Address' \
        --output text \
        --region $REGION)

    log_info "RDS Endpoint: $RDS_ENDPOINT"
    export RDS_ENDPOINT
}

# Create S3 bucket for static web hosting
create_s3_web() {
    log_info "Creating S3 bucket for static web hosting..."

    BUCKET_NAME="${PREFIX}-web-${ACCOUNT_ID}"

    if ! aws s3api head-bucket --bucket $BUCKET_NAME --region $REGION 2>/dev/null; then
        aws s3api create-bucket \
            --bucket $BUCKET_NAME \
            --region $REGION \
            --create-bucket-configuration LocationConstraint=$REGION

        # Enable static website hosting
        aws s3 website s3://$BUCKET_NAME --index-document index.html --error-document index.html

        # Disable block public access
        aws s3api put-public-access-block \
            --bucket $BUCKET_NAME \
            --public-access-block-configuration "BlockPublicAcls=false,IgnorePublicAcls=false,BlockPublicPolicy=false,RestrictPublicBuckets=false" \
            --region $REGION

        # Set bucket policy for public access (inline JSON to avoid Windows path issues)
        BUCKET_POLICY='{
            "Version": "2012-10-17",
            "Statement": [
                {
                    "Sid": "PublicReadGetObject",
                    "Effect": "Allow",
                    "Principal": "*",
                    "Action": "s3:GetObject",
                    "Resource": "arn:aws:s3:::'${BUCKET_NAME}'/*"
                }
            ]
        }'

        aws s3api put-bucket-policy --bucket $BUCKET_NAME --policy "$BUCKET_POLICY" --region $REGION

        # Add tags
        aws s3api put-bucket-tagging --bucket $BUCKET_NAME --tagging "TagSet=[{Key=Project,Value=${PROJECT_TAG}}]" --region $REGION

        log_info "Created S3 bucket: $BUCKET_NAME"
    else
        log_info "S3 bucket already exists: $BUCKET_NAME"
    fi

    WEB_URL="http://${BUCKET_NAME}.s3-website-${REGION}.amazonaws.com"

    # Get CloudFront URL if distribution exists
    CF_DOMAIN=$(aws cloudfront list-distributions --query "DistributionList.Items[?contains(Origins.Items[0].DomainName, '${BUCKET_NAME}')].DomainName | [0]" --output text 2>/dev/null || echo "")
    if [ -n "$CF_DOMAIN" ] && [ "$CF_DOMAIN" != "None" ]; then
        CF_URL="https://${CF_DOMAIN}"
        log_info "CloudFront URL: $CF_URL"
    else
        CF_URL=""
    fi

    export BUCKET_NAME WEB_URL CF_URL
}

# Create CloudWatch Log Groups
create_cloudwatch_logs() {
    log_info "Creating CloudWatch Log Groups..."

    log_info "ECS Express API logs are managed under ${API_EXPRESS_LOG_GROUP}"

    for LOG_NAME in "api" "jobs" "ingestion" "feed" "x-ingestion" "reindex" "sync-index" "otel-collector" "local-jobs" "windows-host" "cloudwatch-agent"; do
        LOG_GROUP="/crs/$LOG_NAME"
        EXISTING=$(aws logs describe-log-groups --log-group-name-prefix "$LOG_GROUP" --region $REGION --query "logGroups[?logGroupName=='$LOG_GROUP'].logGroupName" --output text 2>/dev/null || echo "")
        if [ -z "$EXISTING" ]; then
            aws logs create-log-group --log-group-name "$LOG_GROUP" --tags Project=${PROJECT_TAG} --region $REGION
            aws logs put-retention-policy --log-group-name "$LOG_GROUP" --retention-in-days $LOG_RETENTION_DAYS --region $REGION
            log_info "Created log group: $LOG_GROUP"
        else
            aws logs put-retention-policy --log-group-name "$LOG_GROUP" --retention-in-days $LOG_RETENTION_DAYS --region $REGION
            log_info "Log group already exists: $LOG_GROUP"
        fi
    done
}

create_rum_app_monitor() {
    log_info "Configuring CloudWatch RUM app monitor..."

    local existing_monitor
    existing_monitor=$(aws rum get-app-monitor --name "$RUM_APP_MONITOR_NAME" --region $REGION --output json 2>/dev/null || echo "")

    if [ -z "$existing_monitor" ] && [ -z "$RUM_IDENTITY_POOL_ID" ]; then
        log_warn "Skipping CloudWatch RUM creation because RUM_IDENTITY_POOL_ID is not set and app monitor ${RUM_APP_MONITOR_NAME} does not exist."
        return
    fi

    local web_domain="${WEB_URL#http://}"
    local cf_domain="${CF_URL#https://}"
    local monitor_config_file
    monitor_config_file=$(mktemp)
    local monitor_config_file_native
    monitor_config_file_native=$(to_native_path "$monitor_config_file")

    python - "$monitor_config_file_native" "$RUM_IDENTITY_POOL_ID" "$RUM_GUEST_ROLE_ARN" "$RUM_SESSION_SAMPLE_RATE" "$RUM_ALLOW_COOKIES" "$RUM_ENABLE_XRAY" <<'PY'
import json
import sys

output_path, identity_pool_id, guest_role_arn, sample_rate, allow_cookies, enable_xray = sys.argv[1:]
payload = {
    "IdentityPoolId": identity_pool_id,
    "SessionSampleRate": float(sample_rate),
    "AllowCookies": allow_cookies.lower() == "true",
    "Telemetries": ["errors", "performance", "http"],
    "EnableXRay": enable_xray.lower() == "true"
}

if guest_role_arn:
    payload["GuestRoleArn"] = guest_role_arn

with open(output_path, "w", encoding="utf-8") as handle:
    json.dump(payload, handle)
PY

    local rum_log_flag="--no-cw-log-enabled"
    if [ "$RUM_CW_LOGS_ENABLED" = "true" ]; then
        rum_log_flag="--cw-log-enabled"
    fi

    local domain_args=(--domain-list "$web_domain")
    if [ -n "$cf_domain" ]; then
        domain_args=(--domain-list "$web_domain" "$cf_domain")
    fi

    if [ -z "$existing_monitor" ]; then
        aws rum create-app-monitor \
            --name "$RUM_APP_MONITOR_NAME" \
            "${domain_args[@]}" \
            --app-monitor-configuration "file://${monitor_config_file}" \
            $rum_log_flag \
            --deobfuscation-configuration "JavaScriptSourceMaps={Status=ENABLED,S3Uri=s3://${BUCKET_NAME}/${RUM_SOURCE_MAPS_PREFIX}/}" \
            --custom-events Status=DISABLED \
            --platform Web \
            --tags Project=${PROJECT_TAG} \
            --region $REGION > /dev/null
        log_info "Created CloudWatch RUM app monitor: ${RUM_APP_MONITOR_NAME}"
    elif [ -n "$RUM_IDENTITY_POOL_ID" ]; then
        aws rum update-app-monitor \
            --name "$RUM_APP_MONITOR_NAME" \
            "${domain_args[@]}" \
            --app-monitor-configuration "file://${monitor_config_file}" \
            $rum_log_flag \
            --deobfuscation-configuration "JavaScriptSourceMaps={Status=ENABLED,S3Uri=s3://${BUCKET_NAME}/${RUM_SOURCE_MAPS_PREFIX}/}" \
            --custom-events Status=DISABLED \
            --region $REGION > /dev/null
        log_info "Updated CloudWatch RUM app monitor: ${RUM_APP_MONITOR_NAME}"
    else
        log_info "Reusing existing CloudWatch RUM app monitor: ${RUM_APP_MONITOR_NAME}"
    fi

    rm -f "$monitor_config_file"

    local rum_description_file
    rum_description_file=$(mktemp)
    aws rum get-app-monitor --name "$RUM_APP_MONITOR_NAME" --region $REGION > "$rum_description_file"
    local rum_description_file_native
    rum_description_file_native=$(to_native_path "$rum_description_file")
    local rum_log_group
    rum_log_group=$(python - "$rum_description_file_native" <<'PY'
import json
import sys
from pathlib import Path

payload = json.loads(Path(sys.argv[1]).read_text())
print(payload.get("AppMonitor", {}).get("DataStorage", {}).get("CwLog", {}).get("CwLogGroup", ""))
PY
)

    if [ -n "$rum_log_group" ]; then
        aws logs put-retention-policy --log-group-name "$rum_log_group" --retention-in-days $LOG_RETENTION_DAYS --region $REGION
    fi

    rm -f "$rum_description_file"
}

# Create IAM roles
create_iam_roles() {
    log_info "Creating IAM roles..."

    # ECS Task Execution Role
    if ! aws iam get-role --role-name ${PREFIX}-ecs-execution-role &> /dev/null; then
        aws iam create-role \
            --role-name ${PREFIX}-ecs-execution-role \
            --assume-role-policy-document "$(cat "${SCRIPT_DIR}/iam/ecs-trust-policy.json")" \
            --tags Key=Project,Value=${PROJECT_TAG}

        log_info "Created ECS Execution role: ${PREFIX}-ecs-execution-role"
    fi

    ensure_managed_role_policy "${PREFIX}-ecs-execution-role" "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
    ensure_inline_role_policy "${PREFIX}-ecs-execution-role" "${PREFIX}-ecs-secrets-policy" "${SCRIPT_DIR}/iam/ecs-secrets-policy.json"

    # ECS Task Role
    if ! aws iam get-role --role-name ${PREFIX}-ecs-task-role &> /dev/null; then
        aws iam create-role \
            --role-name ${PREFIX}-ecs-task-role \
            --assume-role-policy-document "$(cat "${SCRIPT_DIR}/iam/ecs-trust-policy.json")" \
            --tags Key=Project,Value=${PROJECT_TAG}

        log_info "Created ECS Task role: ${PREFIX}-ecs-task-role"
    fi

    ensure_inline_role_policy "${PREFIX}-ecs-task-role" "${PREFIX}-task-policy" "${SCRIPT_DIR}/iam/app-policy.json"

    # ECS Express infrastructure role
    if [ "${DEPLOY_ECS_EXPRESS,,}" = "true" ] && ! aws iam get-role --role-name ${PREFIX}-ecs-express-infra-role &> /dev/null; then
        aws iam create-role \
            --role-name ${PREFIX}-ecs-express-infra-role \
            --assume-role-policy-document "$(cat "${SCRIPT_DIR}/iam/ecs-express-infra-trust-policy.json")" \
            --tags Key=Project,Value=${PROJECT_TAG}

        log_info "Created ECS Express infrastructure role: ${PREFIX}-ecs-express-infra-role"
    fi

    if [ "${DEPLOY_ECS_EXPRESS,,}" = "true" ]; then
        ensure_managed_role_policy "${PREFIX}-ecs-express-infra-role" "arn:aws:iam::aws:policy/service-role/AmazonECSInfrastructureRoleforExpressGatewayServices"
    fi

    # EventBridge role for ECS
    if ! aws iam get-role --role-name ${PREFIX}-eventbridge-role &> /dev/null; then
        aws iam create-role \
            --role-name ${PREFIX}-eventbridge-role \
            --assume-role-policy-document "$(cat "${SCRIPT_DIR}/iam/eventbridge-trust-policy.json")" \
            --tags Key=Project,Value=${PROJECT_TAG}

        log_info "Created EventBridge role: ${PREFIX}-eventbridge-role"
    fi

    ensure_inline_role_policy "${PREFIX}-eventbridge-role" "${PREFIX}-eventbridge-ecs-policy" "${SCRIPT_DIR}/iam/eventbridge-ecs-policy.json"
}

# Create ECS Cluster
create_ecs_cluster() {
    log_info "Creating ECS Cluster..."

    if ! aws ecs describe-clusters --clusters ${PREFIX}-cluster --region $REGION --query 'clusters[0].status' --output text 2>/dev/null | grep -q ACTIVE; then
        aws ecs create-cluster \
            --cluster-name ${PREFIX}-cluster \
            --capacity-providers FARGATE FARGATE_SPOT \
            --default-capacity-provider-strategy capacityProvider=FARGATE,weight=1 \
            --tags key=Project,value=${PROJECT_TAG} \
            --region $REGION
        log_info "Created ECS cluster: ${PREFIX}-cluster"
    else
        log_info "ECS cluster already exists: ${PREFIX}-cluster"
    fi
}

create_service_discovery_namespace() {
    log_info "Creating private service discovery namespace..."

    OTEL_NAMESPACE_ID=$(aws servicediscovery list-namespaces \
        --query "Namespaces[?Name=='${OTEL_COLLECTOR_NAMESPACE_NAME}'].Id | [0]" \
        --output text \
        --region "$REGION" 2>/dev/null || echo "")

    if [ -z "$OTEL_NAMESPACE_ID" ] || [ "$OTEL_NAMESPACE_ID" = "None" ]; then
        local operation_id
        operation_id=$(aws servicediscovery create-private-dns-namespace \
            --name "${OTEL_COLLECTOR_NAMESPACE_NAME}" \
            --vpc "$VPC_ID" \
            --creator-request-id "${PREFIX}-otel-namespace" \
            --tags Key=Project,Value=${PROJECT_TAG} \
            --region "$REGION" \
            --query 'OperationId' \
            --output text)

        log_info "Waiting for Cloud Map namespace creation..."
        while true; do
            local status
            status=$(aws servicediscovery get-operation \
                --operation-id "$operation_id" \
                --region "$REGION" \
                --query 'Operation.Status' \
                --output text)

            if [ "$status" = "SUCCESS" ]; then
                break
            fi

            if [ "$status" = "FAIL" ]; then
                log_error "Cloud Map namespace creation failed"
                exit 1
            fi

            echo -n "."
            sleep 5
        done
        echo ""

        OTEL_NAMESPACE_ID=$(aws servicediscovery get-operation \
            --operation-id "$operation_id" \
            --region "$REGION" \
            --query 'Operation.Targets.NAMESPACE' \
            --output text)

        log_info "Created private namespace: ${OTEL_COLLECTOR_NAMESPACE_NAME}"
    else
        log_info "Private namespace already exists: ${OTEL_COLLECTOR_NAMESPACE_NAME}"
    fi

    OTEL_DISCOVERY_SERVICE_ARN=$(aws servicediscovery list-services \
        --query "Services[?Name=='${OTEL_COLLECTOR_DISCOVERY_SERVICE_NAME}'].Arn | [0]" \
        --output text \
        --region "$REGION" 2>/dev/null || echo "")

    if [ -z "$OTEL_DISCOVERY_SERVICE_ARN" ] || [ "$OTEL_DISCOVERY_SERVICE_ARN" = "None" ]; then
        OTEL_DISCOVERY_SERVICE_ARN=$(aws servicediscovery create-service \
            --name "${OTEL_COLLECTOR_DISCOVERY_SERVICE_NAME}" \
            --namespace-id "$OTEL_NAMESPACE_ID" \
            --dns-config "RoutingPolicy=MULTIVALUE,DnsRecords=[{Type=A,TTL=10}]" \
            --health-check-custom-config FailureThreshold=1 \
            --tags Key=Project,Value=${PROJECT_TAG} \
            --region "$REGION" \
            --query 'Service.Arn' \
            --output text)

        log_info "Created service discovery service: ${OTEL_COLLECTOR_DISCOVERY_SERVICE_NAME}.${OTEL_COLLECTOR_NAMESPACE_NAME}"
    else
        log_info "Service discovery service already exists: ${OTEL_COLLECTOR_DISCOVERY_SERVICE_NAME}.${OTEL_COLLECTOR_NAMESPACE_NAME}"
    fi

    export OTEL_NAMESPACE_ID OTEL_DISCOVERY_SERVICE_ARN
}

register_otel_collector_task_definition() {
    log_info "Registering OTEL collector task definition..."

    local collector_task_definition_file
    collector_task_definition_file=$(mktemp)
    local collector_task_definition_file_native
    collector_task_definition_file_native=$(to_native_path "$collector_task_definition_file")
    local adot_collector_config
    adot_collector_config='receivers:\n  otlp:\n    protocols:\n      grpc:\n        endpoint: 0.0.0.0:4317\nprocessors:\n  batch:\nexporters:\n  awsxray:\nservice:\n  pipelines:\n    traces:\n      receivers: [otlp]\n      processors: [batch]\n      exporters: [awsxray]'

    cat > "$collector_task_definition_file" <<EOF
[
  {
    "name": "${OTEL_COLLECTOR_CONTAINER_NAME}",
    "image": "${ADOT_COLLECTOR_IMAGE}",
    "essential": true,
    "portMappings": [
      {
        "containerPort": 4317,
        "protocol": "tcp"
      }
    ],
    "environment": [
      { "name": "AOT_CONFIG_CONTENT", "value": "${adot_collector_config}" }
    ],
    "logConfiguration": {
      "logDriver": "awslogs",
      "options": {
        "awslogs-group": "/crs/otel-collector",
        "awslogs-region": "${REGION}",
        "awslogs-stream-prefix": "service"
      }
    }
  }
]
EOF

    OTEL_COLLECTOR_TASK_DEFINITION_ARN=$(aws ecs register-task-definition \
        --family "${OTEL_COLLECTOR_TASK_FAMILY}" \
        --network-mode "awsvpc" \
        --requires-compatibilities "FARGATE" \
        --cpu "256" \
        --memory "512" \
        --execution-role-arn "arn:aws:iam::${ACCOUNT_ID}:role/${PREFIX}-ecs-execution-role" \
        --task-role-arn "arn:aws:iam::${ACCOUNT_ID}:role/${PREFIX}-ecs-task-role" \
        --container-definitions "file://${collector_task_definition_file_native}" \
        --query 'taskDefinition.taskDefinitionArn' \
        --output text \
        --region "$REGION")

    rm -f "$collector_task_definition_file"
    export OTEL_COLLECTOR_TASK_DEFINITION_ARN
}

create_otel_collector_service() {
    log_info "Creating OTEL collector ECS service..."

    local collector_service_arn
    collector_service_arn=$(aws ecs list-services \
        --cluster "$API_CLUSTER_NAME" \
        --region "$REGION" \
        --query "serviceArns[?contains(@, '/${OTEL_COLLECTOR_SERVICE_NAME}')] | [0]" \
        --output text 2>/dev/null || echo "")

    if [ -z "$collector_service_arn" ] || [ "$collector_service_arn" = "None" ]; then
        aws ecs create-service \
            --cluster "$API_CLUSTER_NAME" \
            --service-name "${OTEL_COLLECTOR_SERVICE_NAME}" \
            --task-definition "$OTEL_COLLECTOR_TASK_DEFINITION_ARN" \
            --launch-type FARGATE \
            --desired-count 1 \
            --network-configuration "awsvpcConfiguration={subnets=[${SUBNET_1_ID},${SUBNET_2_ID}],securityGroups=[${OTEL_COLLECTOR_SG_ID}],assignPublicIp=ENABLED}" \
            --service-registries "registryArn=${OTEL_DISCOVERY_SERVICE_ARN}" \
            --tags key=Project,value=${PROJECT_TAG} \
            --region "$REGION" > /dev/null

        log_info "Created OTEL collector service: ${OTEL_COLLECTOR_SERVICE_NAME}"
    else
        aws ecs update-service \
            --cluster "$API_CLUSTER_NAME" \
            --service "${OTEL_COLLECTOR_SERVICE_NAME}" \
            --task-definition "$OTEL_COLLECTOR_TASK_DEFINITION_ARN" \
            --desired-count 1 \
            --force-new-deployment \
            --region "$REGION" > /dev/null

        log_info "Updated OTEL collector service: ${OTEL_COLLECTOR_SERVICE_NAME}"
    fi
}

create_ecs_express_api_service() {
    if [ "${DEPLOY_ECS_EXPRESS,,}" != "true" ]; then
        log_warn "Skipping ECS Express API deployment (DEPLOY_ECS_EXPRESS is not true)"
        return
    fi

    log_info "Creating ECS Express API service..."

    local primary_container_json
    primary_container_json=$(build_api_primary_container_json "$API_IMAGE_IDENTIFIER")
    local service_arn
    service_arn=$(resolve_ecs_express_service_arn)

    if [ -z "$service_arn" ] || [ "$service_arn" = "None" ]; then
        service_arn=$(aws ecs create-express-gateway-service \
            --cluster "$API_CLUSTER_NAME" \
            --service-name "${API_EXPRESS_SERVICE_NAME}" \
            --execution-role-arn "arn:aws:iam::${ACCOUNT_ID}:role/${PREFIX}-ecs-execution-role" \
            --infrastructure-role-arn "arn:aws:iam::${ACCOUNT_ID}:role/${PREFIX}-ecs-express-infra-role" \
            --task-role-arn "arn:aws:iam::${ACCOUNT_ID}:role/${PREFIX}-ecs-task-role" \
            --primary-container "$primary_container_json" \
            --health-check-path "/health/ready" \
            --network-configuration "{\"subnets\":[\"${SUBNET_1_ID}\",\"${SUBNET_2_ID}\"],\"securityGroups\":[\"${API_EXPRESS_SG_ID}\"]}" \
            --cpu "256" \
            --memory "512" \
            --scaling-target '{"minTaskCount":1,"maxTaskCount":25,"autoScalingMetric":"AVERAGE_CPU","autoScalingTargetValue":60}' \
            --tags key=Project,value=${PROJECT_TAG} \
            --region "$REGION" \
            --query 'service.serviceArn' \
            --output text)

        log_info "Creating ECS Express service (this takes 3-5 minutes)..."
    else
        aws ecs update-express-gateway-service \
            --service-arn "$service_arn" \
            --execution-role-arn "arn:aws:iam::${ACCOUNT_ID}:role/${PREFIX}-ecs-execution-role" \
            --task-role-arn "arn:aws:iam::${ACCOUNT_ID}:role/${PREFIX}-ecs-task-role" \
            --primary-container "$primary_container_json" \
            --health-check-path "/health/ready" \
            --network-configuration "{\"subnets\":[\"${SUBNET_1_ID}\",\"${SUBNET_2_ID}\"],\"securityGroups\":[\"${API_EXPRESS_SG_ID}\"]}" \
            --cpu "256" \
            --memory "512" \
            --scaling-target '{"minTaskCount":1,"maxTaskCount":25,"autoScalingMetric":"AVERAGE_CPU","autoScalingTargetValue":60}' \
            --region "$REGION" > /dev/null

        log_info "Updating ECS Express API service configuration..."
    fi

    if ! wait_for_ecs_express_status "$service_arn" "ACTIVE" 60; then
        log_error "Timed out waiting for ECS Express API service to become ACTIVE"
        exit 1
    fi
    echo ""

    ECS_EXPRESS_SERVICE_ARN="$service_arn"
    ECS_EXPRESS_API_URL=$(resolve_ecs_express_endpoint "$service_arn")
    log_info "ECS Express API is active: ${ECS_EXPRESS_API_URL}"

    export ECS_EXPRESS_SERVICE_ARN ECS_EXPRESS_API_URL
}

# Create OpenSearch Serverless Collection
create_opensearch() {
    if [ "$ENABLE_OPENSEARCH" != "true" ]; then
        log_warn "Skipping OpenSearch creation (ENABLE_OPENSEARCH is not true)"
        if [ -z "$OPENSEARCH_ENDPOINT" ]; then
            log_warn "OPENSEARCH_ENDPOINT is empty; ingestion/feed tasks will fail if run."
        fi
        export OPENSEARCH_ENDPOINT
        return
    fi

    log_info "Creating OpenSearch Serverless collection..."

    # Check if collection exists
    COLLECTION_ID=$(aws opensearchserverless list-collections --region $REGION --query "collectionSummaries[?name=='${PREFIX}-search'].id" --output text 2>/dev/null || echo "")

    if [ -z "$COLLECTION_ID" ]; then
        # Create encryption policy first (required) - use inline JSON
        ENCRYPTION_POLICY='{"Rules":[{"ResourceType":"collection","Resource":["collection/'${PREFIX}'-search"]}],"AWSOwnedKey":true}'

        aws opensearchserverless create-security-policy \
            --name ${PREFIX}-encryption-policy \
            --type encryption \
            --policy "$ENCRYPTION_POLICY" \
            --region $REGION 2>/dev/null || true

        # Create network policy (public access for simplicity)
        NETWORK_POLICY='[{"Rules":[{"ResourceType":"collection","Resource":["collection/'${PREFIX}'-search"]},{"ResourceType":"dashboard","Resource":["collection/'${PREFIX}'-search"]}],"AllowFromPublic":true}]'

        aws opensearchserverless create-security-policy \
            --name ${PREFIX}-network-policy \
            --type network \
            --policy "$NETWORK_POLICY" \
            --region $REGION 2>/dev/null || true

        # Create data access policy
        DATA_ACCESS_POLICY='[{"Rules":[{"ResourceType":"collection","Resource":["collection/'${PREFIX}'-search"],"Permission":["aoss:*"]},{"ResourceType":"index","Resource":["index/'${PREFIX}'-search/*"],"Permission":["aoss:*"]}],"Principal":["arn:aws:iam::'${ACCOUNT_ID}':role/'${PREFIX}'-ecs-task-role","arn:aws:iam::'${ACCOUNT_ID}':root"]}]'

        aws opensearchserverless create-access-policy \
            --name ${PREFIX}-data-access-policy \
            --type data \
            --policy "$DATA_ACCESS_POLICY" \
            --region $REGION 2>/dev/null || true

        # Wait a moment for policies to propagate
        sleep 5

        # Create the collection
        COLLECTION_ID=$(aws opensearchserverless create-collection \
            --name ${PREFIX}-search \
            --type VECTORSEARCH \
            --tags key=Project,value=${PROJECT_TAG} \
            --region $REGION \
            --query 'createCollectionDetail.id' \
            --output text)

        log_info "Created OpenSearch collection: ${PREFIX}-search (ID: $COLLECTION_ID)"
        log_info "Waiting for OpenSearch collection to be active (this takes 2-5 minutes)..."

        # Wait for collection to be active
        while true; do
            STATUS=$(aws opensearchserverless list-collections --region $REGION --query "collectionSummaries[?name=='${PREFIX}-search'].status" --output text)
            if [ "$STATUS" = "ACTIVE" ]; then
                break
            fi
            echo -n "."
            sleep 10
        done
        echo ""
        log_info "OpenSearch collection is now active!"
    else
        log_info "OpenSearch collection already exists: ${PREFIX}-search"
    fi

    # Get collection endpoint
    OPENSEARCH_ENDPOINT=$(aws opensearchserverless list-collections --region $REGION --query "collectionSummaries[?name=='${PREFIX}-search'].collectionEndpoint" --output text)
    log_info "OpenSearch Endpoint: $OPENSEARCH_ENDPOINT"

    # Update secrets file with OpenSearch endpoint
    if [ -f "secrets.env" ]; then
        sed -i "s|OPENSEARCH_ENDPOINT=.*|OPENSEARCH_ENDPOINT=$OPENSEARCH_ENDPOINT|" secrets.env 2>/dev/null || \
        sed -i '' "s|OPENSEARCH_ENDPOINT=.*|OPENSEARCH_ENDPOINT=$OPENSEARCH_ENDPOINT|" secrets.env
    elif [ -f "infrastructure/aws/secrets.env" ]; then
        sed -i "s|OPENSEARCH_ENDPOINT=.*|OPENSEARCH_ENDPOINT=$OPENSEARCH_ENDPOINT|" infrastructure/aws/secrets.env 2>/dev/null || \
        sed -i '' "s|OPENSEARCH_ENDPOINT=.*|OPENSEARCH_ENDPOINT=$OPENSEARCH_ENDPOINT|" infrastructure/aws/secrets.env
    fi

    export OPENSEARCH_ENDPOINT
}

# Register ECS Task Definitions
register_task_definitions() {
    log_info "Registering ECS task definitions..."

    # Build connection string
    CONNECTION_STRING="Host=${RDS_ENDPOINT};Database=crsdb;Username=${DB_USERNAME};Password=${DB_PASSWORD}"
    ADOT_COLLECTOR_CONFIG='receivers:\n  otlp:\n    protocols:\n      grpc:\n      http:\nprocessors:\n  batch:\nexporters:\n  awsxray:\nservice:\n  pipelines:\n    traces:\n      receivers: [otlp]\n      processors: [batch]\n      exporters: [awsxray]'

    # Register task definitions using inline JSON
    JOB_TASKS=()
    if [ "$ENABLE_OPENSEARCH" = "true" ]; then
        JOB_TASKS=("ingestion" "feed")
    fi

    for TASK in "${JOB_TASKS[@]}"; do
        TASK_DEFINITION_FILE=$(mktemp)
        TASK_DEFINITION_FILE_NATIVE=$(to_native_path "$TASK_DEFINITION_FILE")
        cat > "$TASK_DEFINITION_FILE" <<EOF
[
  {
    "name": "${PREFIX}-${TASK}",
    "image": "${ECR_URI}/crs-jobs:latest",
    "essential": true,
    "command": ["${TASK}"],
    "dependsOn": [
      {
        "containerName": "aws-otel-collector",
        "condition": "START"
      }
    ],
    "environment": [
      {"name": "ASPNETCORE_ENVIRONMENT", "value": "Production"},
      {"name": "ConnectionStrings__DefaultConnection", "value": "${CONNECTION_STRING}"},
      {"name": "Embedding__ModelName", "value": "text-embedding-3-small"},
      {"name": "Embedding__Dimensions", "value": "1536"},
      {"name": "OpenSearch__Endpoint", "value": "${OPENSEARCH_ENDPOINT}"},
      {"name": "OpenSearch__IndexName", "value": "crs-content"},
      {"name": "OpenSearch__EmbeddingDimensions", "value": "1536"},
      {"name": "OpenAI__ApiKey", "value": "${OpenAI__ApiKey}"},
      {"name": "OpenAI__Model", "value": "gpt-5-nano"},
      {"name": "OpenAI__MaxTokens", "value": "16384"},
      {"name": "Observability__Environment", "value": "${ENVIRONMENT}"},
      {"name": "Observability__ExecutionEnvironment", "value": "aws"},
      {"name": "Observability__ServiceName", "value": "${JOBS_SERVICE_NAME}"},
      {"name": "Observability__ServiceNamespace", "value": "crs"},
      {"name": "Observability__MetricsNamespace", "value": "${METRICS_NAMESPACE}"},
      {"name": "Observability__TraceSampleRatio", "value": "${TRACE_SAMPLE_RATIO}"},
      {"name": "Observability__EnableSensitiveBodyLogging", "value": "false"},
      {"name": "OTEL_EXPORTER_OTLP_ENDPOINT", "value": "http://127.0.0.1:4317"},
      {"name": "OTEL_EXPORTER_OTLP_PROTOCOL", "value": "grpc"},
      {"name": "OTEL_METRICS_EXPORTER", "value": "none"},
      {"name": "OTEL_LOGS_EXPORTER", "value": "none"},
      {"name": "OTEL_PROPAGATORS", "value": "xray"},
      {"name": "X__ClientId", "value": "${X__ClientId}"},
      {"name": "X__ClientSecret", "value": "${X__ClientSecret}"},
      {"name": "X__RedirectUri", "value": "${X__RedirectUri}"}
    ],
    "logConfiguration": {
      "logDriver": "awslogs",
      "options": {
        "awslogs-group": "/crs/${TASK}",
        "awslogs-region": "${REGION}",
        "awslogs-stream-prefix": "ecs"
      }
    }
  },
  {
    "name": "aws-otel-collector",
    "image": "${ADOT_COLLECTOR_IMAGE}",
    "essential": false,
    "environment": [
      {"name": "AOT_CONFIG_CONTENT", "value": "${ADOT_COLLECTOR_CONFIG}"}
    ],
    "logConfiguration": {
      "logDriver": "awslogs",
      "options": {
        "awslogs-group": "/crs/otel-collector",
        "awslogs-region": "${REGION}",
        "awslogs-stream-prefix": "${TASK}"
      }
    }
  }
]
EOF

        aws ecs register-task-definition \
            --family "${PREFIX}-${TASK}-task" \
            --network-mode "awsvpc" \
            --requires-compatibilities "FARGATE" \
            --cpu "512" \
            --memory "1024" \
            --execution-role-arn "arn:aws:iam::${ACCOUNT_ID}:role/${PREFIX}-ecs-execution-role" \
            --task-role-arn "arn:aws:iam::${ACCOUNT_ID}:role/${PREFIX}-ecs-task-role" \
            --container-definitions "file://${TASK_DEFINITION_FILE_NATIVE}" \
            --region $REGION > /dev/null

        rm -f "$TASK_DEFINITION_FILE"

        log_info "Registered task definition: ${PREFIX}-${TASK}-task"
    done
}

create_cloudwatch_dashboards() {
    log_info "Creating CloudWatch dashboards..."

    API_DASHBOARD_BODY=$(cat <<EOF
{
  "widgets": [
    {
      "type": "metric",
      "x": 0,
      "y": 0,
      "width": 12,
      "height": 6,
      "properties": {
        "title": "API Request Volume",
        "region": "${REGION}",
        "view": "timeSeries",
        "stacked": false,
        "stat": "Sum",
        "period": 300,
        "metrics": [
          ["${METRICS_NAMESPACE}", "api.request.count", "Service", "${API_SERVICE_NAME}", "Environment", "${ENVIRONMENT}"]
        ]
      }
    },
    {
      "type": "metric",
      "x": 12,
      "y": 0,
      "width": 12,
      "height": 6,
      "properties": {
        "title": "API Latency p50/p95",
        "region": "${REGION}",
        "view": "timeSeries",
        "period": 300,
        "metrics": [
          ["${METRICS_NAMESPACE}", "api.request.duration", "Service", "${API_SERVICE_NAME}", "Environment", "${ENVIRONMENT}", {"stat": "p50", "label": "p50"}],
          [".", "api.request.duration", ".", ".", ".", ".", {"stat": "p95", "label": "p95"}]
        ]
      }
    },
    {
      "type": "metric",
      "x": 0,
      "y": 6,
      "width": 8,
      "height": 6,
      "properties": {
        "title": "API 5xx Count",
        "region": "${REGION}",
        "view": "timeSeries",
        "stat": "Sum",
        "period": 300,
        "metrics": [
          ["${METRICS_NAMESPACE}", "api.request.5xx.count", "Service", "${API_SERVICE_NAME}", "Environment", "${ENVIRONMENT}"]
        ]
      }
    },
    {
      "type": "metric",
      "x": 8,
      "y": 6,
      "width": 8,
      "height": 6,
      "properties": {
        "title": "Auth Failure Count",
        "region": "${REGION}",
        "view": "timeSeries",
        "stat": "Sum",
        "period": 300,
        "metrics": [
          ["${METRICS_NAMESPACE}", "auth.failure.count", "Service", "${API_SERVICE_NAME}", "Environment", "${ENVIRONMENT}"]
        ]
      }
    },
    {
      "type": "metric",
      "x": 16,
      "y": 6,
      "width": 8,
      "height": 6,
      "properties": {
        "title": "Rate Limit Rejections",
        "region": "${REGION}",
        "view": "timeSeries",
        "stat": "Sum",
        "period": 300,
        "metrics": [
          ["${METRICS_NAMESPACE}", "api.rate_limit.rejections", "Service", "${API_SERVICE_NAME}", "Environment", "${ENVIRONMENT}"]
        ]
      }
    },
    {
      "type": "metric",
      "x": 0,
      "y": 12,
      "width": 12,
      "height": 6,
      "properties": {
        "title": "Readiness Failures",
        "region": "${REGION}",
        "view": "timeSeries",
        "stat": "Sum",
        "period": 300,
        "metrics": [
          ["${METRICS_NAMESPACE}", "api.request.count", "Service", "${API_SERVICE_NAME}", "Environment", "${ENVIRONMENT}", "Operation", "/health/ready", "Outcome", "server_error"]
        ]
      }
    },
    {
      "type": "metric",
      "x": 12,
      "y": 12,
      "width": 12,
      "height": 6,
      "properties": {
        "title": "API Dependency Failures",
        "region": "${REGION}",
        "view": "timeSeries",
        "stat": "Sum",
        "period": 300,
        "metrics": [
          ["${METRICS_NAMESPACE}", "dependency.failure.count", "Service", "${API_SERVICE_NAME}", "Environment", "${ENVIRONMENT}", "Dependency", "openai", {"label": "OpenAI"}],
          [".", "dependency.failure.count", ".", ".", ".", ".", "Dependency", "opensearch", {"label": "OpenSearch"}],
          [".", "dependency.failure.count", ".", ".", ".", ".", "Dependency", "x", {"label": "X"}]
        ]
      }
    }
  ]
}
EOF
)

JOBS_DASHBOARD_BODY=$(cat <<EOF
{
  "widgets": [
    {
      "type": "metric",
      "x": 0,
      "y": 0,
      "width": 12,
      "height": 6,
      "properties": {
        "title": "Job Success / Failure Counts",
        "region": "${REGION}",
        "view": "timeSeries",
        "stat": "Sum",
        "period": 3600,
        "metrics": [
          ["${METRICS_NAMESPACE}", "job.success.count", "Service", "${JOBS_SERVICE_NAME}", "Environment", "${ENVIRONMENT}"],
          [".", "job.failure.count", ".", ".", ".", ".", {"label": "Failures"}]
        ]
      }
    },
    {
      "type": "metric",
      "x": 12,
      "y": 0,
      "width": 12,
      "height": 6,
      "properties": {
        "title": "Ingestion / Feed Job Duration",
        "region": "${REGION}",
        "view": "timeSeries",
        "period": 3600,
        "metrics": [
          ["${METRICS_NAMESPACE}", "job.duration", "Service", "${JOBS_SERVICE_NAME}", "Environment", "${ENVIRONMENT}", "JobName", "ingestion", {"stat": "Maximum", "label": "Ingestion"}],
          [".", "job.duration", ".", ".", ".", ".", "JobName", "feed", {"stat": "Maximum", "label": "Feed"}]
        ]
      }
    },
    {
      "type": "metric",
      "x": 0,
      "y": 6,
      "width": 8,
      "height": 6,
      "properties": {
        "title": "Items Saved / Indexed",
        "region": "${REGION}",
        "view": "timeSeries",
        "stat": "Sum",
        "period": 3600,
        "metrics": [
          ["${METRICS_NAMESPACE}", "ingestion.items.saved", "Service", "${JOBS_SERVICE_NAME}", "Environment", "${ENVIRONMENT}"],
          [".", "ingestion.items.indexed", ".", ".", ".", ".", {"label": "Indexed"}]
        ]
      }
    },
    {
      "type": "metric",
      "x": 8,
      "y": 6,
      "width": 8,
      "height": 6,
      "properties": {
        "title": "Feed Generation Duration",
        "region": "${REGION}",
        "view": "timeSeries",
        "period": 3600,
        "metrics": [
          ["${METRICS_NAMESPACE}", "feed.generation.duration", "Service", "${JOBS_SERVICE_NAME}", "Environment", "${ENVIRONMENT}", {"stat": "p95"}]
        ]
      }
    },
    {
      "type": "metric",
      "x": 16,
      "y": 6,
      "width": 8,
      "height": 6,
      "properties": {
        "title": "Recommendations Duration",
        "region": "${REGION}",
        "view": "timeSeries",
        "period": 3600,
        "metrics": [
          ["${METRICS_NAMESPACE}", "recommendations.duration", "Service", "${JOBS_SERVICE_NAME}", "Environment", "${ENVIRONMENT}", {"stat": "p95"}]
        ]
      }
    },
    {
      "type": "metric",
      "x": 0,
      "y": 12,
      "width": 12,
      "height": 6,
      "properties": {
        "title": "Job Dependency Failures",
        "region": "${REGION}",
        "view": "timeSeries",
        "stat": "Sum",
        "period": 3600,
        "metrics": [
          ["${METRICS_NAMESPACE}", "dependency.failure.count", "Service", "${JOBS_SERVICE_NAME}", "Environment", "${ENVIRONMENT}", "Dependency", "openai", {"label": "OpenAI"}],
          [".", "dependency.failure.count", ".", ".", ".", ".", "Dependency", "opensearch", {"label": "OpenSearch"}],
          [".", "dependency.failure.count", ".", ".", ".", ".", "Dependency", "x", {"label": "X"}]
        ]
      }
    },
    {
      "type": "metric",
      "x": 12,
      "y": 12,
      "width": 12,
      "height": 6,
      "properties": {
        "title": "Vector Search Latency",
        "region": "${REGION}",
        "view": "timeSeries",
        "period": 3600,
        "metrics": [
          ["${METRICS_NAMESPACE}", "dependency.call.duration", "Service", "${JOBS_SERVICE_NAME}", "Environment", "${ENVIRONMENT}", "Dependency", "opensearch", {"stat": "p95"}]
        ]
      }
    }
  ]
}
EOF
)

    PLATFORM_DASHBOARD_BODY=$(cat <<EOF
{
  "widgets": [
    {
      "type": "metric",
      "x": 0,
      "y": 0,
      "width": 12,
      "height": 6,
      "properties": {
        "title": "API Throughput and Errors",
        "region": "${REGION}",
        "view": "timeSeries",
        "stat": "Sum",
        "period": 300,
        "metrics": [
          ["${METRICS_NAMESPACE}", "api.request.count", "Service", "${API_SERVICE_NAME}", "Environment", "${ENVIRONMENT}", {"label": "Requests"}],
          [".", "api.request.5xx.count", ".", ".", ".", ".", {"label": "5xx"}]
        ]
      }
    },
    {
      "type": "metric",
      "x": 12,
      "y": 0,
      "width": 12,
      "height": 6,
      "properties": {
        "title": "Local Job Wrapper Health",
        "region": "${REGION}",
        "view": "timeSeries",
        "stat": "Sum",
        "period": 3600,
        "metrics": [
          ["${METRICS_NAMESPACE}", "job.wrapper.success.count", "Service", "${JOBS_SERVICE_NAME}", "Environment", "${ENVIRONMENT}", "ExecutionEnvironment", "local", {"label": "Wrapper Success"}],
          [".", "job.wrapper.failure.count", ".", ".", ".", ".", ".", ".", {"label": "Wrapper Failure"}],
          [".", "job.host.heartbeat", ".", ".", ".", ".", ".", ".", {"label": "Heartbeat"}]
        ]
      }
    },
    {
      "type": "metric",
      "x": 0,
      "y": 6,
      "width": 12,
      "height": 6,
      "properties": {
        "title": "API Readiness and Latency",
        "region": "${REGION}",
        "view": "timeSeries",
        "period": 300,
        "metrics": [
          ["${METRICS_NAMESPACE}", "api.request.duration", "Service", "${API_SERVICE_NAME}", "Environment", "${ENVIRONMENT}", {"stat": "p95", "label": "API p95"}],
          [".", "api.request.count", ".", ".", ".", ".", "Operation", "/health/ready", "Outcome", "server_error", {"stat": "Sum", "label": "Readiness Failures"}]
        ]
      }
    },
    {
      "type": "metric",
      "x": 12,
      "y": 6,
      "width": 12,
      "height": 6,
      "properties": {
        "title": "Frontend Sessions and Page Views",
        "region": "${REGION}",
        "view": "timeSeries",
        "stat": "Sum",
        "period": 300,
        "metrics": [
          ["AWS/RUM", "SessionCount", "application_name", "${RUM_APP_MONITOR_NAME}", {"label": "Sessions"}],
          [".", "PageViewCount", ".", ".", {"label": "Page Views"}]
        ]
      }
    }
  ]
}
EOF
)

    FRONTEND_DASHBOARD_BODY=$(cat <<EOF
{
  "widgets": [
    {
      "type": "metric",
      "x": 0,
      "y": 0,
      "width": 12,
      "height": 6,
      "properties": {
        "title": "Frontend JavaScript and HTTP Errors",
        "region": "${REGION}",
        "view": "timeSeries",
        "stat": "Sum",
        "period": 300,
        "metrics": [
          ["AWS/RUM", "JsErrorCount", "application_name", "${RUM_APP_MONITOR_NAME}", {"label": "JS Errors"}],
          [".", "Http4xxCount", ".", ".", {"label": "HTTP 4xx"}],
          [".", "Http5xxCount", ".", ".", {"label": "HTTP 5xx"}]
        ]
      }
    },
    {
      "type": "metric",
      "x": 12,
      "y": 0,
      "width": 12,
      "height": 6,
      "properties": {
        "title": "Frontend Navigation Performance",
        "region": "${REGION}",
        "view": "timeSeries",
        "period": 300,
        "metrics": [
          ["AWS/RUM", "PerformanceNavigationDuration", "application_name", "${RUM_APP_MONITOR_NAME}", {"stat": "p50", "label": "Navigation p50"}],
          [".", "PerformanceNavigationDuration", ".", ".", {"stat": "p95", "label": "Navigation p95"}],
          [".", "WebVitalsLargestContentfulPaint", ".", ".", {"stat": "p95", "label": "LCP p95"}]
        ]
      }
    },
    {
      "type": "metric",
      "x": 0,
      "y": 6,
      "width": 12,
      "height": 6,
      "properties": {
        "title": "Dependency Failure Counts",
        "region": "${REGION}",
        "view": "timeSeries",
        "stat": "Sum",
        "period": 300,
        "metrics": [
          ["${METRICS_NAMESPACE}", "dependency.failure.count", "Service", "${API_SERVICE_NAME}", "Environment", "${ENVIRONMENT}", "Dependency", "openai", {"label": "API OpenAI"}],
          [".", "dependency.failure.count", "Service", "${API_SERVICE_NAME}", "Environment", "${ENVIRONMENT}", "Dependency", "opensearch", {"label": "API OpenSearch"}],
          [".", "dependency.failure.count", "Service", "${JOBS_SERVICE_NAME}", "Environment", "${ENVIRONMENT}", "Dependency", "openai", {"label": "Jobs OpenAI"}],
          [".", "dependency.failure.count", "Service", "${JOBS_SERVICE_NAME}", "Environment", "${ENVIRONMENT}", "Dependency", "x", {"label": "Jobs X"}]
        ]
      }
    }
  ]
}
EOF
)

    aws cloudwatch put-dashboard \
        --dashboard-name "${PREFIX}-platform-overview" \
        --dashboard-body "${PLATFORM_DASHBOARD_BODY}" \
        --region $REGION > /dev/null

    aws cloudwatch put-dashboard \
        --dashboard-name "${PREFIX}-api-observability" \
        --dashboard-body "${API_DASHBOARD_BODY}" \
        --region $REGION > /dev/null

    aws cloudwatch put-dashboard \
        --dashboard-name "${PREFIX}-jobs-observability" \
        --dashboard-body "${JOBS_DASHBOARD_BODY}" \
        --region $REGION > /dev/null

    aws cloudwatch put-dashboard \
        --dashboard-name "${PREFIX}-dependency-frontend-observability" \
        --dashboard-body "${FRONTEND_DASHBOARD_BODY}" \
        --region $REGION > /dev/null

    log_info "Created CloudWatch dashboards: ${PREFIX}-platform-overview, ${PREFIX}-api-observability, ${PREFIX}-jobs-observability, ${PREFIX}-dependency-frontend-observability"
}

put_metric_alarm() {
    local alarm_name="$1"
    local metric_name="$2"
    local statistic="$3"
    local threshold="$4"
    local comparison_operator="$5"
    local period="$6"
    local evaluation_periods="$7"
    shift 7

    aws cloudwatch put-metric-alarm \
        --alarm-name "$alarm_name" \
        --alarm-description "$alarm_name" \
        --namespace "$METRICS_NAMESPACE" \
        --metric-name "$metric_name" \
        --statistic "$statistic" \
        --threshold "$threshold" \
        --comparison-operator "$comparison_operator" \
        --period "$period" \
        --evaluation-periods "$evaluation_periods" \
        --treat-missing-data notBreaching \
        --dimensions "$@" \
        --region $REGION > /dev/null
}

put_extended_stat_alarm() {
    local alarm_name="$1"
    local metric_name="$2"
    local extended_statistic="$3"
    local threshold="$4"
    local comparison_operator="$5"
    local period="$6"
    local evaluation_periods="$7"
    shift 7

    aws cloudwatch put-metric-alarm \
        --alarm-name "$alarm_name" \
        --alarm-description "$alarm_name" \
        --namespace "$METRICS_NAMESPACE" \
        --metric-name "$metric_name" \
        --extended-statistic "$extended_statistic" \
        --threshold "$threshold" \
        --comparison-operator "$comparison_operator" \
        --period "$period" \
        --evaluation-periods "$evaluation_periods" \
        --treat-missing-data notBreaching \
        --dimensions "$@" \
        --region $REGION > /dev/null
}

create_cloudwatch_alarms() {
    log_info "Creating CloudWatch alarms..."

    put_metric_alarm \
        "${PREFIX}-api-5xx-spike" \
        "api.request.5xx.count" \
        "Sum" \
        "5" \
        "GreaterThanOrEqualToThreshold" \
        "300" \
        "1" \
        "Name=Service,Value=${API_SERVICE_NAME}" \
        "Name=Environment,Value=${ENVIRONMENT}"

    put_extended_stat_alarm \
        "${PREFIX}-api-p95-latency" \
        "api.request.duration" \
        "p95" \
        "1500" \
        "GreaterThanThreshold" \
        "300" \
        "2" \
        "Name=Service,Value=${API_SERVICE_NAME}" \
        "Name=Environment,Value=${ENVIRONMENT}"

    put_metric_alarm \
        "${PREFIX}-api-readiness-failures" \
        "api.request.count" \
        "Sum" \
        "3" \
        "GreaterThanOrEqualToThreshold" \
        "300" \
        "1" \
        "Name=Service,Value=${API_SERVICE_NAME}" \
        "Name=Environment,Value=${ENVIRONMENT}" \
        "Name=Operation,Value=/health/ready" \
        "Name=Outcome,Value=server_error"

    put_metric_alarm \
        "${PREFIX}-ingestion-job-failures" \
        "job.failure.count" \
        "Sum" \
        "1" \
        "GreaterThanOrEqualToThreshold" \
        "3600" \
        "1" \
        "Name=Service,Value=${JOBS_SERVICE_NAME}" \
        "Name=Environment,Value=${ENVIRONMENT}" \
        "Name=JobName,Value=ingestion"

    put_metric_alarm \
        "${PREFIX}-feed-job-failures" \
        "job.failure.count" \
        "Sum" \
        "1" \
        "GreaterThanOrEqualToThreshold" \
        "3600" \
        "1" \
        "Name=Service,Value=${JOBS_SERVICE_NAME}" \
        "Name=Environment,Value=${ENVIRONMENT}" \
        "Name=JobName,Value=feed"

    put_metric_alarm \
        "${PREFIX}-ingestion-duration-high" \
        "job.duration" \
        "Maximum" \
        "1800000" \
        "GreaterThanThreshold" \
        "3600" \
        "1" \
        "Name=Service,Value=${JOBS_SERVICE_NAME}" \
        "Name=Environment,Value=${ENVIRONMENT}" \
        "Name=JobName,Value=ingestion"

    for SERVICE_NAME in "${API_SERVICE_NAME}" "${JOBS_SERVICE_NAME}"; do
        for DEPENDENCY in "openai" "opensearch" "x"; do
            put_metric_alarm \
                "${PREFIX}-${SERVICE_NAME}-${DEPENDENCY}-dependency-failures" \
                "dependency.failure.count" \
                "Sum" \
                "3" \
                "GreaterThanOrEqualToThreshold" \
                "300" \
                "1" \
                "Name=Service,Value=${SERVICE_NAME}" \
                "Name=Environment,Value=${ENVIRONMENT}" \
                "Name=Dependency,Value=${DEPENDENCY}"
        done
    done

    for LOCAL_JOB_NAME in "ingestion" "x-ingestion" "feed"; do
        put_metric_alarm \
            "${PREFIX}-${LOCAL_JOB_NAME}-wrapper-failures" \
            "job.wrapper.failure.count" \
            "Sum" \
            "1" \
            "GreaterThanOrEqualToThreshold" \
            "3600" \
            "1" \
            "Name=Service,Value=${JOBS_SERVICE_NAME}" \
            "Name=Environment,Value=${ENVIRONMENT}" \
            "Name=ExecutionEnvironment,Value=local" \
            "Name=JobName,Value=${LOCAL_JOB_NAME}"

        aws cloudwatch put-metric-alarm \
            --alarm-name "${PREFIX}-${LOCAL_JOB_NAME}-missing-heartbeat" \
            --alarm-description "${PREFIX}-${LOCAL_JOB_NAME}-missing-heartbeat" \
            --namespace "$METRICS_NAMESPACE" \
            --metric-name "job.host.heartbeat" \
            --statistic "Sum" \
            --threshold "1" \
            --comparison-operator "LessThanThreshold" \
            --period "86400" \
            --evaluation-periods "1" \
            --treat-missing-data breaching \
            --dimensions \
                "Name=Service,Value=${JOBS_SERVICE_NAME}" \
                "Name=Environment,Value=${ENVIRONMENT}" \
                "Name=ExecutionEnvironment,Value=local" \
                "Name=JobName,Value=${LOCAL_JOB_NAME}" \
            --region $REGION > /dev/null
    done

    aws cloudwatch put-metric-alarm \
        --alarm-name "${PREFIX}-frontend-js-errors" \
        --alarm-description "${PREFIX}-frontend-js-errors" \
        --namespace "AWS/RUM" \
        --metric-name "JsErrorCount" \
        --statistic "Sum" \
        --threshold "5" \
        --comparison-operator "GreaterThanOrEqualToThreshold" \
        --period "300" \
        --evaluation-periods "1" \
        --treat-missing-data notBreaching \
        --dimensions "Name=application_name,Value=${RUM_APP_MONITOR_NAME}" \
        --region $REGION > /dev/null

    log_info "Created CloudWatch alarms for API, jobs, local scheduler heartbeats, dependencies, and frontend errors"
}

# Create CloudFront invalidation schedule (runs daily at 1 PM Pacific, after the 12 PM feed job)
create_cloudfront_invalidation_schedule() {
    log_info "Creating CloudFront cache invalidation schedule..."

    BUCKET_NAME="${PREFIX}-web-${ACCOUNT_ID}"
    DIST_ID=$(aws cloudfront list-distributions --query "DistributionList.Items[?contains(Origins.Items[0].DomainName, '${BUCKET_NAME}')].Id | [0]" --output text 2>/dev/null || echo "")

    if [ -z "$DIST_ID" ] || [ "$DIST_ID" = "None" ]; then
        log_warn "No CloudFront distribution found for ${BUCKET_NAME} - skipping invalidation schedule"
        return
    fi

    log_info "CloudFront distribution: $DIST_ID"

    # Create scheduler IAM role
    if ! aws iam get-role --role-name ${PREFIX}-scheduler-role &> /dev/null; then
        aws iam create-role \
            --role-name ${PREFIX}-scheduler-role \
            --assume-role-policy-document "$(cat "${SCRIPT_DIR}/iam/scheduler-trust-policy.json")" \
            --tags Key=Project,Value=${PROJECT_TAG}

        aws iam put-role-policy \
            --role-name ${PREFIX}-scheduler-role \
            --policy-name ${PREFIX}-scheduler-cloudfront-policy \
            --policy-document "$(cat "${SCRIPT_DIR}/iam/scheduler-cloudfront-policy.json")"

        log_info "Created Scheduler role: ${PREFIX}-scheduler-role"
        sleep 10
    fi

    SCHEDULER_ROLE_ARN="arn:aws:iam::${ACCOUNT_ID}:role/${PREFIX}-scheduler-role"

    SCHEDULE_ARGS=(
        --schedule-expression "cron(0 13 * * ? *)"
        --schedule-expression-timezone "America/Los_Angeles"
        --target "{
            \"Arn\": \"arn:aws:scheduler:::aws-sdk:cloudfront:createInvalidation\",
            \"RoleArn\": \"${SCHEDULER_ROLE_ARN}\",
            \"Input\": \"{\\\"DistributionId\\\": \\\"${DIST_ID}\\\", \\\"InvalidationBatch\\\": {\\\"Paths\\\": {\\\"Quantity\\\": 1, \\\"Items\\\": [\\\"/*\\\"]}, \\\"CallerReference\\\": \\\"scheduled-<aws.scheduler.execution-id>\\\"}}\"
        }"
        --flexible-time-window '{"Mode": "OFF"}'
        --state ENABLED
        --region $REGION
    )

    if aws scheduler get-schedule --name ${PREFIX}-cloudfront-invalidation --region $REGION &> /dev/null; then
        aws scheduler update-schedule --name ${PREFIX}-cloudfront-invalidation "${SCHEDULE_ARGS[@]}"
        log_info "Updated CloudFront invalidation schedule"
    else
        aws scheduler create-schedule --name ${PREFIX}-cloudfront-invalidation "${SCHEDULE_ARGS[@]}"
        log_info "Created CloudFront invalidation schedule: daily at 1 PM Pacific"
    fi
}

# Create EventBridge rules for scheduled jobs
create_eventbridge_rules() {
    log_info "Creating EventBridge scheduled rules..."

    if [ "$ENABLE_OPENSEARCH" = "true" ]; then
        # Ingestion job - weekly on Sunday at midnight UTC
        if ! aws events describe-rule --name ${PREFIX}-ingestion-schedule --region $REGION &> /dev/null; then
            aws events put-rule \
                --name ${PREFIX}-ingestion-schedule \
                --schedule-expression "cron(0 0 ? * SUN *)" \
                --state ENABLED \
                --tags Key=Project,Value=${PROJECT_TAG} \
                --region $REGION
            log_info "Created EventBridge rule: ${PREFIX}-ingestion-schedule (weekly on Sunday at midnight UTC)"
        fi

        # Feed job - weekly on Sunday at 2 AM UTC
        if ! aws events describe-rule --name ${PREFIX}-feed-schedule --region $REGION &> /dev/null; then
            aws events put-rule \
                --name ${PREFIX}-feed-schedule \
                --schedule-expression "cron(0 2 ? * SUN *)" \
                --state ENABLED \
                --tags Key=Project,Value=${PROJECT_TAG} \
                --region $REGION
            log_info "Created EventBridge rule: ${PREFIX}-feed-schedule (weekly on Sunday at 2 AM UTC)"
        fi
    fi

    # Add ECS targets using inline JSON
    JOB_TASKS=()
    if [ "$ENABLE_OPENSEARCH" = "true" ]; then
        JOB_TASKS=("ingestion" "feed")
    fi

    for TASK in "${JOB_TASKS[@]}"; do
        TASK_DEF_ARN=$(aws ecs describe-task-definition --task-definition ${PREFIX}-${TASK}-task --region $REGION --query 'taskDefinition.taskDefinitionArn' --output text)

        TARGET_JSON='[{"Id":"'${PREFIX}'-'${TASK}'-target","Arn":"arn:aws:ecs:'${REGION}':'${ACCOUNT_ID}':cluster/'${PREFIX}'-cluster","RoleArn":"arn:aws:iam::'${ACCOUNT_ID}':role/'${PREFIX}'-eventbridge-role","EcsParameters":{"TaskDefinitionArn":"'${TASK_DEF_ARN}'","TaskCount":1,"LaunchType":"FARGATE","NetworkConfiguration":{"awsvpcConfiguration":{"Subnets":["'${SUBNET_1_ID}'","'${SUBNET_2_ID}'"],"SecurityGroups":["'${API_SG_ID}'"],"AssignPublicIp":"ENABLED"}}}}]'

        aws events put-targets \
            --rule ${PREFIX}-${TASK}-schedule \
            --targets "$TARGET_JSON" \
            --region $REGION

        log_info "Added ECS target to ${PREFIX}-${TASK}-schedule"
    done
}

# Print summary
print_summary() {
    echo ""
    echo "=============================================="
    echo -e "${GREEN}CRS AWS Deployment Complete!${NC}"
    echo "=============================================="
    echo ""
    echo "Resources created (all prefixed with 'crs-'):"
    echo ""
    echo "Networking:"
    echo "  - VPC: ${PREFIX}-vpc ($VPC_ID)"
    echo "  - Subnets: ${PREFIX}-subnet-1, ${PREFIX}-subnet-2"
    echo ""
    echo "Container Registry:"
    echo "  - ECR: ${ECR_URI}/crs-api"
    echo "  - ECR: ${ECR_URI}/crs-jobs"
    echo ""
    echo "Database:"
    echo "  - RDS PostgreSQL: ${PREFIX}-db"
    echo "  - Endpoint: ${RDS_ENDPOINT}"
    echo ""
    echo "API:"
    if [ "${DEPLOY_ECS_EXPRESS,,}" = "true" ]; then
        echo "  - ECS Express: ${API_EXPRESS_SERVICE_NAME}"
        echo "  - URL: https://${ECS_EXPRESS_API_URL}"
        echo "  - CloudWatch Logs: ${API_EXPRESS_LOG_GROUP}"
    fi
    echo "  - OTEL collector: ${OTEL_COLLECTOR_SERVICE_NAME}"
    echo "  - OTEL endpoint: ${OTEL_COLLECTOR_DISCOVERY_SERVICE_NAME}.${OTEL_COLLECTOR_NAMESPACE_NAME}:4317"
    echo ""
    echo "Web (Static):"
    echo "  - S3 Bucket: ${BUCKET_NAME}"
    echo "  - URL: ${WEB_URL}"
    echo ""
    echo "Vector Search:"
    if [ "$ENABLE_OPENSEARCH" != "true" ]; then
        echo "  - OpenSearch: skipped (ENABLE_OPENSEARCH is not true)"
    else
        echo "  - OpenSearch: ${PREFIX}-search"
        echo "  - Endpoint: ${OPENSEARCH_ENDPOINT}"
    fi
    echo ""
    echo "Scheduled Jobs:"
    if [ "$ENABLE_OPENSEARCH" = "true" ]; then
        echo "  - Ingestion: Weekly on Sunday at midnight UTC"
        echo "  - Feed: Weekly on Sunday at 2 AM UTC"
    else
        echo "  - None (AWS ingestion/feed schedules disabled)"
    fi
    echo "  - CloudFront invalidation: Daily at 1:00 PM Pacific"
    echo ""
    echo "Observability:"
    echo "  - Metrics namespace: ${METRICS_NAMESPACE}"
    echo "  - Log retention days: ${LOG_RETENTION_DAYS}"
    echo "  - Dashboard: ${PREFIX}-platform-overview"
    echo "  - Dashboard: ${PREFIX}-api-observability"
    echo "  - Dashboard: ${PREFIX}-jobs-observability"
    echo "  - Dashboard: ${PREFIX}-dependency-frontend-observability"
    echo "  - API logs: ${API_EXPRESS_LOG_GROUP}"
    echo "  - Collector logs: /crs/otel-collector"
    echo "  - Local jobs logs: /crs/local-jobs"
    echo "  - Windows host logs: /crs/windows-host"
    echo "  - CloudWatch Agent logs: /crs/cloudwatch-agent"
    echo "  - CloudWatch RUM monitor: ${RUM_APP_MONITOR_NAME}"
    echo ""
    echo "=============================================="
    echo "Next Steps:"
    echo "1. Build and push Docker images:"
    echo "   ./infrastructure/aws/build-and-push.sh"
    echo ""
    echo "2. Deploy web frontend:"
    echo "   ./infrastructure/aws/deploy-web.sh"
    echo ""
    echo "3. Tail ECS Express API logs:"
    echo "   aws logs tail ${API_EXPRESS_LOG_GROUP} --follow --region ${REGION}"
    echo "=============================================="
}

# Main execution
main() {
    log_info "Starting CRS AWS deployment..."
    log_info "Region: $REGION"
    log_info "Environment: $ENVIRONMENT"
    echo ""

    check_aws_cli
    create_vpc
    create_security_groups
    create_ecr
    create_secrets
    create_cloudwatch_logs
    create_iam_roles
    create_rds
    create_s3_web
    create_rum_app_monitor
    create_ecs_cluster
    create_service_discovery_namespace
    register_otel_collector_task_definition
    create_otel_collector_service
    create_opensearch
    prepare_api_runtime_configuration
    create_ecs_express_api_service
    register_task_definitions
    create_eventbridge_rules
    create_cloudfront_invalidation_schedule
    create_cloudwatch_dashboards
    create_cloudwatch_alarms
    print_summary
}

# Run main
main "$@"
