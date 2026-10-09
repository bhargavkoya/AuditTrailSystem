# AuditFlow Implementation Plan (approved with defaults)

Source spec: `docs/PRD.md` (converted from `docs/auditflow_prd.docx`). Conventions: `CLAUDE.md`.

## Approved decisions
- YARP gateway: **yes**, infrastructure only (no business logic).
- Azure Function: **skipped** for the POC.
- Open Decisions 1-6: **defaults accepted** (section 11).
- PRD ambiguities: **defaults accepted** (section 11).
- Attachment routes nested under `/engagements/{id}`; home role is a JWT claim used only for create and the reviewer picker.

## 1. Roadmap

| Milestone | Scope | Demo at the end |
|---|---|---|
| Phase 0 - Scaffolding | Repo layout, 6 service skeletons + shared library, compose with SQL Server and Service Bus emulator, health + Swagger, placeholder page | One command starts everything; page shows service health |
| Phase 1 - Foundation | Local JWT issuer, role model, seed users, engagement creation, dashboard shell, outbox, `IEventBus`, first events | Log in as each role; role-scoped dashboard; create publishes `EngagementCreated` |
| Phase 2 - Workflow Core | Five tabs, rule-driven visibility, sections/fields, validation + banners, attachments, reconciliation | Change a Tab 1 option and sections appear/disappear; upload at all three scopes |
| Phase 3 - Review and Real-Time | Submit, review screen, comments, approve/reject, full state machine, SSE | Two browsers: edit propagates live; submit, reject, rework, approve |
| Phase 4 - Hardening | App Insights/OTel, 409 UX + concurrency tests, permission audit, ~100-user load check, CI | Trace one submit across services by correlation id; force a 409 |

Telemetry config is wired in Phase 0; the PRD lists observability integration in Phase 4.

## 2. Microservices

One SQL Server instance, one database per service. No cross-service DB reads.

| Service | Responsibility | Owns | Endpoints | Publishes | Consumes |
|---|---|---|---|---|---|
| auth :5001 | Local OAuth-style issuer, JWT + JWKS, global capability claims | Users, Roles | `/auth/login`, `/auth/me`, `/auth/permissions`, `/.well-known/jwks.json` | - | - |
| engagement :5002 | Engagement CRUD, participants, **state machine**, permission policy, dashboard list | Engagements, EngagementParticipants | PRD engagement endpoints + internal `GET /internal/engagements/{id}/access` | EngagementCreated, EngagementUpdated, EngagementSubmitted, EngagementStatusChanged | SectionUpdated, ConfigurationChanged (work started / rework started / last activity), ReviewApproved, ReviewRejected |
| workflow :5003 | Tabs, sections, fields, rule evaluation, validation, reconciliation | EngagementConfiguration, WorkflowTab, WorkflowSection, SectionField, ValidationIssue, ReconciliationSummary, RuleSet | PRD workflow endpoints + internal `GET /internal/engagements/{id}/submission-readiness` | ConfigurationChanged, SectionUpdated, ValidationSummaryChanged | EngagementCreated, EngagementUpdated, AttachmentAdded, EngagementStatusChanged |
| attachment :5004 | Metadata, scoped access, file store behind `IFileStore` | Attachment | PRD attachment endpoints + download | AttachmentAdded | EngagementStatusChanged |
| review :5005 | Review screen aggregation, comments, decisions, history | TabComment, ReviewDecision | PRD review endpoints | ReviewerCommentAdded, ReviewApproved, ReviewRejected | EngagementSubmitted, EngagementStatusChanged |
| notification :5006 | Consumes all events, persists log, SSE fan-out | DomainEventLog | `GET /engagements/{id}/events/stream` | - | all events |

Gateway (YARP) :5000: single browser origin, routes overlapping `/engagements/{id}/...` paths to the owning service, passes SSE through unbuffered.

**Cross-service authorization.** Roles are per engagement, so the JWT carries identity and a global home role only (`sub`, `name`, home role; decides who may create engagements and appear in the reviewer picker). Other services get `{role, status, version, capabilities[]}` from engagement-service `/internal/engagements/{id}/access`, computed by the single permission policy + state machine, cached 30 s, invalidated by `EngagementStatusChanged`.

## 3. Database schema (SQL Server + Dapper, DbUp)

`RowVer` = `rowversion`. Every publishing service also has `OutboxMessage` and `ProcessedMessage`.

