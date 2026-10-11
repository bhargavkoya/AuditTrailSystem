# Phase 4: Hardening (observability, 409 UX, permission audit, load check, CI): detailed plan

Status: **proposed, awaiting approval**. Builds on `implementation-plan.md` section 12 and the delivered Phases 1-3.
Branch: `feature/phase-4-hardening` (cut from `main` after Phase 3's PR #4 merged).

## 1. Goal and demo

> Trace one submit across every service by its correlation id, force a 409 and resolve it field by field, prove with a script that
> no role can do more than the matrix allows, show that ~100 concurrent users are fine, and have CI prove all of it on every PR.

**Demo script (5 minutes)**
1. `docker compose --profile observability up` also starts a local OpenTelemetry dashboard. Submit ENG-1003 as Bob, open the dashboard: **one trace** spans gateway -> engagement -> workflow (readiness) and, through the broker, review and notification consumers (`consume EngagementSubmitted`, `consume EngagementStatusChanged`). Search the trace by the `X-Correlation-Id` shown in the browser's network tab; the same id appears in every service's logs.
2. Metrics view: outbox backlog, consumer success / failure counts, open SSE streams, 409 count, request latency per service.
3. **Force a 409:** two browsers, same section, save in the first, then the second. The second shows *who* changed it (from the live event) and, per conflicting field, **your value vs theirs** with *Take theirs* / *Keep mine*; saving again succeeds against the new version. Same polish on edit-details, submit and the reviewer decision.
4. `./infra/local/permission-audit.ps1` prints a role x status x endpoint grid (about 150 checks) with expected vs actual codes: all green; deliberately break a policy to see it go red.
5. `./infra/local/load-test.ps1`: 100 virtual users (reads, saves on separate engagements, uploads, 100 open SSE streams) for 2 minutes; the report shows p50 / p95 / p99 per operation, error rate, 409 count and SSE delivery latency against stated targets.
6. Open the PR: GitHub Actions runs build, unit tests, frontend build, Docker integration tests and a compose smoke test.

**Out of scope:** real Azure provisioning (Bicep / Terraform), Azure Functions (skipped), Key Vault beyond the existing abstraction, browser E2E automation (you verify the UI), rule-editing UI, document history (non-goals), multi-instance SSE backplane.

## 2. How the pieces fit

```
browser (traceparent + X-Correlation-Id) -> gateway -> service A --HTTP (traceparent + correlation id)--> service B
                                                          |
                                                outbox row stores traceparent + correlation id
                                                          v
                                          dispatcher publishes -> Service Bus message properties
                                                          v
                                       consumer: "consume X" span is a CHILD of the publishing span
all services --OTLP--> local dashboard (default)      or      Application Insights (APPLICATIONINSIGHTS_CONNECTION_STRING)
```

## 3. Deliverables by area

### 3.1 Observability (the main item)
- **Trace propagation through the broker (the gap today).** Only the correlation id crosses the bus now, so every consumer starts a new trace.
  Add `TraceParent` / `TraceState` columns to `OutboxMessage` (migration `0003`), filled from `Activity.Current` in `SqlOutbox.EnqueueAsync`
  (the request span at write time), copied by `OutboxDispatcher` into `ServiceBusMessage.ApplicationProperties`, and read by the subscriber host
  to start `consume <EventType>` with that parent (`ActivityContext.TryParse`). Result: one trace from the browser click through every async hop.
- **Spans and tags:** `auditflow.engagement_id`, `auditflow.event_type`, `auditflow.cycle`, `auditflow.actor_id` on request, outbox publish and consume spans;
  SQL calls via `AddSqlClientInstrumentation`; Dapper stays plain (the SQL client instrumentation sees it).
- **Metrics** (`Meter "AuditFlow"`): `auditflow.outbox.pending` (observable gauge), `auditflow.outbox.publish_failures`, `auditflow.events.consumed` / `.failed` / `.duplicates`
  (by event type), `auditflow.concurrency.conflicts` (by resource), `auditflow.sse.connections` (observable gauge), `auditflow.sse.dropped` (resync count),
  `auditflow.lifecycle.transitions` (by from / to), `auditflow.validation.blocked_submits`.
- **Logs:** OpenTelemetry logging provider (same exporter choice as traces), console formatter with scopes (`CorrelationId`, `EngagementId`) so a grep by id
  works without a backend. Exceptions caught by the global handler are logged once with the trace id.
- **Exporters (unchanged order):** Application Insights -> OTLP -> console. New: a compose profile `observability` with the **.NET Aspire dashboard**
  container (OTLP on 4317, UI on 18888) so traces, logs and metrics are viewable with no Azure account; services get `OTEL_EXPORTER_OTLP_ENDPOINT` when the profile is on.
- **Health:** readiness checks include the broker (peek on the subscription) and, for notification-service, the tailer's last poll age (stale > 10 s = unhealthy).
- **Key Vault:** abstraction already exists (`SecretProvider`); this phase adds a documented env-var mapping and a test that the provider order is env -> user secrets -> Key Vault. No Azure resources are created.

### 3.2 409 UX polish (frontend, backend unchanged except the payload noted)
- Shared `ConflictBanner` + `useConflictResolution` in `frontend/shared`, used by section / tab save, edit-details, submit and the reviewer decision.
- **Who and what:** the live event already names the actor; the banner says "Alice changed *Source data* 12 s ago".
- **Field-level compare:** after a 409 the page refetches the section and shows, per field the user edited, *yours vs current*; actions *Take theirs* (drop my edit) and *Keep mine* (keep the draft, rebased onto the new version so the next save carries the new `If-Match`). Fields nobody else touched stay as is. No business decisions: the client only compares displayed values.
- 409 on edit-details refetches and shows the same compare for name / period / region / reviewer; 409 on a decision shows "already decided in this cycle" with the recorded decision; submit 409 / 403 re-reads the engagement.
- The existing "Load latest (keep my edits)" becomes the *Keep mine* path.

### 3.3 Permission audit
- `infra/local/permission-audit.ps1` (+ `tools/permission-audit/audit.mjs`, Node, no packages): logs in as the five demo users, creates throwaway engagements, drives each to every
  status through the real API (create -> edit -> submit -> reject / approve), and for every route x role x status asserts the status code from a **table-driven matrix**
  (`tools/permission-audit/matrix.json`, generated from the permissions section of the master plan and reviewed by hand).
  Covers: who can read, edit, upload, see attachments / comments, submit, comment, decide, open the review screen, open the stream, edit details, and unauthenticated / non-participant calls (401 / 403 / 404).
- Prints a grid, exits non-zero on any mismatch (CI runs it against the compose stack).
- **Route inventory test** (xUnit, per service, no Docker): enumerates `EndpointDataSource`, fails if a mapped route lacks `RequireAuthorization` and is not on an explicit allow-list (`/health*`, `/auth/login`, `/.well-known/*`, `/`).
- Fix whatever it finds, with a regression test each.

### 3.4 Load check (~100 concurrent users)
- `infra/local/load-test.ps1` -> `tools/load-test/load.mjs` (Node 20 `fetch`, no dependencies; k6 is not installed here). Scenario mix per virtual user: dashboard list, open engagement (tabs + tab + validation), save a section (own engagement, no conflicts), upload a small file,
  review screen read (reviewers), plus a deliberate contention group (10 users on one section) to measure the 409 rate. 100 users, 30 s ramp, 2 min steady.
- **100 concurrent SSE streams**, with a probe that saves a section and measures time until the event reaches the other streams (delivery latency).
- **Targets (stated up front):** reads p95 < 300 ms; writes p95 < 800 ms; SSE delivery p95 < 2 s; 5xx = 0; no thread-pool starvation (checked with `dotnet-counters`-style metrics exposed through the new meter: thread-pool queue length); outbox pending returns to 0 within 10 s of the end.
- Output: `docs/architecture/load-test-results.md` (numbers from this machine, with the caveat), and what was tuned (connection pool size, SQL indexes found missing, `MaxConcurrentCalls`).

### 3.5 CI (GitHub Actions)
- `.github/workflows/ci.yml` on pull requests and pushes to `main`:
  1. **build-test:** .NET 8, restore, build the solution, `dotnet test --filter "Category!=Integration"`, upload TRX.
  2. **frontend:** Node 20, `npm ci`, `tsc --noEmit`, `vite build`.
  3. **integration:** `dotnet test --filter "Category=Integration"` (Testcontainers works on `ubuntu-latest`).
  4. **compose-smoke:** `docker compose --profile apps up -d --build`, wait for health, run `permission-audit` and a 20-user, 30 s `load-test` smoke, collect logs on failure.
- NuGet and npm caches; concurrency group cancels superseded runs; a README badge. Branch protection is your call (not configured from here).

### 3.6 Smaller hardening items
- **Dead letters:** Service Bus `MaxDeliveryCount` is 5; add a metric and a log line when a message is abandoned for the last time, plus a short runbook (`docs/architecture/runbook.md`: where to look for stuck outbox rows, DLQ, resync storms, expired tokens).
- **Config validation at startup** (fail fast): missing connection string, signing key, base URLs.
- **Security headers / limits:** gateway request size limits, `nosniff` on the API, CORS origins from config (already), and a check that no endpoint returns stack traces (the exception handler already maps to problem details; add a test).
- **Docs:** ADR 0010 (observability design: trace propagation through the outbox), ADR 0011 (permission audit as a living contract), README sections, API / events docs where payloads change (none expected), plan deviations.

## 4. Implementation steps (small commits, build + tests after each)

| Step | Work |
|---|---|
| 4.1 | Trace context through the outbox and broker (migration, enqueue, dispatch, consume) + tests (parent/child, absent header) |
| 4.2 | Meters and spans (outbox, consumers, conflicts, SSE, transitions) + tests with `MeterListener` |
| 4.3 | OTel logs, SQL instrumentation, readiness checks (broker, tailer age), startup config validation |
| 4.4 | Compose `observability` profile (Aspire dashboard) + OTLP env wiring + trace walkthrough doc |
| 4.5 | Frontend 409 polish: shared banner, compare / take-theirs / keep-mine, other 409 call sites (type-check + build only) |
| 4.6 | Route inventory tests; fix findings |
| 4.7 | Permission audit script, matrix, grid output; fix findings |
| 4.8 | Load-test script, SSE latency probe, run, tune, results doc |
| 4.9 | GitHub Actions workflow (incl. compose smoke) |
| 4.10 | Runbook, ADRs 0010-0011, README, docs, plan deviations |

## 5. How you will verify (given at the end of the phase)
`docker compose --profile apps --profile observability up -d --build`, open http://localhost:18888, submit an engagement, follow the trace; force a 409 in two browsers; run `./infra/local/permission-audit.ps1` and `./infra/local/load-test.ps1`; `dotnet test` (unit) and `dotnet test --filter Category=Integration`; open the PR and watch the workflow.

## 6. Risks

| Risk | Mitigation |
|---|---|
| Aspire dashboard image / OTLP quirks on Windows Docker | optional profile; console exporter and log correlation remain the fallback |
| Trace context in the outbox widens a hot table | two nullable short columns, no index; rows are already deleted/ignored after publish |
| Permission matrix drifts from policy | audit generated from one table, reviewed once, run in CI; failing audit = failing PR |
| Audit and load tests mutate data | each run creates its own engagements; the compose job uses a fresh stack |
| Load numbers on a laptop with Docker are not production numbers | stated as such; targets are about regressions and 100-user plausibility, not capacity planning |
| 409 compare UI adds client logic | strictly displays two values and moves a draft; it never decides validity or merges |
| CI flakiness (Testcontainers, emulator start-up) | health wait with timeout, retry only the compose-smoke job's startup, logs uploaded on failure |

## 7. Decisions needed (defaults in bold)
1. **Local trace/metric/log backend = the .NET Aspire dashboard container** behind a compose profile. Alternative: Jaeger + Prometheus + Grafana (more pieces, more familiar names).
2. **Trace context crosses the broker via `traceparent` stored in the outbox row** (a migration to every publishing service). Alternative: the Azure SDK's experimental ServiceBus activity source (no schema change, but publishing happens in the background dispatcher, so the parent would be the dispatcher, not the request).
3. **Load tool = a dependency-free Node script.** Alternative: k6 (nicer reports and thresholds, requires installing it locally and in CI).
4. **CI includes the Docker integration job and a compose smoke job** (slower, roughly 10-15 min). Alternative: unit + frontend only, run integration locally.
5. **Permission audit = black-box script against the live stack + per-service route inventory tests.**
6. **Submit race (edit slips between readiness and submit): keep as a documented gap.** Alternative: pass the validation-summary hash through the submit and have workflow-service reject if it moved (new internal endpoint, extra round trip).
7. **409 UX = field-level compare with *Take theirs* / *Keep mine*.** Alternative: banner only (what exists today plus the actor's name).
8. **Azure provisioning is documentation only** (env-var mapping for App Insights, Key Vault, Service Bus; no Bicep). Alternative: a minimal Bicep for the namespace, Key Vault and App Insights.
9. **`DomainEventLog` retention stays unbuilt** (documented gap), since a purge needs the `resync` interplay tested properly; say if you want it in this phase.
10. **Git flow:** commits per step on `feature/phase-4-hardening`; push and PR when you say.
