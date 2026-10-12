# Runbook

Where to look when something is wrong, in the order that usually pays off. Local commands assume the compose stack
(`./infra/local/up.ps1`, add `-Observability` for the dashboard at http://localhost:18888). SQL examples use the container name
`auditflow-sqlserver-1`; the `sa` password is `MSSQL_SA_PASSWORD` (default in `docker-compose.yml`).

## First minute

1. `http://localhost:5000/health/{auth|engagement|workflow|attachment|review|notification}` (through the gateway) or `http://localhost:500N/health`
   per service. Each reports its checks: `sqlserver`, `servicebus` (consumers) and `event-log-tailer` (notification-service).
   `/health/live` only says the process is up.
2. `docker compose --profile apps ps` and `docker compose --profile apps logs --tail 100 <service>`. Console lines are single-line with scopes;
   search a request by its id: `docker compose --profile apps logs | Select-String <X-Correlation-Id>`.
3. With the dashboard: Traces filtered by `auditflow.correlation_id` or `auditflow.engagement_id` shows the request and every event it caused.
4. A service that exits at startup names the missing setting (for example `Services__Workflow__BaseUrl`, `Auth__MetadataAddress`,
   `ServiceBus__SubscriptionName`). Outside Development these are required; see ADR 0010.

## Symptoms

### "I saved but the other screen did not update" / status is stale
Events are asynchronous (outbox, broker, consumer), so a second of lag is normal (ADR 0006). Longer means a stuck stage:

1. **Outbox backlog** (publisher side). Rows never published, per service database:
   ```sql
   SELECT TOP 20 Seq, EventType, EngagementId, Attempts, LastError, NextAttemptAt, OccurredAt
   FROM dbo.OutboxMessage WHERE PublishedAt IS NULL ORDER BY Seq;
   ```
   Metric `auditflow.outbox.pending` is the same number. `Attempts > 0` with a `LastError` means the broker is unreachable or rejecting; the dispatcher
   retries with backoff up to 30 s between attempts (100 attempts), the head row first so ordering holds. A single failing row blocks the rows
   behind it by design. A large backlog with no errors is throughput: the dispatcher sends each poll's rows (up to 50) as one broker batch.
2. **Broker.** `/health` of any consumer shows `servicebus`. In Azure, check the subscription's active and dead-letter counts. Locally the
   emulator has no UI: its management endpoint is `http://localhost:5300/health`, and `docker compose logs servicebus` shows entity errors.
3. **Consumer.** `auditflow.events.failed` by `event_type` and the error log lines `Handling <type> <id> failed (delivery n)`. A handler failure rolls
   back its transaction (including the processed-message marker), so redelivery retries cleanly. `auditflow.events.duplicates` rising is
   harmless redelivery.
4. **Access cache.** After a status change, other services learn the new status from `EngagementStatusChanged` (cache evicted). If a user is
   refused for up to 30 s after a transition, that is the cache TTL as a fallback.

### Dead-lettered messages
Subscriptions use `MaxDeliveryCount = 5` (`infra/local/servicebus/Config.json`; keep `ServiceBus:MaxDeliveryCount` in sync). On the delivery that
exhausts the retries the consumer logs `... failed on its last delivery (5 of 5); the broker will dead-letter it` and increments
`auditflow.events.dead_lettered`. Fix the cause (usually a handler bug or a payload it cannot read), then replay.

- Azure: Service Bus Explorer on the subscription's `$DeadLetterQueue`, resubmit the message (or re-publish it from the outbox row).
- Local emulator: read the dead-letter sub-queue with the SDK
  (`client.CreateReceiver(topic, subscription, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter })`), or simply re-seed.
- Because consumers are idempotent by message id, resubmitting is safe even if part of the work had already happened.

### Browsers keep refetching everything ("resync")
`auditflow.sse.dropped` counts `resync` frames sent. Causes: a client reconnected after missing more than `Sse:ReplayCap` (500) events, or a slow
client overflowed its bounded buffer (`Sse:ChannelCapacity`, 256). A storm of them usually means the event log is growing faster than clients read
(a bulk operation) or the notification-service is CPU starved. Check `auditflow.sse.connections`, the notification-service CPU, and that the
tailer is healthy (`event-log-tailer` check; stale after 10 s).

`DomainEventLog` is never purged in this POC (documented gap); a purge must keep the `resync` behaviour for clients that are behind it.

### 401 from everything / "session expired"
Tokens last `Auth:TokenMinutes` (60). The stream sends `event: reauth` shortly before expiry and the UI signs out on a 401. If every call fails
with 401 right after a restart, the signing key changed: the key is stored in the auth database (or supplied as `Secrets:Auth:SigningKeyPem` /
Key Vault), and the other services read the public key from `/.well-known/jwks.json` of `Auth:MetadataAddress`. Check that URL from inside the
failing service's container.

### 409 on save
Expected when two people edit the same section: `auditflow.concurrency.conflicts` by `resource`. The UI shows who changed what and offers
take-theirs / keep-mine. A 409 on submit means the engagement moved (for example already Under Review); the page re-reads it.

### 422 on submit
Not a failure: the backend lists the blocking validation errors. `auditflow.validation.blocked_submits` counts them.

### 502 from a service
A dependency was unreachable (`HttpRequestException` is mapped to 502 with no detail). The log line has the target; typical cause is
workflow-service or engagement-service restarting. Callers use 5 s timeouts and do not retry.

### Slow under load
Run `./infra/local/load-test.ps1 -Smoke` for a quick signal. See [load-test-results.md](load-test-results.md) for the reference numbers and what was
tuned. The first things to read: `auditflow.outbox.pending` (is publishing keeping up), per-container CPU (`docker stats`), and the span durations
of the slow route in the dashboard.

## Configuration (environment variables)

| Setting | Env var | Notes |
|---|---|---|
| SQL connection | `ConnectionStrings__Default` | required; one database per service |
| Service Bus | `ServiceBus__ConnectionString`, `__TopicName`, `__SubscriptionName` | empty connection string = in-memory bus (nothing delivered); handlers without a subscription stop the host |
| Peer services | `Services__Engagement__BaseUrl`, `Services__Workflow__BaseUrl`, `Services__Auth__BaseUrl` | required outside Development |
| Token validation | `Auth__MetadataAddress` | required outside Development |
| Telemetry | `APPLICATIONINSIGHTS_CONNECTION_STRING` (wins) or `OTEL_EXPORTER_OTLP_ENDPOINT` | `AuditFlow__Telemetry__Console=true` adds console traces |
| Key Vault | `KeyVault__Uri` | Key Vault is asked first (`Auth:SigningKeyPem` becomes `Auth-SigningKeyPem`); configuration (`Secrets__Auth__SigningKeyPem`, user-secrets) is the fallback when the vault has no such secret. A vault failure other than "not found" is raised, not hidden |
| SSE | `Sse__HeartbeatSeconds`, `__ReplayCap`, `__ChannelCapacity`, `__TailerPollMs` | defaults in `SseOptions` |
| Outbox | `Messaging__OutboxPollMs` | idle poll interval (500 ms) |

## Azure deployment checklist (documentation only, nothing is provisioned from this repo)

- One App Service / Container App per service plus the gateway; only the gateway is public. `/internal/*` is protected only by the gateway not
  routing it, so keep the services on a private network or add service-to-service credentials before exposing anything else.
- Azure SQL (one database per service), Service Bus Standard (topic `auditflow-events` and the five subscriptions from `Config.json` with the
  same filters and `MaxDeliveryCount`), Key Vault, Application Insights. Use managed identities instead of connection strings where the SDK allows.
- Replace the local auth-service issuer with an identity provider (change `Auth__MetadataAddress` and the issuer / audience settings).
- SSE needs a single notification-service instance (the hub is in memory) or a backplane, and a proxy that does not buffer responses and keeps
  idle connections longer than the 15 s heartbeat.
- Do not ship the demo users, their password, or the SQL `sa` password from the compose file.