| Service | Tables |
|---|---|
| auth | `Users(UserId, Email UQ, DisplayName, PasswordHash, HomeRole)`, `Roles` (seeded 4) |
| engagement | `Engagements(EngagementId 'ENG-1001' from SEQUENCE, Name, PeriodType, Region, OwnerUserId, ReviewerUserId, Status CHECK, SubmissionCount, LastActivityAt, CreatedAt, RowVer)`, `EngagementParticipants(EngagementId, UserId, Role, DisplayName snapshot)` |
| workflow | `EngagementConfigurations(EngagementId PK, PeriodType, Region, ConfigTypes json, Options json, RowVer)`, `WorkflowTabs(EngagementId, TabNo, Title, RowVer)`, `WorkflowSections(EngagementId, SectionKey, TabNo, Title, SortOrder, IsVisible, RowVer)`, `SectionFields(EngagementId, SectionKey, FieldKey, FieldType, ValueText/Bool/Date, IsRequired, IsVisible)`, `ValidationIssues(...)`, `ReconciliationSummaries(...)`, `RuleSets(Version, Json, IsActive)`, `AttachmentPresence(EngagementId, ScopeType, ScopeKey)` |
| attachment | `Attachments(AttachmentId, EngagementId, ScopeType, TabNo?, SectionKey?, FileName, ContentType, SizeBytes, StorageKey, UploadedBy, UploadedAt, IsCurrent)`; filtered unique index on scope WHERE `IsCurrent=1` = latest only |
| review | `TabComments(CommentId, EngagementId, TabNo, Cycle, AuthorUserId, AuthorName, Body, CreatedAt)`, `ReviewDecisions(DecisionId, EngagementId, Cycle, Decision, ReviewerUserId, Comment, DecidedAt)` with CHECK: comment required when rejected |
| notification | `DomainEventLog(Seq BIGINT IDENTITY, EventId UQ, EventType, EngagementId, Payload, OccurredAt)` indexed `(EngagementId, Seq)` |

Row versions live on `Engagements`, `EngagementConfigurations`, `WorkflowTabs`, `WorkflowSections` (fields are versioned via their section). Comments, decisions, attachments are append/replace-only.

`DomainEvent` is the outbox row on the publisher side (same transaction, makes review actions auditable) and `DomainEventLog` on the notification side (SSE replay).

Version format: opaque base64 rowversion via `ETag` / `If-Match`.

## 4. REST contract

Shorthand: **Auth** = any valid JWT. **Part(x)** = participant with capability x (via `/access`). Writes need `If-Match`; conflict = 409 ProblemDetails with current version. **Edit** also requires status Draft, In Progress or Rejected.

| Endpoint (via gateway) | Service | Rule |
|---|---|---|
| `POST /auth/login` | auth | anonymous |
| `GET /auth/me`, `GET /auth/permissions` | auth | Auth |
| `POST /engagements` | engagement | home role Auditor/Co-Auditor; reviewer must be a Reviewer-home-role user, not the creator |
| `GET /engagements?scope=owned\|assigned\|reviewing\|participating&status=&q=` | engagement | Auth; only caller's participations; includes status counts |
| `GET /engagements/{id}` | engagement | Part(view); returns `status`, `myRole`, `allowedActions[]`, `version` |
| `PATCH /engagements/{id}` | engagement | Part(edit), Draft only (name, reviewer) |
| `POST /engagements/{id}/submit` | engagement | Part(submit); state machine; readiness has zero Errors |
| `GET /engagements/{id}/participants` | engagement | Part(view) |
| `GET /engagements/{id}/tabs` | workflow | Part(view) |
| `GET /engagements/{id}/tabs/{tabNo}` | workflow | Part(view); full UI view model |
| `PATCH /engagements/{id}/tabs/{tabNo}` | workflow | Part(edit); Tab 1 bulk configuration |
| `PATCH /engagements/{id}/sections/{sectionKey}` | workflow | Part(edit); returns new version + section issues |
| `GET /engagements/{id}/validation-summary` | workflow | Part(view) |
| `POST /engagements/{id}/attachments` | attachment | Part(attach) |
| `POST /engagements/{id}/tabs/{tabNo}/attachments` | attachment | Part(attach) |
| `POST /engagements/{id}/sections/{sectionKey}/attachments` | attachment | Part(attach) |
| `GET /engagements/{id}/attachments`, `.../{attId}/download` | attachment | Part(attach.view); Viewer 403 |
| `GET /reviews/{engagementId}` | review | Part(view), role Reviewer/Auditor/Co-Auditor |
| `POST /reviews/{engagementId}/comments` | review | assigned Reviewer, status Under Review |
| `POST /reviews/{engagementId}/approve`, `/reject` | review | assigned Reviewer, Under Review; reject needs non-empty comment (400) |
| `GET /engagements/{id}/events/stream` | notification | Part(view); events filtered per role |

