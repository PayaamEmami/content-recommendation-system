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

    export API_SG_ID RDS_SG_ID
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

    log_info "App Runner API logs are managed under /aws/apprunner/${PREFIX}-api/<service-id>/application"

    for LOG_NAME in "jobs" "ingestion" "feed" "x-ingestion" "reindex" "sync-index" "otel-collector" "local-jobs" "windows-host" "cloudwatch-agent"; do
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

    # App Runner role
    if ! aws iam get-role --role-name ${PREFIX}-apprunner-role &> /dev/null; then
        aws iam create-role \
            --role-name ${PREFIX}-apprunner-role \
            --assume-role-policy-document "$(cat "${SCRIPT_DIR}/iam/apprunner-trust-policy.json")" \
            --tags Key=Project,Value=${PROJECT_TAG}

        log_info "Created App Runner role: ${PREFIX}-apprunner-role"
    fi

    ensure_managed_role_policy "${PREFIX}-apprunner-role" "arn:aws:iam::aws:policy/service-role/AWSAppRunnerServicePolicyForECRAccess"
    ensure_managed_role_policy "${PREFIX}-apprunner-role" "arn:aws:iam::aws:policy/AWSXRayDaemonWriteAccess"
    ensure_inline_role_policy "${PREFIX}-apprunner-role" "${PREFIX}-app-policy" "${SCRIPT_DIR}/iam/app-policy.json"

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
        DATA_ACCESS_POLICY='[{"Rules":[{"ResourceType":"collection","Resource":["collection/'${PREFIX}'-search"],"Permission":["aoss:*"]},{"ResourceType":"index","Resource":["index/'${PREFIX}'-search/*"],"Permission":["aoss:*"]}],"Principal":["arn:aws:iam::'${ACCOUNT_ID}':role/'${PREFIX}'-apprunner-role","arn:aws:iam::'${ACCOUNT_ID}':role/'${PREFIX}'-ecs-task-role","arn:aws:iam::'${ACCOUNT_ID}':root"]}]'

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

create_apprunner_observability_configuration() {
    log_info "Creating App Runner observability configuration..."

    APP_RUNNER_OBSERVABILITY_CONFIGURATION_ARN=$(aws apprunner list-observability-configurations \
        --region $REGION \
        --query "ObservabilityConfigurationSummaryList[?ObservabilityConfigurationName=='${PREFIX}-xray'].ObservabilityConfigurationArn | [0]" \
        --output text 2>/dev/null || echo "")

    if [ -z "$APP_RUNNER_OBSERVABILITY_CONFIGURATION_ARN" ] || [ "$APP_RUNNER_OBSERVABILITY_CONFIGURATION_ARN" = "None" ]; then
        APP_RUNNER_OBSERVABILITY_CONFIGURATION_ARN=$(aws apprunner create-observability-configuration \
            --observability-configuration-name "${PREFIX}-xray" \
            --trace-configuration '{"Vendor":"AWSXRAY"}' \
            --tags Key=Project,Value=${PROJECT_TAG} \
            --region $REGION \
            --query 'ObservabilityConfiguration.ObservabilityConfigurationArn' \
            --output text)
        log_info "Created App Runner observability configuration: ${PREFIX}-xray"
    else
        log_info "App Runner observability configuration already exists: ${PREFIX}-xray"
    fi

    export APP_RUNNER_OBSERVABILITY_CONFIGURATION_ARN
}

