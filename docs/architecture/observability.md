# Observability walkthrough

How to follow one request across every service, locally and in Azure. Design rationale: ADR 0010 (added at the end of Phase 4).

## What is emitted

| Signal | Source | Notes |
|---|---|---|
| Traces | ASP.NET Core, HttpClient, SqlClient (Dapper's commands), `AuditFlow.*` activity sources | `publish <Event>` and `consume <Event>` spans link the async hops |
| Metrics | `AuditFlow` meter, ASP.NET Core, HttpClient, `System.Runtime` | list below |
| Logs | OpenTelemetry logging provider + single-line console with scopes | each record carries the trace id; scopes add `CorrelationId`, `EngagementId`, `EventType`, `MessageId` |

Exporter choice is the same for all three: `APPLICATIONINSIGHTS_CONNECTION_STRING` (Azure Monitor), else `OTEL_EXPORTER_OTLP_ENDPOINT` (OTLP), else nothing is exported
(the console still shows logs; `AuditFlow__Telemetry__Console=true` adds console traces).

### Custom metrics (`AuditFlow` meter)

| Metric | Type | Tags |
|---|---|---|
| `auditflow.outbox.pending` | gauge | unpublished outbox rows at the last dispatcher poll |
| `auditflow.outbox.publish_failures` | counter | |
| `auditflow.events.consumed` / `.failed` / `.duplicates` | counter | `event_type` |
| `auditflow.concurrency.conflicts` | counter | `resource` (section key, `engagement`, `decision`) |
| `auditflow.sse.connections` | gauge | open streams (notification-service) |
| `auditflow.sse.dropped` | counter | clients told to `resync` |
| `auditflow.lifecycle.transitions` | counter | `from`, `to` |
| `auditflow.validation.blocked_submits` | counter | |

## Run it locally (no Azure account)

```powershell
./infra/local/up.ps1 -Observability      # same stack + the Aspire dashboard, services pointed at it
# UI: http://localhost:18888   (OTLP/gRPC from the host: localhost:4317)
./infra/local/down.ps1                   # stops everything, dashboard included
```

Equivalent by hand: `$env:OTEL_EXPORTER_OTLP_ENDPOINT='http://otel-dashboard:18889'; docker compose --profile apps --profile observability up -d --build`.
The dashboard is unsecured and in memory (local development only); restarting it clears the data.

## Trace one submit

1. Sign in as Bob (`bob` / `Passw0rd!`), open an engagement that is In Progress and passes validation, and press **Submit**. The seeded
   ENG-1003 is already Under Review and ENG-1002 has blocking validation errors, so fill in a new engagement (or rework ENG-1005) first.
2. In the browser's network tab, copy the `X-Correlation-Id` response header of `POST /engagements/{id}/submit`.
3. Dashboard -> **Traces** -> filter `auditflow.correlation_id = <id>` (or by `auditflow.engagement_id`). One trace shows:

```
gateway                POST /engagements/{id}/submit
 engagement-service      POST /engagements/{id}/submit            auditflow.engagement_id, auditflow.actor_id
  workflow-service         GET  /internal/engagements/{id}/submission-readiness
   (SQL spans)
  (SQL spans: transition + outbox insert)
  engagement-service     publish EngagementSubmitted               <- background dispatcher, child of the request
   review-service          consume EngagementSubmitted
   notification-service    consume EngagementSubmitted
  engagement-service     publish EngagementStatusChanged
   notification-service    consume EngagementStatusChanged
   workflow/attachment/review  consume EngagementStatusChanged     (access-cache eviction)
```

The `publish` spans run after the request has returned, so they appear later in the same trace. That is the outbox working as designed: the event is
published only after the transaction commits, and the stored `traceparent` keeps it attached to the request that caused it.

4. **Structured logs** -> filter by the same `CorrelationId`: every service's lines for this submit, each linking back to its span.

Without a dashboard: `docker compose --profile apps logs | Select-String <correlation-id>` finds the same lines (the console format includes scopes).

## Metrics worth looking at during the demo

- `auditflow.outbox.pending` returns to 0 within a poll interval after any write.
- `auditflow.events.consumed` by `event_type` rises with activity; `.failed` and `.duplicates` stay near zero (duplicates appear after a broker redelivery).
- `auditflow.concurrency.conflicts` increments when two browsers save the same section.
- `auditflow.sse.connections` equals the number of open engagement pages.

## Readiness

`/health` runs every check; `/health/live` runs none.

| Check | Services | Unhealthy when |
|---|---|---|
| `sqlserver` | all | the database does not answer `SELECT 1` |
| `servicebus` | services that consume | a peek on the service's own subscription fails or takes more than 5 s |
| `event-log-tailer` | notification-service | no poll for more than 10 s |

## In Azure (not provisioned by this repo)

Set `APPLICATIONINSIGHTS_CONNECTION_STRING` on every service (and the gateway). Traces, metrics and logs flow to the same Application Insights resource, so
the transaction view shows the same tree as above. Secrets (signing key, connection strings) resolve env var -> user secrets -> Key Vault (`KeyVault__Uri`).

## Status

Written in step 4.4 without a running Docker engine: the compose file parses (`docker compose config`), but the dashboard container, the OTLP
wiring and the trace tree above have **not been observed live yet**. Re-check this page against a real run before the demo.
