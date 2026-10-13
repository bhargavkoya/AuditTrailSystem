# Running AuditFlow locally

Step-by-step local setup. Commands are given for **cmd** first (no PowerShell needed); the `.ps1` scripts in `infra/local` only wrap
the same `docker compose` calls. For what to click once it is running, see the walkthroughs in the [README](../../README.md).

## Prerequisites
- .NET 8 SDK (`global.json` pins 8.0.x), needed only to run tests
- Docker Desktop, running (the first build needs a few GB free)
- Node 20+

## 1. Start the backend
```cmd
cd <repo root>
docker compose --profile apps up -d --build
```
Starts SQL Server, the Service Bus emulator, the YARP gateway and the six services. The first run builds the images and takes several minutes.

With the telemetry dashboard (http://localhost:18888):
```cmd
set OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-dashboard:18889
docker compose --profile apps --profile observability up -d --build
```
Infrastructure only (SQL Server + Service Bus emulator, to run services from your IDE): `docker compose up -d`.

## 2. Check it is healthy
```cmd
docker compose --profile apps ps
curl http://localhost:5000/health/auth
```
Allow 30 to 60 seconds after startup. `/health/{auth|engagement|workflow|attachment|review|notification}` through the gateway should each report `Healthy`.

## 3. Start the frontend
In a second terminal:
```cmd
cd frontend
npm install
cd apps\web
npm run dev
```
Open http://localhost:5173 and sign in with a demo user (password `Passw0rd!`): `alice@auditflow.test` (Auditor), `bob@auditflow.test` (Co-Auditor),
`rachel@auditflow.test` / `rohan@auditflow.test` (Reviewers), `victor@auditflow.test` (Viewer).

## 4. Stop and reset
```cmd
docker compose --profile apps --profile observability down        :: stop, keep data
docker compose --profile apps --profile observability down -v     :: stop and wipe all data; re-seeds on the next start
```
Run the `-v` form once after pulling a phase that adds databases or broker subscriptions.

## 5. Tests
```cmd
dotnet test AuditFlow.sln --filter "Category!=Integration"   :: fast, no Docker
dotnet test AuditFlow.sln                                    :: also runs Testcontainers integration tests (needs Docker)
```
Against the running stack (Node 20+; PowerShell scripts): `infra\local\permission-audit.ps1`, `infra\local\load-test.ps1 -Smoke`.

## Ports
| Component | Port |
|---|---|
| gateway | 5000 |
| auth / engagement / workflow / attachment / review / notification (each has `/swagger`, `/health`) | 5001 to 5006 |
| SQL Server | 1433 |
| Service Bus emulator (AMQP / management) | 5672 / 5300 |
| OpenTelemetry dashboard (UI / OTLP), observability profile only | 18888 / 4317 |

## Troubleshooting

### Build fails with `read-only file system` (for example `/var/lib/docker/buildkit/.../metadata_v2.db`)
Docker's own WSL2 disk went read-only (full disk, I/O error or interrupted write); it is not a project problem. In order:
1. Quit Docker Desktop, run `wsl --shutdown`, start Docker Desktop, wait for "Engine running", retry the build.
2. `docker system df`, then `docker builder prune -af` and `docker system prune -f`; also check free space on `C:`.
3. Last resort: Docker Desktop, Settings, Troubleshoot, Clean / Purge data (deletes all local images and volumes, not just AuditFlow's).

### PowerShell scripts fail with execution-policy or parsing errors
You do not need them to run the app; use the cmd commands above. If you want a script anyway:
`powershell -NoProfile -ExecutionPolicy Bypass -File infra\local\up.ps1`.

### A service exits at startup
Its log names the missing setting: `docker compose --profile apps logs --tail 100 <service>`.

### Everything returns 401 after a restart
The signing key changed. Run the `down -v` command and start again.

### The UI looks stale
Updates are event-driven (outbox, broker, consumer), so a second of lag is normal. More lag: see the [runbook](runbook.md).
