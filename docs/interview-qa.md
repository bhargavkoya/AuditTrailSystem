# AuditFlow: 30 Interview Questions and Answers

Written from the point of view of a senior architect interviewing for a backend / solution-architect role, based on this repository
(PRD, ADRs 0001-0011, phase plans, load-test results, code layout). Answers are written to be spoken in first person about this project.
Where something is **not built or not verified**, the answer says so; say the same in the interview.

Sections: [A. Architecture](#a-architecture-and-service-boundaries) | [B. Messaging](#b-messaging-and-consistency) |
[C. Concurrency](#c-concurrency-and-data) | [D. Backend-controlled UI](#d-backend-controlled-ui-rules-and-workflow) |
[E. Security](#e-security) | [F. Real time](#f-real-time-sse) | [G. Observability, testing, performance](#g-observability-testing-performance) |
[H. Judgement](#h-judgement-and-trade-offs)

---

## A. Architecture and service boundaries

### 1. Walk me through the architecture in two minutes.
Six deployable ASP.NET Core 8 services behind a YARP gateway: **auth** (JWT issuer), **engagement** (lifecycle, participants, permissions),
**workflow** (tabs, sections, rules, validation), **attachment**, **review** (comments, decisions) and **notification** (event log + SSE).
Each owns its own SQL Server database and never reads another's. Synchronous HTTP is for user-driven reads and writes; Azure Service Bus
(one topic, a subscription per consumer) carries domain events; SSE pushes change notifications to the browser. Cross-cutting concerns
(outbox, idempotent consumers, telemetry, health, secrets) live in a `BuildingBlocks` library, which is infrastructure, not a business service.

### 2. Why microservices for a POC? Isn't that over-engineering?
The PRD explicitly says "microservices only, no modular-monolith fallback", and the goal is to discuss service boundaries, messaging and
consistency fluently. I would not choose six services for a team of one in production. The honest trade-off: I get realistic failure modes
(eventual consistency, duplicate delivery, partial outages) to reason about, at the cost of operational weight. I kept each service minimal:
only the endpoints and tables its responsibility needs.

### 3. How did you decide the service boundaries?
By **who owns the invariant**, not by noun. Engagement-service owns the status and the permission matrix (one state machine). Workflow owns
field values and rule evaluation. Review owns decisions and comments. Attachment owns file metadata and bytes. Notification owns the event
log and fan-out. Test: if two services both need to change a table to enforce one rule, the boundary is wrong. Review-service, for example,
cannot move the engagement status; it records a decision and engagement-service applies it (ADR 0009).

### 4. What is the gateway for, and what did you deliberately keep out of it? (ADR 0001)
It gives the browser one origin and one stable SSE URL, because paths like `/engagements/{id}/...` are served by several services. It has **no
business logic**: no authorization decisions, no aggregation. Every service validates the JWT itself. `/internal/**` is deliberately not routed,
so service-to-service endpoints are reachable only on the Docker network, and a test asserts the gateway routes no internal path.

### 5. Database per service on one SQL Server instance: is that really isolation? (ADR 0002)
Separate **databases**, not schemas, so "no cross-service reads" is enforced by connection string rather than discipline. One instance is just
the cheapest local setup (the Service Bus emulator needs SQL Server anyway). Data other services need travels by API call or event, and I
store **snapshots** instead of joins (participants' display names are copied at creation). Cost: names can go stale after a rename. In
production each database could move to its own server without code changes.

### 6. How does a service get data it does not own?
Three options in order of preference: carry what it needs in the event (small, id-oriented payloads); a synchronous internal call when it
needs the current truth (access checks, submit readiness); or a local projection built from events (workflow's `AttachmentPresence` records
which scopes have attachments so "attachment required" rules work without reading the attachment database).

---

## B. Messaging and consistency

### 7. Explain the outbox pattern and why you used it. (ADR 0003)
You cannot put a database write and a Service Bus publish in one transaction. So the service writes an `OutboxMessage` row **in the same
transaction** as the state change. A background `OutboxDispatcher` polls unpublished rows in order, publishes through `IEventBus`
(MessageId = EventId) and marks them published. Events therefore exist only if the state committed, and are not lost if the broker or the
service is down. Delivery is **at-least-once**, so consumers must be idempotent.

### 8. How do you make consumers idempotent?
An inbox table: `EventDispatcher` runs, in one transaction, an insert of `(consumer, messageId)` into `ProcessedMessage` followed by the
handlers, which share the same unit of work. A duplicate delivery hits the primary key and is skipped; a handler failure rolls back the
marker too, so the retry reprocesses. The handler's writes and the marker commit atomically, which is what makes it mechanical rather than
"be careful".

### 9. Does the outbox preserve ordering? What happens when a message keeps failing?
Rows are published in order, and a failing row is retried with exponential backoff (1 s doubling to 30 s, up to 100 attempts) and **blocks
newer rows**, so a retry never reorders events for an engagement. Trade-off: a poison message stalls the queue behind it (head-of-line
blocking). For a POC I accepted that; in production I would add a dead-letter/quarantine path and an alert on the backlog metric.

### 10. The system is eventually consistent. Where does a user see that, and how did you handle it? (ADR 0006, 0009)
Status changes are the visible case. Editing a Draft happens in workflow-service, but the status belongs to engagement-service, so workflow
publishes `SectionUpdated` and engagement-service drives `StartWork` through the state machine. The header can say "Draft" for about a second.
Alternative: workflow calls engagement synchronously inside the save, which couples the two services' availability. I chose events; the UI
refetches after a save and SSE's `EngagementStatusChanged` closes the gap. Likewise an approve/reject returns **202** with the decision, and
the UI shows "recorded, status updating".

### 11. Concurrent consumers can process events for one engagement out of order. How do you cope?
Handlers are written so order does not matter: transitions are guarded (`UPDATE ... WHERE Status = @from`), and "touch activity" is
commutative. For SSE, where order matters to clients, versions travel in the payload and clients reconcile against them rather than trusting
arrival order. The alternative, Service Bus sessions keyed by engagement id, would give ordering but reduce parallelism; documented, not built.

### 12. Why one topic with a subscription per service instead of queues?
Several services react to the same event (`SectionUpdated` goes to engagement, notification, and others), so pub/sub fits. Each subscription
has correlation-rule filters on the message Subject, one per event type, so a service receives only what it handles. The subscriber waits for
its subscription to exist before starting, which I found necessary when the emulator was still loading.

### 13. Would you use Azure Functions here?
The PRD mentions them for async scale-out; the project instructions mark them optional. The natural candidate is a single function consuming
a Service Bus topic (for example, notification fan-out or a retention job). I did not build one because the in-process hosted consumers already
share the idempotent pipeline, and splitting would duplicate that plumbing. It is a deployment choice, not an architectural change, because
`IEventBus` and the handler abstraction already isolate the broker.

---

## C. Concurrency and data

### 14. How do you handle two people editing the same section? (ADR 0006)
Optimistic concurrency with SQL Server `rowversion` at **section** granularity. The client sends the version it loaded (`If-Match`, or a
per-section `version` in a tab PATCH). The update is `UPDATE ... WHERE RowVer = @v`; if no row changes the API returns **409** with
`currentVersion`. A tab PATCH is an atomic batch: one stale section rolls back everything. The UI shows a field-level "yours vs current" view
and keeps the user's unsaved text.

### 15. Why section-level and not engagement-level or field-level versioning?
Engagement-level would produce constant false conflicts (any save by anyone would invalidate everyone). Field-level would be precise but
explodes the version bookkeeping and the UI. The section is the unit people actually edit, and it is the unit that Tab 1 rules show or hide, so
it is a natural consistency boundary.

### 16. You also take an `UPDLOCK, HOLDLOCK` per engagement. Isn't that pessimistic locking, contradicting the optimistic approach?
Different purposes. Rule evaluation is **whole-engagement** (changing Tab 1 flips visibility across many sections and recomputes the validation
summary). Without serialization, one save could evaluate against a snapshot another save is changing. So writes to one engagement are
serialized by locking its configuration row at the start of the transaction. The `rowversion` check remains the guard that decides who loses.
Contention per engagement is low, so this is fine for about 100 users; at higher write rates I would revisit it.

### 17. What do you return when an engagement is locked: 409 or 403?
**403.** Under Review and Approved remove `content.edit` and `attachment.upload` from the access answer, so the capability is absent. 409 is
reserved for version conflicts. Mixing them would make clients guess whether to retry with fresh data (409) or stop (403).

### 18. How do you guarantee "only the latest attachment is visible"? (ADR 0007)
In the database, not in application code. `Attachments.ScopeKey` is a computed column (`engagement`, `tab:3`, `section:section-2-1`) with a
**filtered unique index** `(EngagementId, ScopeKey) WHERE IsCurrent = 1`. A replacement marks the old row not current and deletes its file after
the commit. Concurrent uploads to the same scope are serialized with `sp_getapplock` so they cannot trip the index. Bytes sit behind `IFileStore`
(local volume now; blob storage would be another implementation). Known gap: a crash between file write and commit can leave an orphan file,
cleaned on the error path only, with no sweeper.

### 19. Why Dapper and DbUp rather than EF Core?
Explicit SQL matters here: `rowversion` predicates, `UPDLOCK`, filtered unique indexes, computed columns and guarded transitions are things I want
visible in the code and reviewable, not generated. Dapper is thin and fast, DbUp gives simple ordered embedded SQL scripts applied at startup.
Cost: more boilerplate and no change tracking, which suits small, intention-revealing aggregates.

---

## D. Backend-controlled UI, rules and workflow

### 20. What does "backend-controlled UI" mean concretely?
The backend is the single source of truth for status, permissions, section visibility, validation outcomes and workflow progression. Engagement
reads return `allowedActions` / `capabilities`; the workflow API returns which sections are visible and which issues exist. The React client
renders that and owns no business decisions: no `if (role === 'Reviewer')` logic. Benefit: one place to change and test rules, and no client/server
drift. Cost: more round trips and a thin client that cannot act offline.

### 21. How is Tab 1 driving downstream sections without hardcoding in the client? (ADR 0005)
A versioned JSON **rule set** describes tabs, 13 sections, fields, `requiredWhen` conditions, attachment policy and reconciliation thresholds.
Conditions use a small DSL (`equals`, `in`, `isTrue`, `isEmpty`, `gt/gte/lt/lte`, `lengthLt`, `not`, `all`, `any`) over **facts** such as
`cfg.derivatives`, `periodType`, `region`. Evaluation is pure code (`RuleEvaluator`, `ValidationEngine`, `ReconciliationCalculator`) with no I/O.
Unticking *Derivatives* in Tab 1 hides the Derivatives sections and clears their errors; ticking it brings them back with their values (hidden values
are kept, just invisible to rules).

### 22. Why recompute derived data on every read instead of storing it?
No stale rows. If I stored issues, a rule-set change or a late `AttachmentAdded` could leave old messages behind. Evaluating about 13 sections is
cheap, and only the last published summary **hash** is stored so `ValidationSummaryChanged` is emitted only when it actually changes. Each
engagement is **pinned** to the rule-set version it was provisioned with, so a new version never changes an in-flight engagement silently.
Limit: the DSL has no loops or cross-section arithmetic; if requirements needed that I would adopt a real rules engine.

### 23. Walk me through the state machine and where it is enforced. (ADR 0004)
Draft -> In Progress -> Under Review -> Approved | Rejected; Rejected -> In Progress after rework; Approved is final. `EngagementStateMachine`
in engagement-service is the only code allowed to change `Engagements.Status`, and the write is guarded in SQL
(`WHERE Status = @from [AND RowVer = @ver]`), so even a race or duplicate event cannot make an illegal transition. 154 unit tests cover it,
including every invalid transition. Submit is safe against double-click for the same reason: the status guard, not a version check, is what
rejects the second one.

### 24. What stops someone submitting an incomplete engagement?
Submit asks workflow-service for readiness synchronously (5 s timeout, no retry) and refuses with **422** plus the blocking issues. Known gap I
documented: an edit can slip in between the readiness check and the status transition. Fix options are re-checking readiness inside the
transition or locking, which I judged out of scope for the POC.

---

## E. Security

### 25. How do roles work, given a user can be Auditor on one engagement and Reviewer on another? (ADR 0004)
The JWT (RS256, validated by every service through OIDC discovery + JWKS) carries identity and a **home role** only, answering global questions
(may this user create engagements? who can be picked as reviewer?). Per-engagement permissions come from **engagement-service**, which owns
`PermissionPolicy` (role x status -> capabilities). Other services call `GET /internal/engagements/{id}/access` with the caller's token and get
`{role, status, version, capabilities[]}`. Nobody else re-implements the matrix. Non-participants get 403; unknown engagements 404. A 30 s cache is
invalidated by `EngagementStatusChanged`.

### 26. How do you know the permissions are actually enforced everywhere? (ADR 0011)
Two checks on every PR. **Route inventory tests** map every service's real endpoints and fail if a route lacks `RequireAuthorization` and is not on
an explicit allow-list (login, discovery documents); `/internal/*` must always require auth. A **black-box permission audit** logs in as the demo
users, drives throwaway engagements through every status via the real API, and compares 841 role x status x operation results with a
**hand-written** `matrix.json`. It is deliberately not generated from the code, so a policy regression shows as a mismatch. Changing a permission
means editing both in one PR.

### 27. What are the security weaknesses of this POC?
Stated plainly: `/internal/*` endpoints accept any valid token and rely on the gateway not routing them (production needs service-to-service
credentials, e.g. managed identity or client credentials). Tokens last 60 minutes with no refresh. The local issuer is not a full OAuth server
(Entra ID can replace it by configuration since services depend only on metadata address, issuer and audience). The demo password and DB-stored
signing key are dev defaults; secrets sit behind an abstraction that supports Key Vault. Uploads: 10 MB, names sanitized, always served as
`application/octet-stream` with `nosniff`, but no antivirus or content verification.

---

## F. Real time (SSE)

### 28. Why SSE instead of SignalR or WebSockets? And how does it stay consistent with authorization? (ADR 0008)
Updates are one-directional server-to-browser, so SSE is simpler (plain HTTP, automatic reconnect, `Last-Event-ID` replay) and the PRD excludes
SignalR. Frames carry only the **envelope** (names, ids, versions), never field values; the client re-reads the changed section through the normal
API, so ordinary authorization applies. Callers without `attachment.view` / `comment.view` never receive `AttachmentAdded` /
`ReviewerCommentAdded` (Viewers see neither). Control events handle edge cases: `ready` (refetch), `resync` (gap over 500 events or slow client),
`reauth` (token expired), plus a 15 s heartbeat.

### 29. Why does SSE read from the event log by polling rather than pushing from the consumer? What are the scaling limits?
Delivery source is `DomainEventLog`, polled every 250 ms by `EventLogTailer` into an in-memory `EventHub` (a bounded channel per browser). It
reads only **committed** rows, so nothing is announced that could still roll back, and live delivery and replay share one table and one `Seq`.
Pushing from the handler is lower latency but runs inside the consumer's transaction. Because `Seq` is an identity and consumers run concurrently,
a lower `Seq` can commit after a higher one, so each poll re-reads 200 rows behind the last published and de-duplicates. Scaling: each instance runs
its own tailer and serves its own browsers (it works because they all read the same table), so no backplane is needed. The log grows without bound
in the POC; retention must keep `resync` in mind.

---

## G. Observability, testing, performance

### 30. How did you test and measure this, and what did the load test actually find? (ADR 0010, load-test-results)
**Tests:** xUnit + Moq unit tests for the state machine, permission matrix, validators, every rule operator, rule-driven visibility, reconciliation
thresholds and the idempotent consumer pipeline; Testcontainers integration tests (`Category=Integration`) for outbox atomicity/ordering/backoff,
duplicate delivery, parallel writers (2 and 8 racing; exactly one wins), the full submit-reject-rework-approve lifecycle across databases, and the SSE
hub; plus the route inventory and permission audit above.

**Observability:** the correlation id flows through HTTP, outbox rows, messages and logs, and the W3C `traceparent` is stored **in the outbox row**
so a consumer's span joins the trace of the request that caused it, even if the dispatcher publishes minutes later. Export goes to Application
Insights if configured, else OTLP (local Aspire dashboard), else nothing; one `AuditFlow` meter covers outbox backlog, duplicates, 409s, SSE
connections and resyncs. Readiness checks reflect what each service needs (SQL, broker, tailer freshness).

**Load test (100 users + 100 SSE streams, stress profile):** 0 server errors, 83 % of deliberately contended saves returned 409 as designed, but
targets were missed: SSE delivery p95 was 95.9 s. The cause was **not** the request path: the outbox dispatcher published one message per broker
round trip (about 25 msg/s) and a backlog built up. I changed it to publish each poll's rows as one ordered broker batch and removed expensive
expected-outcome logging. **The post-tuning numbers are not measured yet** (Docker ran out of memory during the rebuild), so the improvement is
covered by integration tests but unverified. Say that out loud rather than claiming a fix.

---

## H. Judgement and trade-offs

*(Bonus follow-ups interviewers often ask after the above; short answers.)*

- **What would you change first for production?** Service-to-service credentials; dead-letter handling and an alert on outbox backlog; blob storage
  for attachments; a retention job for the event log; a token refresh flow; re-check readiness inside the submit transition.
- **What would break first at 10x users?** The per-engagement write lock is fine, but the shared SQL Server (including the broker emulator), the
  single-instance outbox dispatcher per service, and the polling tailers. I would partition the outbox by engagement, move to managed Service Bus, and
  measure before tuning, as the load test showed the bottleneck was not where I first expected.
- **What did you deliberately not build?** Document version history, reporting/analytics, an admin UI for rules, field-level audit playback, and
  external integrations (the PRD non-goals). Saying no, in writing, is part of the design.
- **What are you most proud of?** Boundaries that hold under test: one state machine, one permission matrix, audited black-box against a
  hand-written contract, and a trace that follows a click through the outbox and broker to every consumer.
