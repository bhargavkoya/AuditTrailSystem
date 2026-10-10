# Phase 1: Foundation (FR-1, FR-2, FR-3): detailed plan

Status: **proposed, awaiting approval.** Builds on `implementation-plan.md` (sections 1-12) and the Phase 0 scaffold.
No Phase 1 code is written yet.

## 1. Goal and demo

> Log in as each of four roles, see a role-scoped dashboard with status cards, filters and quick actions, create an engagement,
> and watch `EngagementCreated` travel engagement-service -> outbox -> Service Bus -> notification-service -> its event log.

**Demo script (5 minutes)**
1. `./infra/local/up.ps1`, then `npm run dev`. Log in as Alice (Auditor): 5 seeded engagements, one per status; cards show 1/1/1/1/1.
2. Switch to Victor (Viewer): only the engagements he participates in; no "New engagement" button (backend says `canCreateEngagement=false`).
3. Switch to Rachel (Reviewer): "reviewing" filter; quick action "Review" appears only on the Under Review row.
4. As Alice, create an engagement (options come from the backend). It appears in Draft with `ENG-1006`, `myRole=Auditor`.
5. Open `http://localhost:5006/swagger` -> dev events endpoint (or the SQL log) shows `EngagementCreated` with the same correlation id that appears in the engagement-service response header.
6. Swagger: call `POST /engagements` as a Viewer -> 403; as non-participant `GET /engagements/ENG-1003` -> 403.

**Out of scope for Phase 1:** tabs/sections/rules (Phase 2), attachments (Phase 2), submit/review/SSE (Phase 3), `PATCH /engagements/{id}` (Phase 2, arrives with optimistic-concurrency UX), App Insights dashboards (Phase 4).

## 2. Deliverables by area

### 2.1 BuildingBlocks (shared infrastructure only, no domain code)
| Component | Purpose |
|---|---|
| `AddAuditFlowAuthentication` | JwtBearer using auth-service OIDC discovery (`Auth:MetadataAddress`), fixed issuer/audience, `ICurrentUser` (UserId, Name, HomeRole) |
| `ISecretProvider` | Config/user-secrets/env implementation; Key Vault implementation selected when `KeyVault:Uri` is set (stack requirement; used for the signing key) |
| `IDbConnectionFactory` + `IUnitOfWork` | Dapper connections and an explicit transaction scope, all async |
| `AddAuditFlowMigrations` | DbUp: `EnsureDatabase` then embedded SQL scripts at startup; shared script for `OutboxMessage` + `ProcessedMessage` |
| Outbox | `IOutbox.Enqueue(envelope, uow)`; `OutboxDispatcher : BackgroundService` (poll 500 ms, `UPDLOCK, READPAST`, publish via `IEventBus`, mark published, attempts counter) |
| Consumers | `ServiceBusSubscriberHost` (one `ServiceBusProcessor` per subscription, dispatches by Subject to `IEventHandler<T>` registry); `IdempotentHandler` wrapper (insert `ProcessedMessage` + work in one transaction; PK violation = skip); restores `CorrelationContext` from message |
| Versioning helpers | `RowVersion` <-> base64 string; `ConcurrencyException` -> 409 ProblemDetails with current version |
| Errors | Global ProblemDetails middleware (validation 400/422, forbidden 403, conflict 409) |
| Tracing | `ActivitySource "AuditFlow.Messaging"`: spans for outbox publish and handler execution (already included in `AddSource("AuditFlow.*")`) |

### 2.2 contracts
- `contracts/events` -> project `AuditFlow.Contracts.Events`: payload records + event-type name constants + `README` catalog. Phase 1 payloads: `EngagementCreated`, `EngagementUpdated` (type declared, unused), `EngagementStatusChanged` (declared, unused).
- `contracts/api` -> project `AuditFlow.Contracts.Api`: `EngagementAccessDto`, `CreationOptionsDto` and shared enums (`EngagementStatus`, `EngagementRole`). Services share contracts, never internals.

`EngagementCreated` payload (schemaVersion 1):
```json
{ "name": "...", "periodType": "Quarterly", "region": "EMEA", "status": "Draft", "seeded": false,
  "reviewerUserId": "...", "participants": [ { "userId": "...", "role": "Auditor", "displayName": "..." } ],
  "configuration": { "configTypes": ["Equities"], "options": { "includeReconciliation": true } } }
```
`status` and `seeded` let workflow-service (Phase 2) provision seeded engagements in the right state.

