# AuditFlow (POC)

Backend-controlled audit engagement management platform: a demoable microservices skeleton.
Spec: [docs/PRD.md](docs/PRD.md) | Plans: [implementation](docs/architecture/implementation-plan.md), [Phase 1](docs/architecture/phase-1-plan.md), [Phase 2](docs/architecture/phase-2-plan.md) |
Decisions: [docs/decisions](docs/decisions) | API: [docs/api](docs/api/README.md) | Events: [contracts/events](contracts/events/README.md) |
Conventions: [CLAUDE.md](CLAUDE.md)

## Status
- **Phase 0** (scaffold): done.
- **Phase 1** (foundation): login and roles, role-scoped dashboard, engagement creation, outbox + Service Bus events,
  notification-service event log.
- **Phase 2** (workflow core): five tabs driven by a JSON rule set (Tab 1 shows or hides sections and changes what is required),
  validation banners (Error / Warning / Information), attachments at section / tab / engagement level (latest file only),
  reconciliation variances, optimistic concurrency (409), event-driven Draft / Rework -> In Progress.
- Next: Phase 3 (submit, review, SSE), Phase 4 (hardening).

## Prerequisites
.NET 8 SDK (`global.json` pins 8.0.x), Docker Desktop, Node 20+.

## Run
```powershell
# everything in Docker: SQL Server, Service Bus emulator, gateway, 6 services (first run builds images)
./infra/local/up.ps1

# frontend (npm workspace rooted at frontend/)
cd frontend; npm install; cd apps/web; npm run dev     # http://localhost:5173
```
Infrastructure only: `docker compose up -d`. Stop: `./infra/local/down.ps1` (`-Volumes` wipes all data and re-seeds on the next start).

### Try it
Sign in at http://localhost:5173 with a demo user (password `Passw0rd!`):

| User | Role | What to notice |
|---|---|---|
| alice@auditflow.test | Auditor | sees all 5 seeded engagements, edits all five tabs, "New engagement" button |
| bob@auditflow.test | Co-Auditor | same permissions as Alice |
| rachel@auditflow.test | Reviewer | "Reviewing" filter; "Review" action only on the Under Review row |
| rohan@auditflow.test | Reviewer | reviews a different set of engagements |
| victor@auditflow.test | Viewer | no create button; read-only tabs and **no attachment UI** |

**Five-minute walkthrough** (as Alice, open **ENG-1002**):
1. Five tabs with issue badges; the banners at the top list what is missing, click one to jump to the field.
2. Tab 1: untick *Derivatives* and save: the Derivatives sections vanish from Tabs 2 and 3 and their errors disappear; tick it again and they return with their values.
3. Tab 2: *Derivatives positions* -> book 1,000,000 and ledger 1,080,000; Tab 5 shows a *Significant variance* and the banner blocks readiness.
4. Attach a file to a section, to Tab 3 and to the engagement; upload another at the same scope: only the latest is shown.
5. Open the same section in two browsers, save in one, then the other: "Someone else changed ...", and your unsaved text is kept.
6. As Victor (Viewer): read-only, no attachments. As Rachel (Reviewer): read-only. **ENG-1003** (Under Review) is locked for Alice.
7. The first edit on **ENG-1001** (Draft) turns it *In Progress* a moment later (event-driven); editing **ENG-1005** (Rejected) reopens it.

Create an engagement (it appears with "Setting up the workflow..." for a second), then watch the event arrive: `GET http://localhost:5006/dev/events/recent` (use a token from
`POST /auth/login`), or open the Swagger pages (`http://localhost:5001/swagger` ... `5006`).

## Ports
| Component | Port |
|---|---|
| gateway (YARP) | 5000 |
| auth / engagement / workflow / attachment / review / notification | 5001 to 5006 |
| SQL Server | 1433 |
| Service Bus emulator (AMQP / management) | 5672 / 5300 |

Each service: `/health`, `/health/live`, `/swagger`. The gateway proxies `/health/{service}`.

## Layout
```
frontend/{apps/web, features/{dashboard,engagement,review}, shared}   services/{auth,engagement,workflow,attachment,review,notification}-service
services/gateway   services/building-blocks   contracts/{api,events}   infra/{local,docker,azure}   docs   tests
```

## Tests
```powershell
dotnet build AuditFlow.sln
dotnet test AuditFlow.sln                                   # everything (integration tests need Docker)
dotnet test AuditFlow.sln --filter "Category!=Integration"  # fast unit tests only
```
Unit tests cover the state machine (every invalid transition), the permission matrix, the create and update rules, login and tokens,
the idempotent consumer pipeline, the access cache, and the whole workflow domain (every rule operator, rule-driven visibility,
validation at field / section / tab level, reconciliation thresholds, value parsing, catalogue integrity, upload rules).
Integration tests (`Category=Integration`) start a SQL Server container with Testcontainers and cover outbox atomicity, ordering and
backoff, duplicate delivery, rollback, the engagement queries, workflow provisioning (idempotent), section saves with 409 and parallel
writers, atomic tab saves, Tab 1 driven visibility with events, the workflow consumers, attachment replace / permissions / validation,
and the engagement status transitions.

## Telemetry
OpenTelemetry in every service. `APPLICATIONINSIGHTS_CONNECTION_STRING` -> Application Insights; `OTEL_EXPORTER_OTLP_ENDPOINT` -> OTLP;
otherwise set `AuditFlow__Telemetry__Console=true` for console traces. The correlation id (`X-Correlation-Id`) flows through
HTTP, outbox rows, Service Bus messages and handler logs.

## Dev-only settings
The SQL password in compose and appsettings, the demo users and their password, and the database-stored signing key are local-dev
defaults. Override `MSSQL_SA_PASSWORD`; supply `Secrets:Auth:SigningKeyPem` (or `KeyVault:Uri`) to take the key out of the database.