Deviation from the PRD: attachment routes are nested under `/engagements/{id}`.
Internal (not routed by gateway): `GET /internal/engagements/{id}/access`, `GET /internal/engagements/{id}/submission-readiness`.

## 5. SSE contract

- `GET /engagements/{id}/events/stream`, one stream per engagement being viewed.
- Auth: fetch-based client (`@microsoft/fetch-event-source`) sends the Bearer token. Fallback: single-use ticket (`POST .../events/ticket`, 30 s, `?ticket=`). On connect, notification calls `/access`; 403 if no role. On JWT expiry send `event: reauth` and close.
- Frame:
  ```
  id: 1042
  event: SectionUpdated
  data: {"eventId":"...","eventType":"SectionUpdated","schemaVersion":1,"engagementId":"ENG-1024",
         "tabId":"tab-3","sectionId":"section-3-2","changedFields":["controlOwner","reviewDate"],
         "version":"AAAAAAAAB9E=","actor":{"userId":"...","name":"Alice"},
         "correlationId":"...","timestamp":"2026-05-15T00:00:00Z"}
  ```
- No field values in payloads. Client refetches only the changed section via the normal API, so authorization stays in one place.
- Viewers never receive `AttachmentAdded` or `ReviewerCommentAdded`.
- Reconnect: `id:` = `DomainEventLog.Seq`; on `Last-Event-ID` replay `Seq > id` for that engagement; if gap too large or purged send `event: resync` (client refetches tabs + validation summary). Heartbeat comment every 15 s.
- Bus to browser: notification has one subscription for all events; handler inserts into `DomainEventLog` (idempotent on `EventId`) and pushes to in-memory per-engagement bounded `Channel<T>` subscribers (overflow triggers `resync`). Multi-instance would need a subscription per instance; documented only.
- Gateway must not buffer the response and needs a long timeout.

## 6. Frontend (backend-controlled)

Pages: `/login`, `/` dashboard, `/engagements/:id` (five tabs), `/engagements/:id/review`, create-engagement dialog.

- shared: `apiClient` (token, correlation id, `If-Match`, 409 mapping), `AuthProvider`, `useEngagementStream`, `Banner`, `StatusBadge`, `ConflictBanner`
- features/dashboard: `StatusCards`, `EngagementFilters`, `EngagementTable`, `QuickActions` (from `allowedActions`)
- features/engagement: `EngagementHeader`, `TabNav`, `ValidationBanners`, `SectionRenderer`, `FieldRenderer`, `AttachmentPanel`, `ReconciliationTable`, `SubmitBar`
- features/review: `ReviewLayout` (distinct colour scheme), `ReadOnlyTabView`, `TabCommentThread`, `DecisionPanel`, `ReviewHistory`, `ValidationSummaryPanel`

Tab view model:
```
{ tabNo, title, version, editable,
  sections:[{ sectionKey, title, visible, editable, version,
              fields:[{ key, type, label, value, required, editable, issues:[...] }],
              attachment:{ canUpload, current:{fileName, uploadedAt} | null },
              issues:[...] }],
  issues:[...] }
```
Engagement view returns `allowedActions` and `myRole`. The client has no role checks and no rule logic.

SSE reconciliation: compare event version with cached; ignore if cached >= event; else invalidate only that section query; if the user has unsaved edits in the section, show a "changed by X; reload?" banner.

Layout: Vite path aliases + tsconfig paths to `frontend/features/*` and `frontend/shared`; single `package.json` in `apps/web` (no npm workspaces); Tailwind `content` includes those folders.

## 7. User stories and acceptance criteria

