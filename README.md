# AuditFlow (POC)

Backend-controlled audit engagement management platform: a demoable microservices skeleton.
Spec: [docs/PRD.md](docs/PRD.md) | Plan: [docs/architecture/implementation-plan.md](docs/architecture/implementation-plan.md) | Conventions: [CLAUDE.md](CLAUDE.md)

## Status
Phase 0 (scaffolding) only: services expose health + Swagger, no features yet.

## Prerequisites
.NET 8 SDK, Docker Desktop, Node 20+ (global.json pins SDK 8.0.x).

## Run
```powershell
# everything in Docker: SQL Server, Service Bus emulator, gateway, 6 services
./infra/local/up.ps1

# frontend
cd frontend; npm install; cd apps/web; npm run dev     # http://localhost:5173
```
Infrastructure only: `docker compose up -d`. Stop: `./infra/local/down.ps1` (`-Volumes` wipes data).

## Ports
| Component | Port |
|---|---|
| gateway (YARP) | 5000 |
| auth / engagement / workflow / attachment / review / notification | 5001 to 5006 |
| SQL Server | 1433 |
| Service Bus emulator (AMQP / mgmt) | 5672 / 5300 |

Each service: `/health`, `/health/live`, `/swagger`. The gateway proxies `/health/{service}`.

## Layout
```
frontend/{apps/web, features/*, shared}   services/{auth,engagement,workflow,attachment,review,notification}-service
services/gateway   services/building-blocks   contracts/{api,events}   infra/{local,docker,azure}   docs   tests
```

## Commands
```powershell
dotnet build AuditFlow.sln
dotnet test AuditFlow.sln
```

## Telemetry
OpenTelemetry in every service. `APPLICATIONINSIGHTS_CONNECTION_STRING` -> Application Insights; `OTEL_EXPORTER_OTLP_ENDPOINT` -> OTLP;
otherwise set `AuditFlow__Telemetry__Console=true` for console traces.

The SQL password in compose and appsettings is a local-dev default only; override with `MSSQL_SA_PASSWORD`.
