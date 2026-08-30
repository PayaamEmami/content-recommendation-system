#!/usr/bin/env bash
# Local Crs.Jobs runner. Loads infrastructure/aws/secrets.env.
# Works on macOS, Linux, and Windows Git Bash / WSL.
#
# Default (no args): daily pipeline — x-ingestion is independent of source
# ingestion/feed; feed runs only after ingestion succeeds.
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

Single jobs:
  ingestion     Pull content from configured sources
  x-ingestion   Sync posts from connected X accounts
  feed          Generate personalized recommendation feeds
  reindex       Rebuild embeddings and reindex content
  sync-index    Reconcile the local vector index with the database
EOF
}

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
secrets_file="$repo_root/infrastructure/aws/secrets.env"

mode="pipeline"
job_name=""
skip_x=0
skip_ingestion=0
results=()

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
    ingestion|x-ingestion|feed|reindex|sync-index)
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

is_windows() {
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) return 0 ;;
  esac
  [[ -n "${WINDIR:-}" ]]
}

is_local_opensearch() {
  local endpoint="${OpenSearch__Endpoint:-http://localhost:9200}"
  endpoint="${endpoint%/}"
  [[ "$endpoint" =~ ^https?://(localhost|127\.0\.0\.1)(:[0-9]+)?$ ]]
}

start_docker_desktop() {
  if [[ "$(uname -s)" == "Darwin" ]]; then
    open -a Docker
    return 0
  fi

  if is_windows; then
    local docker_desktop="/c/Program Files/Docker/Docker/Docker Desktop.exe"
    if [[ -f "$docker_desktop" ]]; then
      "$docker_desktop" &
      return 0
    fi
  fi

  echo "Docker is not running. Start Docker Desktop and retry." >&2
  return 1
}

ensure_docker() {
  if docker info >/dev/null 2>&1; then
    echo "Docker is already running"
    return 0
  fi

  echo "Docker is not running. Opening Docker Desktop..."
  start_docker_desktop || return 1

  local timeout=120 elapsed=0
  while (( elapsed < timeout )); do
    if docker info >/dev/null 2>&1; then
      echo "Docker is ready"
      return 0
    fi
    sleep 5
    elapsed=$((elapsed + 5))
    echo "Waiting for Docker (${elapsed}s/${timeout}s)"
  done

  echo "Docker did not become ready within ${timeout}s." >&2
  return 1
}

wait_for_opensearch() {
  local endpoint="${OpenSearch__Endpoint:-http://localhost:9200}"
  endpoint="${endpoint%/}"
  local health_uri="${endpoint}/_cluster/health"

  echo "Checking OpenSearch at $endpoint"

  if is_local_opensearch; then
    local status
    status="$(docker ps --filter "name=crs-opensearch" --format "{{.Status}}" 2>/dev/null || true)"
    if [[ "$status" != Up* ]]; then
      echo "Starting local OpenSearch container"
      docker compose -f "$repo_root/docker-compose.yml" up -d opensearch
    else
      echo "OpenSearch container is already running"
    fi
  else
    echo "Using remote OpenSearch; skipping local Docker OpenSearch"
  fi

  local timeout=120 elapsed=0
  while (( elapsed < timeout )); do
    if curl -fsS --max-time 5 "$health_uri" 2>/dev/null | grep -Eq '"status"[[:space:]]*:[[:space:]]*"(green|yellow)"'; then
      echo "OpenSearch is healthy"
      return 0
    fi
    sleep 5
    elapsed=$((elapsed + 5))
    echo "Waiting for OpenSearch (${elapsed}s/${timeout}s)"
  done

  echo "OpenSearch did not become healthy within ${timeout}s at $endpoint." >&2
  return 1
}

prepare_job() {
  local name="$1"

  if [[ "$name" == "x-ingestion" ]]; then
    echo "Skipping Docker and OpenSearch prerequisites for x-ingestion"
    if [[ -z "${X__ClientId:-}" ]]; then
      echo "X__ClientId is not set. Add it to infrastructure/aws/secrets.env (same OAuth 2.0 Client ID used by the API)." >&2
      return 2
    fi
    return 0
  fi

  if is_local_opensearch; then
    ensure_docker || return 1
  else
    echo "Skipping local Docker startup because OpenSearch endpoint is remote"
  fi
  wait_for_opensearch
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

load_secrets

export DOTNET_ENVIRONMENT="${DOTNET_ENVIRONMENT:-Production}"
export Observability__Environment="${Observability__Environment:-dev}"
export Observability__ExecutionEnvironment="${Observability__ExecutionEnvironment:-local}"
export Observability__ServiceName="${Observability__ServiceName:-crs-jobs}"

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