### 2.3 auth-service (database `AuditFlow_Auth`)
Tables: `Roles(RoleName PK)` seeded 4; `Users(UserId, Email UQ, DisplayName, PasswordHash, HomeRole FK)`; `SigningKeys(KeyId, PrivatePem, CreatedAt)` (dev-grade, key sourced via `ISecretProvider`).

| Endpoint | Rule | Notes |
|---|---|---|
| `POST /auth/login` | anonymous | `{email, password}` -> `{accessToken, expiresAt, user}`; RS256 JWT, claims `sub, name, email, role` (home role), `iss=auditflow-auth`, `aud=auditflow`, 60 min; failures return a generic 401 |
| `GET /auth/me` | Auth | identity from token |
| `GET /auth/permissions` | Auth | global capabilities only: `{ canCreateEngagement }` (per-engagement permissions come from engagement-service) |
| `GET /auth/users?homeRole=` | Auth | id, name, homeRole: feeds reviewer/participant pickers |
| `GET /.well-known/openid-configuration`, `/.well-known/jwks.json` | anonymous | what the other services validate against |
| `POST /internal/users/lookup` | Auth, internal (not routed by gateway) | batch lookup used by engagement-service to validate and snapshot display names |

Password hashing: ASP.NET `PasswordHasher<T>` (PBKDF2). Seed users (dev only, password `Passw0rd!`, documented in README):

| User | Home role |
|---|---|
| Alice Auditor | Auditor |
| Bob Co-Auditor | Co-Auditor |
| Rachel Reviewer | Reviewer |
| Rohan Reviewer | Reviewer |
| Victor Viewer | Viewer |

Deterministic GUIDs so engagement seeds can reference them.

### 2.4 engagement-service (database `AuditFlow_Engagement`)
Tables: `Engagements`, `EngagementParticipants` (per the main plan), `EngagementSeq` (SEQUENCE starting at 1001), outbox/processed-message. Indexes: `EngagementParticipants(UserId, EngagementId)`, `Engagements(Status)`.

Code layout (thin): `Api/` (minimal-API endpoint groups), `Domain/` (pure), `Data/` (Dapper repositories), `Clients/` (`IUserDirectoryClient`, typed HttpClient with correlation handler).

**Domain (pure, fully unit-tested):**
- `EngagementStateMachine`: the complete transition table from the main plan (create, WorkStarted, Submit, Approve, Reject, ReworkStarted), actor/role rules and preconditions as inputs. Phase 1 *uses* only creation, but the whole table is built and tested now because it is pure and the interview centrepiece.
- `PermissionPolicy`: the full role x status matrix producing capabilities (`view, edit, comment, attach, attach.view, comment.view, submit, review`) and `allowedActions`.
- `CreateEngagementValidator`: see rules below.

**Endpoints**
| Endpoint | Rule |
|---|---|
| `GET /engagements/creation-options` | Auth; `{periodTypes, regions, configTypes, options[{key,label,type}]}` from backend configuration (the client hardcodes nothing) |
| `POST /engagements` | home role Auditor/Co-Auditor else 403 |
| `GET /engagements?scope=&status=&q=&page=&pageSize=` | Auth; only my participations |
| `GET /engagements/{id}` | participant else 403; returns `status, myRole, allowedActions[], version (ETag)` |
| `GET /engagements/{id}/participants` | participant else 403; Viewer sees names and roles only |
| `GET /internal/engagements/{id}/access` | forwards the user's bearer token; returns `EngagementAccessDto {role, status, version, capabilities[]}`; not routed by the gateway; no consumers until Phase 2 but defined and tested now |

**Create rules** (validator, mapped to 400/422):
- Mandatory: `name`, `periodType`, `region`, `reviewerUserId`, `configuration.configTypes` (non-empty). All values must be in the creation-options catalogue.
- Reviewer must have home role Reviewer and must not be the creator.
- Extra `participantUserIds`: each user's participant role = their home role (Auditor/Co-Auditor/Viewer); Reviewers only via `reviewerUserId`; no duplicates; the creator is added automatically as owner.
- All user checks go through `IUserDirectoryClient` (one batch call). Display names are snapshotted into `EngagementParticipants`.
- One transaction: insert engagement (id `ENG-nnnn` from the sequence) + participants + outbox `EngagementCreated`. Response 201 with the same shape as `GET /engagements/{id}`.

**List semantics:** `owned` = I am owner; `assigned` = my role is Auditor/Co-Auditor and I am not the owner; `reviewing` = my role is Reviewer; `participating` = any (default). `q` matches name or id. `counts` per status are computed for the same scope and `q` but ignore the status filter, so the cards double as filter tabs. Row: `engagementId, name, region, periodType, reviewer{userId,name}, status, lastUpdatedAt, myRole, allowedActions[]`.

