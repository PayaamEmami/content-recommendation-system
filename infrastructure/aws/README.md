# CRS AWS Infrastructure

Single place for CRS cloud ops: Lightsail API runtime, ECR image builds, and S3/CloudFront web deploy.

## Architecture

| Piece | Resource |
|-------|----------|
| API + Postgres (pgvector) + Caddy | Lightsail instance `crs-lightsail` + static IP `crs-lightsail-ip` |
| API image | ECR `crs-api` |
| Web (Blazor WASM) | S3 `crs-web-{account}` + CloudFront |
| Jobs | Local `scripts/run-job.sh` → Lightsail Postgres |
| MCP | Lambda `crs-mcp-server` Function URL (us-west-2) wrapping Crs.Api REST |

Region: **us-west-2**. All resources use the `crs-` prefix.

## Prerequisites

- AWS CLI configured
- Docker (for image builds)
- .NET 10 SDK (for web publish)
- SSH key at `~/.ssh/crs-lightsail-key.pem` (existing Lightsail key pair)

The Lightsail instance, static IP, and key pair are already created. Recreate them in the Lightsail console / AWS CLI if needed.

## Day-to-day

### First-time host setup (or after wiping `/opt/crs`)

```bash
cd infrastructure/aws
cp lightsail.env.example .env
# fill DB_PASSWORD, OpenAI__ApiKey, JWT_SECRET, CRS_API_IMAGE, CORS origins

./build-and-push.sh --skip-lightsail   # if ECR has no image yet
./deploy-lightsail.sh
./deploy-web.sh                        # defaults to Lightsail static IP → *.sslip.io
```

### Update API image on Lightsail

```bash
cd infrastructure/aws
./build-and-push.sh          # build/push ECR + redeploy Lightsail
# or
./build-and-push.sh --skip-lightsail
./deploy-lightsail.sh
```

### Publish web

```bash
./deploy-web.sh
```

Resolve the static IP with:

```bash
aws lightsail get-static-ip --static-ip-name crs-lightsail-ip --region us-west-2 --query 'staticIp.ipAddress' --output text
```

### Local jobs

Copy `secrets.env.example` → `secrets.env` (gitignored) and point at Lightsail:

```bash
ConnectionStrings__DefaultConnection=Host=<crs-lightsail-ip>;Database=crsdb;Username=crsadmin;Password=...
```

```bash
./scripts/run-job.sh
```

When your public IP changes (CGNAT/mobile), reopen Lightsail port **5432** for that CIDR or jobs cannot connect.

## pgvector cutover

After deploying this stack (Postgres image `pgvector/pgvector:pg15`, no OpenSearch container):

1. Deploy compose **without** `docker compose down -v`. The named volume `postgres_data` must be kept (Postgres 15 data dir is compatible with the pgvector pg15 image).
2. Confirm API `/health` and that `ContentEmbeddings` exists (API `MigrateAsync` on startup).
3. From the job host, backfill vectors then regenerate feeds:

```bash
./scripts/run-job.sh reindex
./scripts/run-job.sh feed
```

`reindex` calls OpenAI for the current corpus.
4. Close Lightsail TCP **9200**. Leave **5432** open for jobs.
5. Stay on Lightsail `medium_3_0` until ingest + feed are proven. Do not downsize in the same change.

## MCP (assistant)

The Streamable HTTP MCP server lives in `mcp/` and is deployed separately from Lightsail:

```bash
python mcp/create_api_key.py
# then:
MCP_API_KEY_SHA256=<hash> \
CRS_API_BASE_URL=https://<crs-lightsail-ip>.sslip.io \
CRS_EMAIL=... \
CRS_PASSWORD=... \
./mcp/deploy.sh
```

Paste the Function URL and raw `cak_…` key into the assistant at `/chat/automation` as a **second** MCP connection (do not replace the task-board MCP).

`crs_ingest_source` runs the per-source API pipeline (extract, save, embed). The ranked daily feed is still produced by local `scripts/run-job.sh feed`; newly ingested items may not appear in today's feed until that job runs.

Env vars on the Lambda: `MCP_API_KEY_SHA256`, `CRS_API_BASE_URL`, `CRS_EMAIL`, `CRS_PASSWORD`. Never commit those values.

## Scripts

| Script | Purpose |
|--------|---------|
| `deploy-lightsail.sh` | Install Docker if needed; sync Compose/Caddy/`.env`; pull ECR; `compose up` |
| `build-and-push.sh` | Build/push `crs-api` + `crs-jobs` to ECR; optional Lightsail refresh |
| `deploy-web.sh` | Publish Blazor WASM to S3 + CloudFront invalidation |
| `migrate-users.sh` | Optional one-shot Users-table restore (cutover utility) |
| `docker-compose.yml` + `Caddyfile` | Runtime stack copied to `/opt/crs` on the instance |

## Firewall (Lightsail)

| Port | Access |
|------|--------|
| 80, 443 | Public (Caddy / ACME) |
| 22 | Prefer admin CIDR; may need broader access on CGNAT |
| 5432 | Admin / jobs host CIDR only |

`medium_3_0` (~$24/mo, 4 GB) is the current instance plan. Stay on it until pgvector ingest and feed generation are proven in production.

## Secrets

- **Lightsail host compose:** `infrastructure/aws/.env` (from `lightsail.env.example`) — never commit
- **Local jobs / shared bootstrap:** `infrastructure/aws/secrets.env` (from `secrets.env.example`) — never commit

## Observability (optional)

Windows job host CloudWatch agent install:

```powershell
cd infrastructure\aws\cloudwatch-agent
powershell.exe -ExecutionPolicy Bypass -File .\install-windows.ps1 -Region us-west-2
```

## Naming

`crs-lightsail`, `crs-lightsail-ip`, `crs-api` / `crs-jobs` ECR repos, `crs-web-{account}`, containers `crs-postgres`, `crs-api`, `crs-caddy`.
