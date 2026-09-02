#!/usr/bin/env bash
# Deploy/update the CRS Compose stack on crs-lightsail-small.
# SSH target is the static IP, not the instance name.
# Usage: ./deploy-lightsail.sh
set -euo pipefail

REGION="${REGION:-us-west-2}"
INSTANCE_NAME="${INSTANCE_NAME:-crs-lightsail-small}"
STATIC_IP_NAME="${STATIC_IP_NAME:-crs-lightsail-ip}"
SSH_KEY_PATH="${SSH_KEY_PATH:-$HOME/.ssh/crs-lightsail-key.pem}"
SSH_USER="${SSH_USER:-ubuntu}"
REMOTE_DIR="${REMOTE_DIR:-/opt/crs}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

log() { printf '[deploy-lightsail] %s\n' "$*"; }

STATIC_IP="$(aws lightsail get-static-ip --region "$REGION" --static-ip-name "$STATIC_IP_NAME" --query 'staticIp.ipAddress' --output text)"
[[ -n "$STATIC_IP" && "$STATIC_IP" != "None" ]] || { log "Could not resolve static IP"; exit 1; }

if [[ ! -f "$SCRIPT_DIR/.env" ]]; then
  log "Missing ${SCRIPT_DIR}/.env — copy lightsail.env.example and fill secrets first"
  exit 1
fi

# Ensure hostname matches current static IP if still using sslip.io placeholder pattern
if grep -q 'CRS_API_HOSTNAME=.*sslip\.io' "$SCRIPT_DIR/.env"; then
  CURRENT_HOST="$(grep '^CRS_API_HOSTNAME=' "$SCRIPT_DIR/.env" | cut -d= -f2-)"
  DESIRED_HOST="${STATIC_IP}.sslip.io"
  if [[ "$CURRENT_HOST" != "$DESIRED_HOST" ]]; then
    log "Updating CRS_API_HOSTNAME to ${DESIRED_HOST}"
    if [[ "$(uname)" == "Darwin" ]]; then
      sed -i '' "s|^CRS_API_HOSTNAME=.*|CRS_API_HOSTNAME=${DESIRED_HOST}|" "$SCRIPT_DIR/.env"
    else
      sed -i "s|^CRS_API_HOSTNAME=.*|CRS_API_HOSTNAME=${DESIRED_HOST}|" "$SCRIPT_DIR/.env"
    fi
  fi
fi

SSH=(ssh -i "$SSH_KEY_PATH" -o StrictHostKeyChecking=accept-new -o IdentitiesOnly=yes "${SSH_USER}@${STATIC_IP}")
SCP=(scp -i "$SSH_KEY_PATH" -o StrictHostKeyChecking=accept-new -o IdentitiesOnly=yes)

log "Waiting for SSH on ${STATIC_IP}..."
for _ in $(seq 1 60); do
  if "${SSH[@]}" "echo ok" >/dev/null 2>&1; then
    break
  fi
  sleep 5
done
"${SSH[@]}" "echo ok" >/dev/null

log "Installing Docker if needed"
"${SSH[@]}" 'bash -s' <<'REMOTE'
set -euo pipefail
if ! command -v docker >/dev/null 2>&1; then
  sudo apt-get update -y
  sudo apt-get install -y ca-certificates curl
  sudo install -m 0755 -d /etc/apt/keyrings
  sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
  sudo chmod a+r /etc/apt/keyrings/docker.asc
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" | sudo tee /etc/apt/sources.list.d/docker.list >/dev/null
  sudo apt-get update -y
  sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-compose-plugin
  sudo usermod -aG docker ubuntu || true
fi
sudo mkdir -p /opt/crs
sudo chown ubuntu:ubuntu /opt/crs
REMOTE

log "Copying compose files"
"${SCP[@]}" \
  "$SCRIPT_DIR/docker-compose.yml" \
  "$SCRIPT_DIR/Caddyfile" \
  "$SCRIPT_DIR/.env" \
  "${SSH_USER}@${STATIC_IP}:${REMOTE_DIR}/"

# Prefer ECR pull when AWS CLI credentials are available on the deployer
ACCOUNT_ID="$(aws sts get-caller-identity --query Account --output text)"
ECR_REGISTRY="${ACCOUNT_ID}.dkr.ecr.${REGION}.amazonaws.com"
CRS_API_IMAGE="$(grep '^CRS_API_IMAGE=' "$SCRIPT_DIR/.env" | cut -d= -f2-)"

log "Logging remote host into ECR and pulling ${CRS_API_IMAGE}"
ECR_PASSWORD="$(aws ecr get-login-password --region "$REGION")"
"${SSH[@]}" "echo '${ECR_PASSWORD}' | sudo docker login --username AWS --password-stdin '${ECR_REGISTRY}'"
"${SSH[@]}" "cd '${REMOTE_DIR}' && sudo docker compose pull api || true"
# If image pull failed because local build is required, build is not supported here —
# images must exist in ECR. Fall through to compose up which will error clearly.

log "Starting stack"
"${SSH[@]}" "cd '${REMOTE_DIR}' && sudo docker compose up -d"

log "Waiting for API health via Caddy"
API_HOST="$(grep '^CRS_API_HOSTNAME=' "$SCRIPT_DIR/.env" | cut -d= -f2-)"
for _ in $(seq 1 60); do
  code="$(curl -4 -sS -o /dev/null -w '%{http_code}' --max-time 10 "https://${API_HOST}/health" || true)"
  if [[ "$code" == "200" ]]; then
    log "Healthy: https://${API_HOST}/health"
    exit 0
  fi
  sleep 5
done

log "Stack started but /health not ready yet (last HTTP ${code:-n/a}) — check: ssh -i ${SSH_KEY_PATH} ${SSH_USER}@${STATIC_IP} 'cd ${REMOTE_DIR} && sudo docker compose ps && sudo docker compose logs --tail=100'"
exit 0