# Create App Runner Service
create_app_runner() {
    log_info "Creating App Runner service for API..."

    # Check if service exists
    SERVICE_ARN=$(aws apprunner list-services --region $REGION --query "ServiceSummaryList[?ServiceName=='${PREFIX}-api'].ServiceArn" --output text 2>/dev/null || echo "")
    CONNECTION_STRING="Host=${RDS_ENDPOINT};Database=crsdb;Username=${DB_USERNAME};Password=${DB_PASSWORD}"
    APP_RUNNER_INSTANCE_CONFIGURATION='{
        "Cpu": "0.25 vCPU",
        "Memory": "0.5 GB",
        "InstanceRoleArn": "arn:aws:iam::'${ACCOUNT_ID}':role/'${PREFIX}'-apprunner-role"
    }'
    APP_RUNNER_SOURCE_CONFIGURATION='{
        "AuthenticationConfiguration": {
            "AccessRoleArn": "arn:aws:iam::'${ACCOUNT_ID}':role/'${PREFIX}'-apprunner-role"
        },
        "AutoDeploymentsEnabled": false,
        "ImageRepository": {
            "ImageIdentifier": "'${ECR_URI}'/crs-api:latest",
            "ImageRepositoryType": "ECR",
            "ImageConfiguration": {
                "Port": "8080",
                "RuntimeEnvironmentVariables": {
                    "ASPNETCORE_ENVIRONMENT": "Production",
                    "ConnectionStrings__DefaultConnection": "'"${CONNECTION_STRING}"'",
                    "OpenAI__ApiKey": "'"${OpenAI__ApiKey}"'",
                    "Embedding__ModelName": "text-embedding-3-small",
                    "Embedding__Dimensions": "1536",
                    "OpenSearch__Endpoint": "'"${OPENSEARCH_ENDPOINT}"'",
                    "OpenSearch__IndexName": "crs-content",
                    "OpenSearch__EmbeddingDimensions": "1536",
                    "JwtSettings__SecretKey": "'"${JWT_SECRET}"'",
                    "JwtSettings__ExpirationMinutes": "60",
                    "Cors__AllowedOrigins__0": "'"${WEB_URL}"'",
                    "Cors__AllowedOrigins__1": "'"${CF_URL:-$WEB_URL}"'",
                    "Registration__Enabled": "true",
                    "Observability__Environment": "'"${ENVIRONMENT}"'",
                    "Observability__ExecutionEnvironment": "aws",
                    "Observability__ServiceName": "'"${API_SERVICE_NAME}"'",
                    "Observability__ServiceNamespace": "crs",
                    "Observability__MetricsNamespace": "'"${METRICS_NAMESPACE}"'",
                    "Observability__TraceSampleRatio": "'"${TRACE_SAMPLE_RATIO}"'",
                    "Observability__EnableSensitiveBodyLogging": "false",
                    "OTEL_EXPORTER_OTLP_ENDPOINT": "http://localhost:4317",
                    "OTEL_EXPORTER_OTLP_PROTOCOL": "grpc",
                    "OTEL_METRICS_EXPORTER": "none",
                    "OTEL_LOGS_EXPORTER": "none",
                    "OTEL_PROPAGATORS": "xray",
                    "X__ClientId": "'"${X__ClientId}"'",
                    "X__ClientSecret": "'"${X__ClientSecret}"'",
                    "X__RedirectUri": "'"${X__RedirectUri}"'"
                }
            }
        }
    }'

    if [ -z "$SERVICE_ARN" ]; then
        # Create service
        SERVICE_ARN=$(aws apprunner create-service \
            --service-name ${PREFIX}-api \
            --source-configuration "${APP_RUNNER_SOURCE_CONFIGURATION}" \
            --instance-configuration "${APP_RUNNER_INSTANCE_CONFIGURATION}" \
            --observability-configuration "ObservabilityEnabled=true,ObservabilityConfigurationArn=${APP_RUNNER_OBSERVABILITY_CONFIGURATION_ARN}" \
            --health-check-configuration '{"Protocol": "HTTP", "Path": "/health/ready", "Interval": 20, "Timeout": 5, "HealthyThreshold": 1, "UnhealthyThreshold": 5}' \
            --tags Key=Project,Value=${PROJECT_TAG} \
            --region $REGION \
            --query 'Service.ServiceArn' \
            --output text)

        log_info "Creating App Runner service (this takes 2-5 minutes)..."

        # Wait for service to be running
        while true; do
            STATUS=$(aws apprunner describe-service --service-arn $SERVICE_ARN --region $REGION --query 'Service.Status' --output text)
            if [ "$STATUS" = "RUNNING" ]; then
                break
            fi
            echo -n "."
            sleep 10
        done
        echo ""
        log_info "App Runner service is now running!"
    else
        log_info "App Runner service already exists: ${PREFIX}-api"

        aws apprunner update-service \
            --service-arn $SERVICE_ARN \
            --source-configuration "${APP_RUNNER_SOURCE_CONFIGURATION}" \
            --instance-configuration "${APP_RUNNER_INSTANCE_CONFIGURATION}" \
            --observability-configuration "ObservabilityEnabled=true,ObservabilityConfigurationArn=${APP_RUNNER_OBSERVABILITY_CONFIGURATION_ARN}" \
            --health-check-configuration '{"Protocol": "HTTP", "Path": "/health/ready", "Interval": 20, "Timeout": 5, "HealthyThreshold": 1, "UnhealthyThreshold": 5}' \
            --region $REGION > /dev/null

        log_info "Updating App Runner service configuration..."

        while true; do
            STATUS=$(aws apprunner describe-service --service-arn $SERVICE_ARN --region $REGION --query 'Service.Status' --output text)
            if [ "$STATUS" = "RUNNING" ]; then
                break
            fi
            echo -n "."
            sleep 10
        done
        echo ""
        log_info "App Runner service configuration updated"
    fi

    # Get service URL
    API_URL=$(aws apprunner describe-service --service-arn $SERVICE_ARN --region $REGION --query 'Service.ServiceUrl' --output text 2>/dev/null || \
              aws apprunner list-services --region $REGION --query "ServiceSummaryList[?ServiceName=='${PREFIX}-api'].ServiceUrl" --output text)
    SERVICE_ID=$(aws apprunner describe-service --service-arn $SERVICE_ARN --region $REGION --query 'Service.ServiceId' --output text)
    APP_RUNNER_APPLICATION_LOG_GROUP="/aws/apprunner/${PREFIX}-api/${SERVICE_ID}/application"
    aws logs put-retention-policy --log-group-name "$APP_RUNNER_APPLICATION_LOG_GROUP" --retention-in-days $LOG_RETENTION_DAYS --region $REGION 2>/dev/null || true
    log_info "API URL: https://$API_URL"
    log_info "API log group: ${APP_RUNNER_APPLICATION_LOG_GROUP}"
    export API_URL APP_RUNNER_APPLICATION_LOG_GROUP
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
            --container-definitions "file://${TASK_DEFINITION_FILE}" \
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
        "title": "App Runner Readiness and Latency",
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
    echo "  - App Runner: ${PREFIX}-api"
    echo "  - URL: https://${API_URL}"
    echo "  - CloudWatch Logs: ${APP_RUNNER_APPLICATION_LOG_GROUP}"
    echo "  - X-Ray Observability: ${APP_RUNNER_OBSERVABILITY_CONFIGURATION_ARN}"
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
    echo "  - CloudFront invalidation: Daily at 10 AM Pacific"
    echo ""
    echo "Observability:"
    echo "  - Metrics namespace: ${METRICS_NAMESPACE}"
    echo "  - Log retention days: ${LOG_RETENTION_DAYS}"
    echo "  - Dashboard: ${PREFIX}-platform-overview"
    echo "  - Dashboard: ${PREFIX}-api-observability"
    echo "  - Dashboard: ${PREFIX}-jobs-observability"
    echo "  - Dashboard: ${PREFIX}-dependency-frontend-observability"
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
    echo "3. Tail API logs:"
    echo "   aws logs tail ${APP_RUNNER_APPLICATION_LOG_GROUP} --follow --region ${REGION}"
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
    create_opensearch
    create_apprunner_observability_configuration
    create_app_runner
    register_task_definitions
    create_eventbridge_rules
    create_cloudfront_invalidation_schedule
    create_cloudwatch_dashboards
    create_cloudwatch_alarms
    print_summary
}

# Run main
main "$@"
