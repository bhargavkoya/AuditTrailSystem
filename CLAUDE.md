# AuditFlow (P3 POC)

Base-level interview POC: a backend-controlled audit engagement management platform for financial
(investment-banking) audit workflows. The full spec is docs/PRD.md. Read it fully before planning or coding.
If this file and the PRD disagree, the PRD wins; tell me.
Goal: a demoable skeleton I can screen-share in 5 minutes and discuss fluently (service boundaries,
event-driven messaging, SSE, optimistic concurrency, backend-controlled UI, observability).
It mirrors my Globant audit-modernization work. NOT production. Do not over-engineer.

## Stack (from the PRD)
- Frontend: React + Vite + TypeScript + Tailwind CSS
- Backend: ASP.NET Core Web API (.NET 8) microservices, Dapper, SQL Server
- Messaging: Azure Service Bus topics/queues behind an IEventBus abstraction
  (local dev: Service Bus emulator in Docker, which needs SQL Server anyway; in-memory implementation for tests)
- Real-time: Server-Sent Events (no SignalR)
- Auth: OAuth 2.0 + JWT with role claims (a local OAuth/OIDC-style issuer is fine for the POC)
- Secrets: Azure Key Vault behind an abstraction (user-secrets/env locally)
- Observability: Azure Application Insights via OpenTelemetry/SDK, with a console/OTLP fallback so everything runs without an Azure account
- Azure Functions: the PRD mentions them for async scale-out. OPTIONAL for the POC. Propose at most one function consuming a Service Bus topic, and ask me.

## Architecture (PRD: "microservices only, no modular monolith fallback")
Real deployable services, each with its own database/schema and no cross-service DB reads:
- auth-service: OAuth, JWT issuance, role claims, access checks
- engagement-service: engagement create/retrieve/list, participants, status lifecycle
- workflow-service: tabs, sections, field values, rule-driven section visibility, validation logic
- attachment-service: attachment references/metadata, scoped access control
- review-service: review screen data, tab comments, approve/reject, review history
- notification-service: domain event routing and SSE streaming
Observability/platform is a cross-cutting layer (shared library + telemetry config), not a business service.
An API gateway/BFF (e.g. YARP) is allowed as infrastructure only; propose it in the plan and ask me.
Sync HTTP for user-driven reads/writes; async events via Service Bus; SSE for the browser.
Keep each service minimal: only the endpoints and tables its responsibility needs.

## Repo layout (from the PRD)
frontend/{apps/web, features/{dashboard,engagement,review}, shared}
services/{auth,engagement,workflow,review,attachment,notification}-service
contracts/{api,events}   infra/{local,docker,azure}   docs/{architecture,api,decisions}   tests/

## Domain rules (PRD; do not deviate)
- Engagement is the top-level object (workflow, sections, attachments, validations, comments, status, review actions).
- Roles: Auditor, Co-Auditor, Reviewer, Viewer. Roles apply PER ENGAGEMENT via EngagementParticipant.
  - Auditor/Co-Auditor (identical permissions): create engagements, edit all tabs, upload attachments at any level, submit.
  - Reviewer (chosen at engagement creation): never edits content; reads everything; tab-level comments; approve/reject from the separate review screen.
  - Viewer: view engagement details only. CANNOT see attachments or comments.
- Lifecycle: Draft -> In Progress -> Under Review -> Approved | Rejected. Rejected -> In Progress after auditor rework.
  Approved is final and read-only for standard workflow actions. One backend state machine enforces every transition.