**Seed (first run only, when `Engagements` is empty):**
| Id | Status | Owner | Co-Auditor | Reviewer | Viewer |
|---|---|---|---|---|---|
| ENG-1001 | Draft | Alice | Bob | Rachel | Victor |
| ENG-1002 | In Progress | Alice | | Rohan | |
| ENG-1003 | Under Review | Bob | Alice | Rachel | Victor |
| ENG-1004 | Approved | Alice | | Rachel | |
| ENG-1005 | Rejected | Alice | Bob | Rohan | Victor |

Seeds are inserted directly (documented as bypassing the state machine) and each emits an `EngagementCreated` outbox event with `seeded: true` and its status, so later services can provision themselves.

### 2.5 notification-service (database `AuditFlow_Notification`)
Phase 1 scope is only the proof that the bus works: subscribe to `notification` (all events), insert each event into `DomainEventLog` (idempotent on `EventId`), plus a **dev-only** `GET /dev/events/recent?engagementId=` (Auth, enabled only in Development/compose; removed when SSE lands in Phase 3). No SSE yet.

### 2.6 workflow / attachment / review services
Phase 1 changes: wire authentication, migrations (empty initial migration + outbox/processed tables), per-service database name and health check. No endpoints beyond Phase 0.

### 2.7 gateway
Routes added: `/auth/**` and `/.well-known/**` -> auth; `/engagements/**` -> engagement (more specific workflow/attachment routes added in Phase 2 with higher precedence). **No route for `/internal/**`.** Correlation header preserved. Services validate JWTs themselves; the gateway only forwards.

### 2.8 Infrastructure changes
- Per-service connection strings (`Database=AuditFlow_Auth|Engagement|Workflow|Attachment|Review|Notification`); DbUp creates the database on first start with retry until SQL is ready.
- Compose: gateway/service env for `Auth__MetadataAddress=http://auth-service:8080/.well-known/openid-configuration`, `Services__Auth__BaseUrl` for the typed client; local `dotnet run` equivalents in `appsettings.Development.json`.
- Emulator readiness: subscriber host retries with backoff (no hard dependency on emulator timing).

### 2.9 Frontend (`frontend/apps/web`, `features/dashboard`, `shared`)
New dependencies: `react-router-dom`, `@tanstack/react-query` (no UI kit; Tailwind only).

| Piece | Details |
|---|---|
| `shared/auth` | `AuthProvider`, `useAuth`, route guard, login page; token in `sessionStorage` (wrapped in try/catch; memory fallback); 401 -> back to login |
| `shared/api` | `apiClient` (Bearer, `X-Correlation-Id`, ProblemDetails parsing); Vite proxy for `/auth`, `/engagements` |
| `features/dashboard` | `DashboardPage`, `StatusCards` (click to filter), `EngagementFilters` (scope + search + status), `EngagementTable` (PRD columns), `QuickActions` rendered from `allowedActions`, `CreateEngagementDialog` (options and pickers from the backend), empty/error states |
| placeholders | `/engagements/:id` shows header (name, status badge, `myRole`, participants) and "Workflow arrives in Phase 2"; `/engagements/:id/review` placeholder |
| Rule | the client contains no role or status logic: buttons come from `canCreateEngagement` and `allowedActions` |

## 3. Work breakdown (one commit per step; I build and test after each)
| # | Step | Size |
|---|---|---|
| 1 | Branch `feature/phase-1-foundation` from the Phase 0 branch; contracts projects | S |
| 2 | BuildingBlocks: Dapper factory, UnitOfWork, DbUp runner, shared outbox/processed scripts | M |
| 3 | Outbox + dispatcher; idempotent consumer + subscriber host (+ integration tests) | L |
| 4 | Authentication wiring, `ICurrentUser`, `ISecretProvider`, error middleware, versioning helpers | M |
| 5 | auth-service: schema, seed, login/me/permissions/users/lookup, discovery + JWKS (+ tests) | L |
| 6 | engagement-service domain: state machine, permission policy, create validator (+ unit tests) | M |
| 7 | engagement-service data + endpoints: create, list, get, participants, access, creation-options, seed | L |
| 8 | notification-service: subscribe, `DomainEventLog`, dev endpoint | M |
| 9 | Other services: auth wiring, migrations, DB health checks | S |
| 10 | Gateway routes + compose/env updates; end-to-end smoke via `up.ps1` | M |
| 11 | Frontend: auth, api client, dashboard, create dialog, placeholders | L |
| 12 | Docs: ADRs 0001-0004 (gateway, database-per-service, outbox, access-check approach), README updates, API docs | S |

