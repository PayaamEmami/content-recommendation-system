#!/usr/bin/env bash
# One-shot cutover helper: dump Users from a source Postgres and restore into Lightsail.
# Originally used against RDS crs-db; keep only if you still have a dump source.
# Usage: ./migrate-users.sh
set -euo pipefail

REGION="${REGION:-us-west-2}"
RDS_ID="${RDS_ID:-crs-db}"
STATIC_IP_NAME="${STATIC_IP_NAME:-crs-lightsail-ip}"
SSH_KEY_PATH="${SSH_KEY_PATH:-$HOME/.ssh/crs-lightsail-key.pem}"
SSH_USER="${SSH_USER:-ubuntu}"
DUMP_DIR="${DUMP_DIR:-/tmp/crs-migrate}"

log() { printf '[migrate-users] %s\n' "$*"; }

mkdir -p "$DUMP_DIR"
chmod 700 "$DUMP_DIR"

DB_PASSWORD_FILE="${DB_PASSWORD_FILE:-$DUMP_DIR/db-password.txt}"
if [[ ! -f "$DB_PASSWORD_FILE" ]]; then
  aws secretsmanager get-secret-value \
    --region "$REGION" \
    --secret-id crs-secrets/db-password \
    --query SecretString \
    --output text > "$DB_PASSWORD_FILE"
  chmod 600 "$DB_PASSWORD_FILE"
fi

RDS_HOST="$(aws rds describe-db-instances \
  --region "$REGION" \
  --db-instance-identifier "$RDS_ID" \
  --query 'DBInstances[0].Endpoint.Address' \
  --output text)"
STATIC_IP="$(aws lightsail get-static-ip --region "$REGION" --static-ip-name "$STATIC_IP_NAME" --query 'staticIp.ipAddress' --output text)"
SSH=(ssh -i "$SSH_KEY_PATH" -o StrictHostKeyChecking=accept-new -o IdentitiesOnly=yes "${SSH_USER}@${STATIC_IP}")
SCP=(scp -i "$SSH_KEY_PATH" -o StrictHostKeyChecking=accept-new -o IdentitiesOnly=yes)

log "Waiting for Lightsail Postgres"
for _ in $(seq 1 60); do
  if "${SSH[@]}" 'sudo docker exec crs-postgres pg_isready -U crsadmin -d crsdb' >/dev/null 2>&1; then
    break
  fi
  sleep 5
done

log "Ensuring Users table exists (API runs EF migrations on startup)"
for _ in $(seq 1 60); do
  if "${SSH[@]}" "sudo docker exec crs-postgres psql -U crsadmin -d crsdb -tAc \"SELECT to_regclass('public.\\\"Users\\\"')\"" | grep -q Users; then
    break
  fi
  "${SSH[@]}" 'cd /opt/crs && sudo docker compose up -d api' >/dev/null 2>&1 || true
  sleep 5
done

log "Copying DB password and dumping Users from ${RDS_HOST} via postgres:15 on Lightsail"
"${SCP[@]}" "$DB_PASSWORD_FILE" "${SSH_USER}@${STATIC_IP}:/tmp/db-password.txt"
"${SSH[@]}" "chmod 600 /tmp/db-password.txt"

"${SSH[@]}" bash -s -- "$RDS_HOST" <<'REMOTE'
set -euo pipefail
RDS_HOST="$1"
PW="$(tr -d '\n' < /tmp/db-password.txt)"
sudo docker run --rm -e "PGPASSWORD=${PW}" postgres:15-alpine \
  pg_dump -h "$RDS_HOST" -U crsadmin -d crsdb -t '"Users"' --data-only --inserts --no-owner --no-privileges \
  > /tmp/users.sql
echo "dump_lines=$(wc -l < /tmp/users.sql)"
sudo docker exec crs-postgres psql -U crsadmin -d crsdb -c 'TRUNCATE TABLE "Users" CASCADE;'
sudo docker exec -i crs-postgres psql -U crsadmin -d crsdb < /tmp/users.sql
echo -n "users_count="
sudo docker exec crs-postgres psql -U crsadmin -d crsdb -tAc 'SELECT COUNT(*) FROM "Users";'
rm -f /tmp/users.sql /tmp/db-password.txt
REMOTE

log "Done. Log in via the web UI (refresh tokens were not migrated)."