- Five tabs: 1 Input Configuration, 2 Data Collection, 3 Audit Procedures, 4 Documentation & Notes, 5 Reconciliation & Submission.
- Tab 1 drives downstream behaviour: section visibility and validation are determined by rule-based configuration stored in backend logic/config, never hardcoded in the client. Changes to tab 1 immediately affect downstream sections.
- Sections hold: checkboxes, text, dates, documentation text, attachment uploads. Validation applies at field, section and tab level.
- Validation messages: Error / Warning / Information, shown as top-level banners. A validation-summary endpoint exists. Submission only succeeds when required validations pass.
- Attachments at section, tab and whole-engagement level. Only the LATEST uploaded file is visible (no version history).
- Reconciliation tab: percentage-based and value-based comparisons, generalized variance indicators, significant variances and missing values surfaced before submission. No production-grade financial formulas needed.
- Review screen is a distinct view: read-only content, tab-level comments, approve, reject with MANDATORY comment, review-decision history summary, validation summary.
- Dashboard: summary cards per status, search and filters (owned / assigned / reviewing / participating), engagement list (ID, name, region, period type, reviewer, status, last updated, my role), role-sensitive quick actions.
- Backend-controlled UI: the backend is the single source of truth for workflow progression, permissions, validation outcomes, section visibility and status. The frontend renders backend-provided state and owns no business decisions.
- Concurrency: optimistic concurrency with row versioning (SQL Server rowversion); 409 on conflict; version identifiers in update responses and SSE payloads. Target ~100 concurrent users; do not block threads.
- Events: publish only after the transaction commits (outbox pattern). Events are scoped to changed sections/actions and carry only what consumers need.
  PRD event types: EngagementCreated, EngagementUpdated, ConfigurationChanged, SectionUpdated, AttachmentAdded, ValidationSummaryChanged, EngagementSubmitted, ReviewerCommentAdded, ReviewApproved, ReviewRejected, EngagementStatusChanged.
  SSE sends only changed sections or domain events, never the full engagement. Clients reconcile incoming updates against UI state without bypassing backend authority.
- Review actions are permission-gated and auditable (ReviewDecision + DomainEvent persisted).

## PRD API surface (starting point; refine in the plan)
Auth: POST /auth/login, GET /auth/me, GET /auth/permissions
Engagement: POST/GET /engagements, GET/PATCH /engagements/{id}, POST /engagements/{id}/submit, GET /engagements/{id}/participants
Workflow: GET /engagements/{id}/tabs, GET/PATCH /engagements/{id}/tabs/{tabId}, PATCH /engagements/{id}/sections/{sectionId}, GET /engagements/{id}/validation-summary
Attachments: POST /engagements/{id}/attachments, POST /tabs/{tabId}/attachments, POST /sections/{sectionId}/attachments, GET /engagements/{id}/attachments
Review: GET /reviews/{engagementId}, POST /reviews/{engagementId}/comments, /approve, /reject
SSE: GET /engagements/{id}/events/stream

## Non-goals (do NOT build)
Document version history, advanced reporting/analytics, admin-facing rule-engine management UI,
field-level audit trail playback, complex external integrations.

## PRD "Open Decisions" (propose a default for each in the plan; I decide)
1. Reconciliation formulas and thresholds   2. Attachment storage strategy / physical file store
3. Rule-engine representation for tab 1      4. Comment threading model
5. Audit-trail depth for the MVP             6. Whether Approved engagements are fully locked or partially editable

## Conventions
- C#: nullable enabled, async all the way, CancellationToken, constructor DI, SOLID, appropriate design patterns
- Dapper with explicit SQL and a simple migration tool (DbUp or FluentMigrator); no EF Core
- Idempotent event consumers (store processed message ids); correlation id flows through HTTP, messages, logs and traces
- Shared contracts live in contracts/ (API + versioned events); services share contracts, not code internals
- Seed demo data: users covering all four roles, and one engagement per lifecycle state
- Tests: xUnit + Moq for the state machine, permission rules, validation rules, rule-driven section visibility, concurrency conflicts and idempotent consumers
- Small commits, clear messages

## Commands
- ./infra/local/up.ps1       (SQL Server, Service Bus emulator, gateway and all services in Docker)
- docker compose up -d       (infrastructure only: SQL Server, Service Bus emulator)
- dotnet test                (add --filter "Category!=Integration" to skip the Docker-based integration tests)
- cd frontend && npm install; cd apps/web && npm run dev

## Working agreement
- Plan before coding on any new phase; wait for my approval
- After each phase: build, run tests, and tell me exactly how to verify manually
- If the PRD is ambiguous, ask me; don't silently decide
- Don't add anything outside the PRD's MVP functional requirements (FR-1 to FR-12) and the non-goals above