| FR | Story | Acceptance criteria |
|---|---|---|
| 1 | Log in, access follows role | Valid credentials return JWT with `sub` + home role; invalid 401; non-participant 403; all services validate the token |
| 2 | See my engagements | Status cards with counts; PRD columns; filters owned/assigned/reviewing/participating; only my participations; role-sensitive quick actions |
| 3 | Create an engagement | Name, period type, region, reviewer, configuration mandatory (400); starts in Draft with `ENG-nnnn`; creator is participant; `EngagementCreated` published after commit; tabs appear shortly after (UI shows "provisioning") |
| 4 | Tab 1 drives downstream | Rules live in workflow RuleSet; saving Tab 1 changes section visibility/required fields in the same response; `ConfigurationChanged` lists affected sections |
| 5 | Section data entry | All five field kinds; field/section/tab validation on save; hidden sections not validated or shown |
| 6 | Attachments | Upload at section/tab/engagement scope; new upload at same scope replaces old (latest only); Viewer 403 |
| 7 | Role permissions | Matrix enforced server-side; Reviewer PATCH 403; Viewer sees no attachments/comments |
| 8 | Validation messaging | Error/Warning/Information; banner shows counts; `validation-summary` matches tab data |
| 9 | Submit | Succeeds only with zero Errors (else 422 listing them); status Under Review; edits then rejected; `EngagementSubmitted` emitted |
| 10 | Review | Read-only screen; tab comments per cycle; approve -> Approved; reject without comment 400; reject -> Rejected, first auditor edit -> In Progress; history lists decisions |
| 11 | Live changes | Edit by A reaches B's stream within ~1 s; no field values in payload; reconnect replays via Last-Event-ID; Viewer never gets comment/attachment events |
| 12 | Reconciliation | Percentage and value variances computed server-side; significant variances and missing values shown as banners before submit |

## 8. Lifecycle state machine

| From -> To | Trigger | Who | Preconditions |
|---|---|---|---|
| (new) -> Draft | Create | Auditor/Co-Auditor | mandatory fields; reviewer valid |
| Draft -> In Progress | WorkStarted | system, on first `SectionUpdated`/`ConfigurationChanged` by an editor | status Draft |
| In Progress -> Under Review | Submit | Auditor/Co-Auditor participant | workflow readiness has zero Errors (sync call) |
| Under Review -> Approved | Approve | assigned Reviewer | status Under Review |
| Under Review -> Rejected | Reject | assigned Reviewer | mandatory non-empty comment |
| Rejected -> In Progress | ReworkStarted | system, on first edit by an editor | status Rejected |
| Approved | terminal | - | - |

Enforcement: one table-driven `EngagementStateMachine` in engagement-service is the only code writing `Status`. SQL is `UPDATE ... WHERE Status=@from AND RowVer=@ver`; 0 rows = 409. CHECK constraint on values. Approve/reject travel as events from review-service and are applied idempotently by engagement-service; review-service pre-checks via `allowedActions`. Every transition writes `EngagementStatusChanged` to the outbox in the same transaction.

Known gap: an edit can slip in between the readiness check and the transition. Accepted for the POC; the fix is to revalidate inside the transition.

## 9. Permissions matrix

All five tabs are identical per role. Tab 1 hides/shows sections for everyone via rules, never via role.

| Role | View | Edit (all tabs) | Comment | Upload | See attachments | See comments | Submit | Approve/Reject |
|---|---|---|---|---|---|---|---|---|
| Auditor | Y | Y | N | Y | Y | Y (read) | Y | N |
| Co-Auditor | Y | Y | N | Y | Y | Y (read) | Y | N |
| Reviewer | Y | N | Y (tab level) | N | Y | Y | N | Y |
| Viewer | Y | N | N | N | N | N | N | N |

| Status | Editing and uploads | Comments and review |
|---|---|---|
| Draft, In Progress, Rejected | allowed | no |
| Under Review | locked | allowed (Reviewer) |
| Approved | locked | locked |

## 10. Messaging

- Topic `auditflow-events` with filtered subscriptions (SQL filter on `EventType`): `notification` (all), `engagement`, `workflow`, `attachment`, `review`. Built-in DLQ. Implemented in Phase 0 as correlation-filter rules on the message Subject (one rule per event type, OR semantics) in `infra/local/servicebus/Config.json`; the emulator accepts this config. Publish/consume is exercised from Phase 1.
- `IEventBus`: `PublishAsync(EventEnvelope, ct)` + `IEventHandler<T>`. Implementations: `ServiceBusEventBus`, `InMemoryEventBus`.
- Envelope: `eventId`, `eventType`, `schemaVersion`, `engagementId`, `occurredAt`, `correlationId`, `actor`, `payload`.
- Outbox: `OutboxMessage(Id, EventType, Payload, CorrelationId, OccurredAt, PublishedAt NULL, Attempts)` written in the same Dapper transaction as the state change; a `BackgroundService` polls every 500 ms (`SELECT TOP 50 ... WITH (UPDLOCK, READPAST)`), publishes with `MessageId = Id`, marks `PublishedAt`. At-least-once.
- Idempotent consumers: `ProcessedMessage(ConsumerName, MessageId)` composite PK; insert + work in one transaction; PK violation = skip.
- Correlation id: `X-Correlation-Id` header -> message property -> log scope -> activity tag.

