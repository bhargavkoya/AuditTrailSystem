# 0010: One trace across the outbox and the broker; metrics, logs and readiness around it

Status: accepted (Phase 4).

## Context
Before Phase 4 only the correlation id crossed the message bus, so every consumer started a new trace: a submit could be followed through
the HTTP calls but not through the events it caused. The platform needed to be demoable without an Azure account, and cheap enough to leave
on under load.

## Decision
- **W3C trace context is stored in the outbox row** (`TraceParent`, `TraceState`, migration `0003`), copied from `Activity.Current` when the
  event is enqueued. The dispatcher starts a `publish <Event>` span as a child of that context, the Service Bus message carries its id as the
  `traceparent` application property, and the subscriber starts `consume <Event>` as its child. Result: one trace from the browser click
  through every asynchronous hop. A missing or malformed header simply starts a new trace.
  *Rejected:* the Azure SDK's experimental Service Bus activity source. It needs no schema change, but publishing happens in the background
  dispatcher, so the parent would be the dispatcher and not the request that caused the event.
- **One exporter choice for traces, metrics and logs:** Application Insights when its connection string is set, else OTLP when
  `OTEL_EXPORTER_OTLP_ENDPOINT` is set, else nothing. Locally the OTLP target is the .NET Aspire dashboard container behind the compose
  profile `observability` (`./infra/local/up.ps1 -Observability`), so no Azure account is needed.
- **A single `AuditFlow` meter** (outbox backlog, publish failures, consumed / failed / duplicate events, 409s, SSE connections, resyncs,
  lifecycle transitions, blocked submits) plus the built-in `System.Runtime` meter. Request spans carry `auditflow.engagement_id` and
  `auditflow.actor_id`; log scopes carry `CorrelationId`, `EngagementId`, `EventType`, `MessageId`.
- **Readiness reflects what the service needs:** SQL Server for all, the broker (a peek on the service's own subscription) for consumers, and
  the event-log tailer's last poll age (stale after 10 s) for notification-service.
- **Fail fast on configuration:** outside Development, service base URLs and `Auth:MetadataAddress` must be set, and handlers registered
  without a subscription name stop the host instead of silently never running.
- **Log volume is a feature, not noise:** the framework's error log for exceptions that our handler turns into expected 4xx responses, and the
  four Information lines per outgoing HttpClient call, are filtered. They cost real CPU under load and the spans already say the same.

## Consequences
- Every publishing database gained two nullable columns; no index, and rows are ignored once published.
- Telemetry is lost when the local dashboard container stops (in memory by design). Application Insights is the durable option.
- The traceparent in the outbox row is the request's trace even if the dispatcher publishes minutes later (for example after an outage),
  so a retried event still joins the original trace.
- The dispatcher publishes each poll as one broker batch (see the load-test results), so a `publish` span measures preparing and sending
  that batch, not a single message round trip.
