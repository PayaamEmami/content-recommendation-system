#!/usr/bin/env bash
# Local Crs.Jobs runner. Loads infrastructure/aws/.env.
# Works on macOS, Linux, and Windows Git Bash / WSL.
#
# Default (no args): daily pipeline — x-ingestion is independent of source
# ingestion/feed; feed runs only after ingestion succeeds.
#
# By default, jobs reach Lightsail Postgres through an SSH tunnel
# (127.0.0.1:15432 -> instance 5432) so public 5432 is not required.
set -euo pipefail

usage() {
  cat <<'EOF'
Usage:
  ./scripts/run-job.sh                 Daily pipeline (x-ingestion, ingestion, feed)
  ./scripts/run-job.sh --all           Same as the default pipeline
  ./scripts/run-job.sh <job-name>      Run one job
  ./scripts/run-job.sh --help

Pipeline:
  1. x-ingestion   Always attempted. Failure does not skip ingestion/feed.
  2. ingestion     Pull sources, embed, and index.
  3. feed          Runs only if ingestion succeeded.

Pipeline options:
  --skip-x            Skip x-ingestion
  --skip-ingestion    Skip ingestion (feed is also skipped)
  --no-tunnel         Connect using ConnectionStrings__DefaultConnection as-is
                      (skip the SSH tunnel to Lightsail Postgres)

Single jobs:
  ingestion     Pull content from configured sources
  x-ingestion   Sync posts from connected X accounts
  feed          Generate personalized recommendation feeds
  reindex       Rebuild embeddings and store them in Postgres
EOF
}

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
secrets_file="$repo_root/infrastructure/aws/.env"

mode="pipeline"
job_name=""
skip_x=0
skip_ingestion=0
use_tunnel=1
results=()
tunnel_pid=""
tunnel_started=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    -h|--help)
      usage
      exit 0
      ;;
    --all)
      mode="pipeline"
      shift
      ;;
    --skip-x)
      skip_x=1
      shift
      ;;
    --skip-ingestion)
      skip_ingestion=1
      shift
      ;;
    --no-tunnel)
      use_tunnel=0
      shift
      ;;
    ingestion|x-ingestion|feed|reindex)
      if [[ -n "$job_name" ]]; then
        echo "Specify at most one job name. Use the default pipeline to run several." >&2
        usage >&2
        exit 1
      fi
      job_name="$1"
      mode="single"
      shift
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

if [[ "$mode" == "single" && ( "$skip_x" -eq 1 || "$skip_ingestion" -eq 1 ) ]]; then
  echo "--skip-x and --skip-ingestion apply only to the daily pipeline." >&2
  exit 1
fi