## 4. Tests (xUnit + Moq, per CLAUDE.md)
**Unit:** every state-machine transition (valid and invalid, by role/status); permission matrix (role x status x capability, including "Viewer never gets attach.view/comment.view" and "Approved is read-only"); create validator (each rule); login (hash verify, generic 401, claims); versioning helper (base64 round trip); `IdempotentHandler` (second delivery skipped, work rolled back on failure); outbox dispatcher (publishes then marks; failure leaves unpublished and increments attempts).
**Integration (`*.IntegrationTests`, category `Integration`):** outbox written in the same transaction as the engagement (rollback leaves no event); duplicate message processed once; list scope/filters/counts against a real SQL Server; rowversion conflict helper returns 409.
`dotnet test --filter "Category!=Integration"` stays fast; the integration run needs Docker.

## 5. Verification (how you check Phase 1 manually)
1. `dotnet build; dotnet test` (all green).
2. `./infra/local/up.ps1`; all 6 health checks green, each now including its own database.
3. Run the demo script in section 1.
4. `docker compose --profile apps logs notification-service` shows the handled events with the correlation id.

## 6. Risks specific to Phase 1
| Risk | Mitigation |
|---|---|
| Service Bus emulator quirks with processors/filters | Keep the subscriber host small; verify early in step 3; fall back to the in-memory bus for tests |
| JWT validation across docker/localhost (issuer and metadata URLs differ) | Fixed issuer string `auditflow-auth` independent of host; metadata URL configurable per environment |
| Services start before auth-service/SQL/emulator | DbUp and subscriber retry with backoff; JwtBearer fetches keys lazily |
| Eventual consistency of seeded events | Phase 1 only logs them; Phase 2 workflow provisions from the same events |
| Scope creep into Phase 2 | Tabs, attachments, PATCH and submit stay out; policy and state machine are pure and complete, but only creation is wired |
| Local-only signing key and seed passwords | Dev-grade by design; documented; key read via `ISecretProvider` so Key Vault can replace it |

## 7. Decisions needed (defaults in bold)
1. **Creation-options catalogue:** period types **Quarterly, Half-Yearly, Annual**; regions **EMEA, APAC, AMER**; config types **Equities, Fixed Income, Derivatives, FX**; options **`includeReconciliation`, `requiresLegalReview` (booleans)**. Phase 2 rules will key off these.
2. **Integration tests:** **Testcontainers (hermetic)** vs reuse the compose SQL Server.
3. **Dev-only events endpoint** in notification-service: **yes**, removed in Phase 3.
4. **Participant role derived from the user's home role:** **yes** (the main plan's assumption).
5. **Frontend libraries:** **react-router-dom + TanStack Query, no UI kit.**
6. **`PATCH /engagements/{id}`:** **defer to Phase 2** (it arrives with 409 handling and the `If-Match` UX).
7. **Non-participant access:** **403** (as in the main plan) vs 404.
8. **Token storage:** **`sessionStorage`** (simple, survives refresh) vs in-memory only (safer, logs out on refresh).
9. **`ISecretProvider` with Key Vault implementation now:** **yes**, small, covers the stack requirement.
10. **Git flow (confirmed):** `git fetch`, `git checkout main`, `git pull origin main`, then a new branch `feature/phase-1-foundation` from `main`; commit per step, no push until you say. The Phase 0 branch must be merged into `main` first, otherwise the new branch will lack the scaffold.

## 8. Delivered (status after implementation)

All 12 steps are implemented. Differences from the plan above:

- **Outbox backoff and subscriber readiness (added).** A from-scratch run against the real emulator showed that the outbox gave up after
  ~5 s of bus downtime and that a subscriber started before its subscription existed did not recover. The outbox now backs off
  (1 s doubling to 30 s, 100 attempts, head-of-line blocking to keep order; migration `0002_outbox_backoff.sql`) and the subscriber
  waits for its subscription. See ADR 0003.
- **Subscription filters** are correlation rules on the message Subject (one per event type), not SQL filters.
- **List query values** (`scope`, `status`) are parsed case-insensitively.
- **Tests:** unit tests (BuildingBlocks 13, Auth 11, Engagement 154) and Testcontainers integration tests (20). Frontend has no automated tests.
- **Decisions** 1-10 of section 7 were taken as defaults. ADRs 0001-0004 are in `docs/decisions`.
- **Known follow-ups:** concurrent handlers can reorder events for one engagement (matters from Phase 3); the Key Vault secret
  provider is implemented but not exercised against a real vault; `PATCH /engagements/{id}` arrives in Phase 2.
