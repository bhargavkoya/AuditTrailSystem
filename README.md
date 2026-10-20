# AuditFlow (POC)

[![CI](https://github.com/bhargavkoya/AuditTrailSystem/actions/workflows/ci.yml/badge.svg)](https://github.com/bhargavkoya/AuditTrailSystem/actions/workflows/ci.yml)

Backend-controlled audit engagement management platform: a demoable microservices skeleton.
Spec: [docs/PRD.md](docs/PRD.md) | Plans: [implementation](docs/architecture/implementation-plan.md), [Phase 1](docs/architecture/phase-1-plan.md), [Phase 2](docs/architecture/phase-2-plan.md), [Phase 3](docs/architecture/phase-3-plan.md), [Phase 4](docs/architecture/phase-4-plan.md) |
Testing: [manual testing guide](docs/testing-guide.md) |
Operations: [runbook](docs/architecture/runbook.md), [observability](docs/architecture/observability.md), [load test](docs/architecture/load-test-results.md) |
Decisions: [docs/decisions](docs/decisions) | API: [docs/api](docs/api/README.md) | Events: [contracts/events](contracts/events/README.md) |
Conventions: [CLAUDE.md](CLAUDE.md)

## Status
- **Phase 0** (scaffold): done.
- **Phase 1** (foundation): login and roles, role-scoped dashboard, engagement creation, outbox + Service Bus events,
  notification-service event log.
- **Phase 2** (workflow core): five tabs driven by a JSON rule set (Tab 1 shows or hides sections and changes what is required),
  validation banners (Error / Warning / Information), attachments at section / tab / engagement level (latest file only),
  reconciliation variances, optimistic concurrency (409), event-driven Draft / Rework -> In Progress.
- **Phase 3** (review and real time): submit with a backend readiness check, the separate review screen (tab comments, approve, reject with a
  mandatory comment, decision history per review cycle), the full state machine, and SSE live updates with replay, role-filtered events and
  conflict-aware reconciliation in the UI.
- **Phase 4** (hardening): one trace from the browser through the outbox and broker to every consumer, custom metrics and structured logs
  (Application Insights, OTLP or the local Aspire dashboard), readiness checks and fail-fast configuration, a field-level "yours vs current"
  409 experience, a route inventory test plus a black-box permission audit (841 checks), a ~100-user load test, and GitHub Actions CI.
  See the status notes in [phase-4-plan.md](docs/architecture/phase-4-plan.md) (section 8) for what is verified and what is not.

## Prerequisites
.NET 8 SDK (`global.json` pins 8.0.x), Docker Desktop, Node 20+.

## Run
```cmd
:: everything in Docker: SQL Server, Service Bus emulator, gateway, 6 services (first run builds images)
docker compose --profile apps up -d --build

:: frontend (npm workspace rooted at frontend/), in a second terminal
cd frontend
npm install
cd apps\web
npm run dev
```
The app is at http://localhost:5173. Full cmd steps, ports and troubleshooting (including Docker `read-only file system` build errors) are in
[docs/architecture/local-setup.md](docs/architecture/local-setup.md).
Infrastructure only: `docker compose up -d`. Stop: `docker compose --profile apps --profile observability down` (add `-v` to wipe all data and re-seed on the next start).
The `infra/local/*.ps1` scripts do the same and are optional.

### Try it
Step-by-step manual test scenarios (UI, API, SSE, roles, concurrency) are in [docs/testing-guide.md](docs/testing-guide.md).

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

**Phase 3 walkthrough** (review and live updates; use two browser profiles):
1. As Alice open **ENG-1002** and press *Submit for review*: the backend refuses with the blocking errors listed. The engagement stays *In Progress*.
2. Open **ENG-1003** (Under Review, reviewer Rachel) as Bob in one profile and as Rachel in another. Rachel: *Go to review screen* (own colour scheme, read-only tabs, validation summary, decision history).
3. Rachel adds a comment on Tab 2; press *Reject* without text: the server answers 400 and the message is shown. Add a comment and reject: the status becomes *Rejected* on both screens without reloading.
4. Bob edits any field on ENG-1003: it returns to *In Progress*. In Alice's profile (also on ENG-1003) the page shows *"Bob changed ..."* when she has unsaved edits in that section, otherwise it refreshes only that section. Watch the **Live** dot in the header.
5. Bob presses *Submit for review* (cycle 2). Rachel approves: *Approved*, everything read-only, history shows both cycles. Alice and Bob get *View review* to read the feedback.
6. As Victor (Viewer): tabs and validation only. No attachment or comment UI, no review screen (403), and his stream never carries `AttachmentAdded` / `ReviewerCommentAdded`.
7. Raw stream: `curl -N -H "Authorization: Bearer <token>" http://localhost:5000/engagements/ENG-1003/events/stream` (add `-H "Last-Event-ID: 5"` to replay).

After pulling Phase 3 into an existing environment run `./infra/local/down.ps1 -Volumes` once: the review database and the broker subscriptions are new, and the review history for the seeded engagements is created from the seeded events.

Create an engagement (it appears with "Setting up the workflow..." for a second), or open the Swagger pages (`http://localhost:5001/swagger` ... `5006`).

## Ports
| Component | Port |
|---|---|
| gateway (YARP) | 5000 |
| auth / engagement / workflow / attachment / review / notification | 5001 to 5006 |
| SQL Server | 1433 |
| Service Bus emulator (AMQP / management) | 5672 / 5300 |
| OpenTelemetry dashboard (UI / OTLP), `-Observability` only | 18888 / 4317 |

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

Against the running stack (`./infra/local/up.ps1`; both need Node 20+):
```powershell
./infra/local/permission-audit.ps1    # every role x status x operation vs tools/permission-audit/matrix.json (about 45 s); exit 1 on a mismatch
./infra/local/load-test.ps1           # 100 users + 100 SSE streams (about 3.5 min); -Smoke for the 20-user CI version
```
`AuditFlow.Security.Tests` (no Docker) maps every service's real routes and fails if one lacks authorization and is not on an explicit allow-list.
Unit tests cover the state machine (every invalid transition), the permission matrix, the create and update rules, login and tokens,
the idempotent consumer pipeline, the access cache, and the whole workflow domain (every rule operator, rule-driven visibility,
validation at field / section / tab level, reconciliation thresholds, value parsing, catalogue integrity, upload rules).
Integration tests (`Category=Integration`) start a SQL Server container with Testcontainers and cover outbox atomicity, ordering and
backoff, duplicate delivery, rollback, the engagement queries, workflow provisioning (idempotent), section saves with 409 and parallel
writers, atomic tab saves, Tab 1 driven visibility with events, the workflow consumers, attachment replace / permissions / validation,
and the engagement status transitions. Phase 3 adds submit (422 / 403 / double submit), review-service (comments, decisions, composed
screen, degraded tabs, seed history), decisions applied from events, the full submit-reject-rework-approve lifecycle across two databases, and
the SSE hub, tailer and session (replay, resync, reauth, heartbeat, Viewer filtering) in `AuditFlow.Notification.Tests`.

## Telemetry
OpenTelemetry traces, metrics and logs in every service. `APPLICATIONINSIGHTS_CONNECTION_STRING` -> Application Insights; `OTEL_EXPORTER_OTLP_ENDPOINT` -> OTLP;
otherwise set `AuditFlow__Telemetry__Console=true` for console traces. The correlation id (`X-Correlation-Id`) flows through
HTTP, outbox rows, Service Bus messages and handler logs, and the W3C `traceparent` is stored in the outbox row so a consumer's span joins the trace of the request that caused it.

```powershell
./infra/local/up.ps1 -Observability     # also starts the Aspire dashboard: http://localhost:18888
```
See [docs/architecture/observability.md](docs/architecture/observability.md) for the metrics list, readiness checks and a trace walkthrough.

## Dev-only settings
The SQL password in compose and appsettings, the demo users and their password, and the database-stored signing key are local-dev
defaults. Override `MSSQL_SA_PASSWORD`; supply `Secrets:Auth:SigningKeyPem` (or `KeyVault:Uri`) to take the key out of the database.