trim() {
  local value="$1"
  value="${value#"${value%%[![:space:]]*}"}"
  value="${value%"${value##*[![:space:]]}"}"
  printf '%s' "$value"
}

load_secrets() {
  if [[ ! -f "$secrets_file" ]]; then
    echo "No secrets file at $secrets_file; using existing environment variables."
    return
  fi

  echo "Loading job secrets from $secrets_file"
  while IFS= read -r line || [[ -n "$line" ]]; do
    line="$(trim "$line")"
    [[ -z "$line" || "$line" == \#* ]] && continue
    [[ "$line" != *=* ]] && continue

    local name value
    name="$(trim "${line%%=*}")"
    value="$(trim "${line#*=}")"
    [[ -z "$name" ]] && continue

    if [[ -n "${!name:-}" ]]; then
      continue
    fi

    export "$name=$value"
  done < "$secrets_file"
}

cs_field() {
  local cs="$1"
  local want="$2"
  local part name value rest="$cs"
  local want_lc name_lc
  want_lc="$(printf '%s' "$want" | tr '[:upper:]' '[:lower:]')"
  while [[ -n "$rest" ]]; do
    if [[ "$rest" == *';'* ]]; then
      part="${rest%%;*}"
      rest="${rest#*;}"
    else
      part="$rest"
      rest=""
    fi
    [[ -z "$part" ]] && continue
    name="${part%%=*}"
    if [[ "$part" == *=* ]]; then
      value="${part#*=}"
    else
      value=""
    fi
    name_lc="$(printf '%s' "$name" | tr '[:upper:]' '[:lower:]')"
    if [[ "$name_lc" == "$want_lc" ]]; then
      printf '%s' "$value"
      return 0
    fi
  done
  return 1
}

is_loopback_host() {
  local host="$1"
  [[ "$host" == "127.0.0.1" || "$host" == "localhost" || "$host" == "::1" ]]
}

is_ipv4() {
  local host="$1"
  [[ "$host" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]
}

port_is_open() {
  local host="$1"
  local port="$2"
  if command -v python3 >/dev/null 2>&1; then
    python3 -c "import socket; s=socket.create_connection(('$host', int('$port')), 1); s.close()" >/dev/null 2>&1 && return 0
  fi
  if command -v nc >/dev/null 2>&1; then
    nc -z "$host" "$port" >/dev/null 2>&1 && return 0
  fi
  return 1
}

resolve_jobs_ssh_host() {
  local host="" cs_host="" api_host="${CRS_API_HOSTNAME:-}"

  if [[ -n "${CRS_JOBS_SSH_HOST:-}" ]]; then
    printf '%s' "$CRS_JOBS_SSH_HOST"
    return 0
  fi

  api_host="${api_host%.sslip.io}"
  if is_ipv4 "$api_host"; then
    printf '%s' "$api_host"
    return 0
  fi

  cs_host="$(cs_field "${ConnectionStrings__DefaultConnection:-}" Host || true)"
  if [[ -n "$cs_host" ]] && ! is_loopback_host "$cs_host" && [[ "$cs_host" != "postgres" ]]; then
    printf '%s' "$cs_host"
    return 0
  fi

  echo "Cannot resolve Lightsail SSH host. Set CRS_JOBS_SSH_HOST, CRS_API_HOSTNAME (<ip>.sslip.io), or ConnectionStrings__DefaultConnection Host." >&2
  return 1
}

apply_tunnel_connection_string() {
  local local_port="$1"
  local cs="${ConnectionStrings__DefaultConnection:-}"
  local db user pass

  db="$(cs_field "$cs" Database || true)"
  user="$(cs_field "$cs" Username || true)"
  pass="$(cs_field "$cs" Password || true)"
  db="${db:-crsdb}"
  user="${user:-${SQL_ADMIN_USERNAME:-crsadmin}}"
  pass="${pass:-${DB_PASSWORD:-}}"

  if [[ -z "$pass" ]]; then
    echo "No Postgres password found. Set DB_PASSWORD or ConnectionStrings__DefaultConnection in infrastructure/aws/.env." >&2
    return 1
  fi

  export ConnectionStrings__DefaultConnection="Host=127.0.0.1;Port=${local_port};Database=${db};Username=${user};Password=${pass}"
}

cleanup_jobs_tunnel() {
  if [[ "$tunnel_started" -eq 1 && -n "$tunnel_pid" ]]; then
    kill "$tunnel_pid" >/dev/null 2>&1 || true
    wait "$tunnel_pid" >/dev/null 2>&1 || true
    tunnel_started=0
    tunnel_pid=""
  fi
}

start_jobs_tunnel() {
  local local_port ssh_user ssh_key ssh_host i

  if [[ "$use_tunnel" -eq 0 ]]; then
    echo "SSH tunnel disabled (--no-tunnel); using ConnectionStrings__DefaultConnection as-is."
    return 0
  fi
  if [[ "${CRS_JOBS_SSH_TUNNEL:-1}" == "0" ]]; then
    echo "SSH tunnel disabled (CRS_JOBS_SSH_TUNNEL=0); using ConnectionStrings__DefaultConnection as-is."
    return 0
  fi

  local_port="${CRS_JOBS_TUNNEL_LOCAL_PORT:-15432}"
  ssh_user="${CRS_JOBS_SSH_USER:-ubuntu}"
  ssh_key="${CRS_JOBS_SSH_KEY:-$HOME/.ssh/crs-lightsail-key.pem}"
  ssh_key="${ssh_key/#\~/$HOME}"

  if port_is_open "127.0.0.1" "$local_port"; then
    echo "Reusing existing listener on 127.0.0.1:${local_port}"
  else
    if [[ ! -f "$ssh_key" ]]; then
      echo "SSH key not found at $ssh_key (used by deploy-lightsail.sh). Set CRS_JOBS_SSH_KEY or use --no-tunnel." >&2
      return 1
    fi
    ssh_host="$(resolve_jobs_ssh_host)" || return 1
    echo "Opening SSH tunnel 127.0.0.1:${local_port} -> Lightsail Postgres (port 5432)"
    ssh -N \
      -o BatchMode=yes \
      -o ExitOnForwardFailure=yes \
      -o StrictHostKeyChecking=accept-new \
      -o IdentitiesOnly=yes \
      -o ServerAliveInterval=30 \
      -o ServerAliveCountMax=3 \
      -i "$ssh_key" \
      -L "127.0.0.1:${local_port}:127.0.0.1:5432" \
      "${ssh_user}@${ssh_host}" &
    tunnel_pid=$!
    tunnel_started=1

    for i in $(seq 1 50); do
      if port_is_open "127.0.0.1" "$local_port"; then
        break
      fi
      if ! kill -0 "$tunnel_pid" >/dev/null 2>&1; then
        echo "SSH tunnel exited before the local port was ready. Check SSH (port 22) to Lightsail." >&2
        wait "$tunnel_pid" >/dev/null 2>&1 || true
        tunnel_started=0
        tunnel_pid=""
        return 1
      fi
      sleep 0.2
    done

    if ! port_is_open "127.0.0.1" "$local_port"; then
      echo "SSH tunnel did not become ready on 127.0.0.1:${local_port}." >&2
      cleanup_jobs_tunnel
      return 1
    fi
  fi

  apply_tunnel_connection_string "$local_port"
}

prepare_job() {
  local name="$1"

  if [[ "$name" == "x-ingestion" ]]; then
    if [[ -z "${X__ClientId:-}" ]]; then
      echo "X__ClientId is not set. Add it to infrastructure/aws/.env (same OAuth 2.0 Client ID used by the API)." >&2
      return 2
    fi
  fi
  return 0
}

run_dotnet_job() {
  local name="$1"
  echo "==============================================="
  echo "Running CRS ${name} job"
  echo "==============================================="

  cd "$repo_root"
  set +e
  dotnet run --project src/Crs.Jobs -- "$name"
  local exit_code=$?
  set -e

  echo "==============================================="
  if [[ "$exit_code" -eq 0 ]]; then
    echo "${name} finished successfully."
  else
    echo "${name} FAILED with exit code ${exit_code}."
  fi
  echo "==============================================="
  return "$exit_code"
}

record_result() {
  local name="$1"
  local status="$2"
  local detail="${3:-}"
  local seconds="${4:-}"
  results+=("${name}|${status}|${detail}|${seconds}")
}

print_summary() {
  echo
  echo "==============================================="
  echo "Job summary"
  echo "==============================================="
  printf "%-14s %-10s %-8s %s\n" "JOB" "STATUS" "TIME" "NOTES"
  local row name status detail seconds time_display
  for row in "${results[@]}"; do
    IFS='|' read -r name status detail seconds <<<"$row"
    time_display="-"
    if [[ -n "$seconds" ]]; then
      time_display="${seconds}s"
    fi
    printf "%-14s %-10s %-8s %s\n" "$name" "$status" "$time_display" "$detail"
  done
  echo "==============================================="
}

now_epoch() {
  date +%s
}

run_tracked_job() {
  local name="$1"
  local started elapsed exit_code
  started="$(now_epoch)"

  set +e
  prepare_job "$name"
  local prepare_code=$?
  set -e

  if [[ "$prepare_code" -eq 2 ]]; then
    record_result "$name" "skipped" "X__ClientId is not set" ""
    return 0
  fi
  if [[ "$prepare_code" -ne 0 ]]; then
    elapsed="$(( $(now_epoch) - started ))"
    record_result "$name" "failed" "prerequisites failed" "$elapsed"
    return 1
  fi

  set +e
  run_dotnet_job "$name"
  exit_code=$?
  set -e
  elapsed="$(( $(now_epoch) - started ))"

  if [[ "$exit_code" -eq 0 ]]; then
    record_result "$name" "succeeded" "" "$elapsed"
    return 0
  fi

  record_result "$name" "failed" "exit ${exit_code}" "$elapsed"
  return 1
}

run_pipeline() {
  local ingestion_ok=0
  local had_failure=0

  echo "Starting CRS daily pipeline"
  echo "x-ingestion runs independently; feed runs only after ingestion succeeds."
  echo

  if [[ "$skip_x" -eq 1 ]]; then
    record_result "x-ingestion" "skipped" "--skip-x" ""
  else
    if ! run_tracked_job "x-ingestion"; then
      had_failure=1
    fi
  fi

  if [[ "$skip_ingestion" -eq 1 ]]; then
    record_result "ingestion" "skipped" "--skip-ingestion" ""
    record_result "feed" "skipped" "ingestion was skipped" ""
  else
    if run_tracked_job "ingestion"; then
      ingestion_ok=1
    else
      had_failure=1
    fi

    if [[ "$ingestion_ok" -eq 1 ]]; then
      if ! run_tracked_job "feed"; then
        had_failure=1
      fi
    else
      record_result "feed" "skipped" "ingestion did not succeed" ""
    fi
  fi

  print_summary

  if [[ "$had_failure" -eq 1 ]]; then
    exit 1
  fi
  exit 0
}

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet is not installed or not on PATH." >&2
  exit 1
fi

trap cleanup_jobs_tunnel EXIT INT TERM

load_secrets

export DOTNET_ENVIRONMENT="${DOTNET_ENVIRONMENT:-Production}"
export Observability__Environment="${Observability__Environment:-dev}"
export Observability__ExecutionEnvironment="${Observability__ExecutionEnvironment:-local}"
export Observability__ServiceName="${Observability__ServiceName:-crs-jobs}"

start_jobs_tunnel

if [[ "$mode" == "pipeline" ]]; then
  run_pipeline
fi

set +e
prepare_job "$job_name"
prepare_code=$?
set -e
if [[ "$prepare_code" -ne 0 ]]; then
  exit 1
fi
run_dotnet_job "$job_name"
exit $?