| Event | Publisher | Payload | Consumers |
|---|---|---|---|
| EngagementCreated | engagement | name, periodType, region, reviewerId, participants[], configuration | workflow, notification |
| EngagementUpdated | engagement | changedFields, version | workflow, notification |
| ConfigurationChanged | workflow | changedKeys, affectedSections[{id, visible}], version | engagement, notification |
| SectionUpdated | workflow | tabId, sectionId, changedFields, version | engagement, notification |
| AttachmentAdded | attachment | scope, tabNo?, sectionKey?, attachmentId, fileName | workflow, notification (not Viewers) |
| ValidationSummaryChanged | workflow | errors, warnings, infos, readyToSubmit, version | notification |
| EngagementSubmitted | engagement | submittedBy, cycle | review, notification |
| ReviewerCommentAdded | review | commentId, tabNo, authorId | notification (not Viewers) |
| ReviewApproved / ReviewRejected | review | decisionId, reviewerId, cycle | engagement, notification |
| EngagementStatusChanged | engagement | from, to, reason, version | workflow, attachment, review, notification |

## 11. Risks and decisions

| Risk | Mitigation |
|---|---|
| Create then open: workflow not initialized yet | UI shows "provisioning" until tabs exist |
| Dual writes | Outbox is the only publish path |
| Sync call chains fail/slow | Timeouts, one Polly retry, review screen degrades by section |
| Permission cache staleness | 30 s TTL + invalidation; write path trusts state machine SQL guard |
| SSE through gateway (buffering, 6-connection HTTP/1.1 limit) | No response buffering; one stream per open engagement; test early in Phase 3 |
| Docker footprint on one Windows machine | Compose profiles; DbUp at startup; in-memory bus for tests |
| Rule engine / reconciliation scope creep | Small JSON DSL; fixed set of pairs |
| Boilerplate across 6 services + gateway + UI | `BuildingBlocks` = infrastructure only; vertical slices per phase |
| Local issuer is not real OAuth | Services depend only on Authority/JWKS URL; swap in Entra ID |

Open Decision defaults (accepted):
1. Reconciliation: expected vs actual pairs from Tab 2 numeric fields; variance % = |actual - expected| / |expected|; <1% Information, 1-5% Warning, >5% significant (Error); any missing value is an Error; thresholds in the RuleSet.
2. Attachment storage: local volume behind `IFileStore` (10 MB limit), metadata in SQL; Azure Blob documented, not built.
3. Rule engine: versioned JSON in `RuleSets`, evaluated by a pure `IRuleEvaluator` (equals, in, contains, isTrue, all/any -> show section, require field/attachment, set message severity).
4. Comments: flat, append-only, per tab and review cycle; no replies or resolve.
5. Audit trail: `ReviewDecision` + outbox/DomainEvent log (actor, timestamp, correlation id); no field-level history.
6. Approved engagements: fully locked.

PRD ambiguity defaults (accepted): Viewer sees read-only tab content + validation summary (no attachments/comments); Reviewer can read an engagement before submission (read-only); Draft -> In Progress on first saved edit; Rejected -> In Progress on first auditor edit; Auditors can read reviewer comments; period type/region/reviewer editable only in Draft (Tab 1 holds config types and rule options); creator cannot be the Reviewer.

## 12. Phased backlog

- **Phase 0:** scaffolding (see README).
- **Phase 1 (FR-1, 2, 3):** BuildingBlocks (auth middleware, outbox, `IEventBus`, processed-message store); auth service; engagement service (create/list/get, participants, access endpoint, permission policy, state-machine skeleton); gateway routes; dashboard shell; seed users in all four roles + one engagement per status; tests for permission policy, state machine, outbox, idempotent consumer.
- **Phase 2 (FR-4, 5, 6, 8, 12):** workflow init from `EngagementCreated`; RuleSet + `IRuleEvaluator`; tabs/section API with view models; validation framework + summary; `If-Match`/409 on section writes; attachment service; reconciliation; five-tab UI with banners and attachment panels; tests for rule-driven visibility, validation, concurrency.
- **Phase 3 (FR-7, 9, 10, 11):** submit with readiness check; review service (composition, comments, approve/reject); full state machine; notification service (log, SSE, replay, role filtering); `useEngagementStream` + review screen; tests for transitions, mandatory reject comment, SSE filtering.
- **Phase 4:** App Insights/OTel with console/OTLP fallback, cross-service trace demo, 409 UX polish, permission audit script, ~100-user load test, CI workflow.

