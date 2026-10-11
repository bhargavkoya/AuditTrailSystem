# Phase 2: Workflow Core (FR-4, FR-5, FR-6, FR-8, FR-12): detailed plan

Status: **delivered** (see section 9 for what changed during the build). Builds on `implementation-plan.md` and the delivered Phase 1 (`phase-1-plan.md` section 8).
Branch: `feature/phase-2-workflow-core`.

## 1. Goal and demo

> Open an engagement, work through the five tabs, and see the backend drive everything: Tab 1 choices reveal or hide sections,
> validation banners (Error / Warning / Information) update on every save, files attach at section / tab / engagement level,
> reconciliation variances surface before submission, and two people editing the same section get a clean 409.

**Demo script (5 minutes)**
1. Open **ENG-1002 (In Progress)** as Alice: five tabs with issue counts; banners list what is incomplete; click a banner to jump to the field.
2. **Tab 1:** untick *Derivatives*, save. The *Derivatives* sections disappear from Tabs 2 and 3 and their errors vanish; tick it again and they return (with their values).
3. **Tab 2:** enter a book value and a ledger value 8% apart; save. **Tab 5** shows the variance as *Significant* and a banner blocks readiness.
4. **Attach** a file to a section, to Tab 3 and to the engagement; upload a second file at the same scope: only the latest is shown.
5. **Concurrency:** open the same section in two browsers, save in the first, then the second: *"Someone else changed this section"* (409), reload keeps your draft visible.
6. **Roles:** Victor (Viewer) sees the content read-only and **no attachment or comment UI**; Rachel (Reviewer) sees everything read-only; **ENG-1003 (Under Review)** is locked for Alice (403 on save).
7. **Status:** the first edit on **ENG-1001 (Draft)** moves it to *In Progress* (event-driven), and the dashboard shows it.

**Out of scope for Phase 2:** submit and review actions, comments, SSE (Phase 3); observability dashboards, load test (Phase 4); rule-editing UI and
document history (non-goals). The UI refreshes by refetching after a save; live push from other users arrives in Phase 3.

## 2. How the pieces fit

```
Browser -> gateway -> workflow-service   tabs, sections, fields, rules, validation, reconciliation   (owns the workflow data)
                   -> attachment-service upload / list / download, latest-only                       (owns attachment metadata + files)
                   -> engagement-service PATCH details, status changes driven by events

events:  EngagementCreated --> workflow    (provision tabs/sections/config)
         EngagementUpdated --> workflow    (period/region are rule inputs)
         SectionUpdated, ConfigurationChanged --> engagement   (Draft -> In Progress, Rejected -> In Progress)
         AttachmentAdded   --> workflow    (required-attachment rules, revalidate)
         EngagementStatusChanged --> workflow, attachment      (drop cached permissions)
         ValidationSummaryChanged --> notification (logged)
```
Every write path asks engagement-service what the caller may do (`/internal/engagements/{id}/access`, ADR 0004), now through a shared client with a short cache.

## 3. Deliverables by area

### 3.1 BuildingBlocks and contracts
| Piece | Purpose |
|---|---|
| `IEngagementAccessClient` | typed client for the internal access endpoint; forwards the caller's token; maps 403/404; **per-engagement cache (30 s)** with eviction by `EngagementStatusChanged` handlers |
| `IfMatch` helper | reads `If-Match` into a rowversion; missing -> 422, stale -> 409 (existing `ConcurrencyException` carries the current version) |
| Contracts (`contracts/api`) | workflow DTOs (`TabsDto`, `TabViewDto`, `SectionViewDto`, `FieldViewDto`, `IssueDto`, `ValidationSummaryDto`, `ReconciliationRowDto`, patch requests), `AttachmentDto`; new action `editDetails` |
| Contracts (`contracts/events`) | payloads for `ConfigurationChanged`, `SectionUpdated`, `ValidationSummaryChanged`, `AttachmentAdded`, now **published** (declared in Phase 1); `EngagementUpdated`, `EngagementStatusChanged` become live |

### 3.2 workflow-service (database `AuditFlow_Workflow`)
**Tables**
| Table | Columns (key points) | Notes |
|---|---|---|
| `RuleSets` | `Version` PK, `Json`, `IsActive` | version 1 seeded from an embedded JSON file; no editing UI (non-goal) |
| `EngagementConfigurations` | `EngagementId` PK, `PeriodType`, `Region`, `ConfigTypes` json, `Options` json, `RuleSetVersion`, `RowVer` | snapshot of the rule inputs; an engagement keeps the rule set version it was provisioned with |
| `WorkflowTabs` | `(EngagementId, TabNo)`, `Title` | five rows per engagement |
| `WorkflowSections` | `(EngagementId, SectionKey)`, `TabNo`, `Title`, `SortOrder`, `IsVisible`, `UpdatedAt/By`, **`RowVer`** | **the concurrency unit**; every save touches it so the version moves |
| `SectionFields` | `(EngagementId, SectionKey, FieldKey)`, `FieldType`, `ValueText/Bool/Date` | values only; labels, types, required flags come from the rule set at read time |
| `ValidationIssues` | `EngagementId`, `Scope` (Field/Section/Tab/Engagement), `TabNo`, `SectionKey`, `FieldKey`, `Severity`, `Code`, `RuleId`, `Message`, `IsBlocking` | replaced as a set on each recompute |
| `ValidationSummaries` | `EngagementId` PK, counts, `ReadyToSubmit`, `Hash` | change detection so `ValidationSummaryChanged` is emitted only when something changed |
| `ReconciliationSummaries` | `(EngagementId, ComparisonKey)`, expected, actual, value / percent variance, `Indicator`, `IsSignificant`, `IsMissing` | recomputed with validation |
| `AttachmentPresence` | `(EngagementId, ScopeKey)` | fed by `AttachmentAdded`; lets rules say "attachment required" without reading the attachment database |
| outbox / processed-message | shared | existing |

Deviation from the main plan: `WorkflowTabs` carries no rowversion. Concurrency is per **section** (what users edit); the tab-level PATCH is an
all-or-nothing batch of section patches, each carrying its own version.

**Rule set (Open Decision 3, section 6.3)**: a versioned JSON document with a section catalogue, field definitions and rules, evaluated by pure code:
- `IRuleEvaluator`: facts (`cfg.*` flags, `opt.*` flags, `periodType`, `region`, field values) in -> section visibility, required flags and rule-driven issues out.
- Conditions: `equals`, `in`, `isTrue`, `isEmpty`, `gt` / `lt` (numeric and date), `not`, `all`, `any`. Effects: `visibleWhen`, `requiredWhen`, `attachment.requiredWhen`, and `rules[]` that raise a message with a severity, scope and target when their condition holds.
- `IValidationEngine`: evaluator output + built-in checks (type / format, max length) -> issues at field, section, tab and engagement scope; `ReadyToSubmit = no Errors`.
- `IReconciliationCalculator`: see 6.1.

**Section catalogue (investment-banking audit flavour; all editable in the JSON)**
| Tab | Section | Visible when | Key fields and rules |
|---|---|---|---|
| 1 Input Configuration | `section-1-1` Engagement setup | always | config type checkboxes (Equities, Fixed Income, Derivatives, FX), options (include reconciliation, requires legal review), period start / end dates, scope summary. Rules: at least one config type (Error); end >= start (Error) |
| 2 Data Collection | `section-2-1` Source data extract | always | source system, extract date (required); record count (number); **attachment required** (Error) |
| | `section-2-2` .. `2-5` Equities / Fixed income / Derivatives / FX positions | the matching config type | book value (required), ledger value (required when reconciliation is on); numeric format |
| 3 Audit Procedures | `section-3-1` Standard procedures | always | procedures completed (checkbox, required), performed by, performed on (required), observations |
| | `section-3-2` Derivatives model validation | Derivatives | model validated (checkbox, required), notes (documentation, required) |
| | `section-3-3` Legal review | requires legal review | reviewer, sign-off date (required), **attachment required** |
| 4 Documentation & Notes | `section-4-1` Audit summary | always | summary (documentation): required (Error), under 50 characters (Warning) |
| | `section-4-2` Exceptions and notes | always | notes; Warning when a variance exists and notes are empty |
| 5 Reconciliation & Submission | `section-5-1` Reconciliation | include reconciliation | computed comparison rows; comment required (Error) when any variance is significant |
| | `section-5-2` Submission readiness | always | computed summary (read-only) |

Hidden sections keep their saved values (they come back when the section is re-enabled) but are excluded from validation and from the UI.

**Endpoints** (via the gateway; authorization uses the engagement access check)
| Endpoint | Rule | Notes |
|---|---|---|
| `GET /engagements/{id}/tabs` | `engagement.view` | `{ready, tabs[{tabNo,title,issueCounts}]}`; `ready:false` while provisioning (UI polls) |
| `GET /engagements/{id}/tabs/{tabNo}` | `engagement.view` | full view model: visible sections only, each field with `type, label, value, required, editable, issues`; per section `version`, `editable`, `attachment {allowed, required, canUpload, hasCurrent}` (**omitted for Viewers**); computed reconciliation rows on Tab 5 |
| `PATCH /engagements/{id}/sections/{sectionKey}` | `content.edit`, `If-Match` | body `{fields:{key:value}}`; returns the updated section, validation summary and visibility changes; 409 on a stale version; 422 for unknown / hidden fields or bad types; no-op when nothing changed (no event, no version bump) |
| `PATCH /engagements/{id}/tabs/{tabNo}` | `content.edit` | body `{sections:[{sectionKey, version, fields}]}`; atomic across sections |
| `GET /engagements/{id}/validation-summary` | `engagement.view` | counts, `readyToSubmit`, issues for the banners, reconciliation rows |
| `GET /internal/engagements/{id}/submission-readiness` | internal | `{readyToSubmit, errors}`; built now because validation lives here, consumed by Phase 3 submit |
| `GET /internal/engagements/{id}/sections/{sectionKey}` | internal | `{exists, visible, tabNo, attachmentAllowed}` for attachment-service |

**Behaviour of a save (one transaction):** check access and the section version -> apply field changes -> re-evaluate the whole engagement
(rules, visibility, required flags, reconciliation) -> replace issues -> update visibility of other sections if Tab 1 changed -> write outbox events
(`SectionUpdated` with `changedFields` and the new version; `ConfigurationChanged` with `affectedSections` when Tab 1 changed;
`ValidationSummaryChanged` only if the summary hash changed). Events carry versions and names, never field values.

**Consumers:** `EngagementCreated` (provision, idempotent; for `seeded` engagements also writes sample data matching their status),
`EngagementUpdated` (refresh period / region facts and re-evaluate), `AttachmentAdded` (update presence, revalidate),
`EngagementStatusChanged` (evict the access cache).

### 3.3 attachment-service (database `AuditFlow_Attachment`)
- `Attachments(AttachmentId, EngagementId, ScopeType Engagement|Tab|Section, TabNo?, SectionKey?, ScopeKey (computed), FileName, ContentType, SizeBytes, StorageKey, UploadedByUserId, UploadedByName, UploadedAt, IsCurrent)` with a **filtered unique index `(EngagementId, ScopeKey) WHERE IsCurrent = 1`**: only the latest file per scope is current, no history.
- `IFileStore` (Open Decision 2): `LocalFileStore` on a Docker volume; keys are GUID paths, never user-supplied names. Azure Blob is documented, not built.
- Endpoints: `POST /engagements/{id}/attachments`, `POST /engagements/{id}/tabs/{tabNo}/attachments`, `POST /engagements/{id}/sections/{sectionKey}/attachments` (multipart `file`; capability `attachment.upload`), `GET /engagements/{id}/attachments` and `.../{attachmentId}/download` (`attachment.view`; **Viewers get 403**).
- Upload: 10 MB limit, file name sanitized and stored as metadata only, content type recorded but not trusted, download served as `attachment` with `nosniff`. A replacement marks the old row not current and deletes the old file after the transaction commits.
- Section scope is validated through workflow-service (`/internal/.../sections/{key}`: must exist, be visible, allow attachments); tab scope is 1 to 5.
- Publishes `AttachmentAdded`; consumes `EngagementStatusChanged` (cache eviction).

### 3.4 engagement-service
- `PATCH /engagements/{id}`: `If-Match`, editors only, **Draft only**; name, period type, region, reviewer (validated like create; changing the reviewer replaces the participant row). Emits `EngagementUpdated`. Quick action `editDetails` returned by the backend only in Draft.
- Starts consuming (`SectionUpdated`, `ConfigurationChanged`): for an editor actor, Draft -> In Progress (`StartWork`) and Rejected -> In Progress (`StartRework`) through the existing state machine, guarded by `WHERE Status = @from`; updates `LastActivityAt`; **first publisher of `EngagementStatusChanged`** (`from, to, reason, version`). Events arriving in other statuses are logged and ignored.
- Seeder: the 5 seeded engagements get varied configurations (so the demo shows different sections).

### 3.5 gateway and compose
Routes: workflow (`/engagements/{id}/tabs/**`, `/sections/**`, `/validation-summary`) and attachment (`/engagements/{id}/attachments/**`, `/tabs/*/attachments`, `/sections/*/attachments`) are more specific than the existing `/engagements/**` route. A request body limit and long timeout for uploads. Compose: attachment file volume, `ServiceBus__SubscriptionName` for engagement / workflow / attachment, `Services__Engagement__BaseUrl` and `Services__Workflow__BaseUrl`. Existing emulator subscriptions already cover Phase 2 events.

### 3.6 Frontend (`features/engagement`)
| Piece | Details |
|---|---|
| `EngagementPage` | header (status, my role, readiness), edit-details dialog when the backend offers `editDetails`, `TabNav` with issue badges, provisioning state ("Setting up workflow…", polls) |
| `ValidationBanners` | Error / Warning / Information stack from `validation-summary`; each banner links to its tab and field |
| `TabView` / `SectionCard` / `FieldRenderer` | renders exactly what the API returns (visibility, editability, required, issues); checkbox, text, date, documentation textarea; per-section dirty tracking; one "Save tab" button (tab PATCH) with a clear saved / failed state |
| `AttachmentPanel` | section / tab / engagement scope; shows the current file, upload replaces it; **not rendered** when the API omits attachment info (Viewer) |
| `ReconciliationTable` | percentage and value variances with indicators, missing values called out |
| `ConflictBanner` | on 409: "Someone else changed this section", reload without losing the user's unsaved text |
| Rule | no role, status or rule logic in the client: `editable`, `visible`, `required`, `canUpload` all come from the API |

## 4. Work breakdown (one commit per step, build and test after each)
| # | Step | Size |
|---|---|---|
| 1 | Branch from updated `main`; this plan | S |
| 2 | BuildingBlocks: access client + cache, `IfMatch`; contracts (DTOs, event payloads, `editDetails`) | M |
| 3 | workflow domain (pure): rule set JSON + models, `RuleEvaluator`, `ValidationEngine`, `ReconciliationCalculator`, value parsing (+ unit tests) | L |
| 4 | workflow data: migration, repositories, provisioning handler, sample-data seeding (+ integration tests) | L |
| 5 | workflow API: tabs, section / tab PATCH with concurrency, validation summary, internal endpoints, events (+ integration tests) | L |
| 6 | workflow consumers: `EngagementUpdated`, `AttachmentAdded`, `EngagementStatusChanged` | M |
| 7 | engagement-service: PATCH, event handlers, `EngagementStatusChanged`, `editDetails`, seed variety (+ tests) | M |
| 8 | attachment-service: schema, file store, endpoints, workflow client (+ tests) | L |
| 9 | gateway routes + compose; clean-start end-to-end check through the gateway | M |
| 10 | Frontend: tabs, banners, sections, attachments, reconciliation, conflicts | L |
| 11 | Docs: ADRs 0005 (rule engine), 0006 (concurrency and eventual status), 0007 (attachment storage), API and event docs, README | S |
| 12 | Final run (tests, clean stack), open the PR | S |

## 5. Tests (xUnit + Moq; integration with Testcontainers)
**Unit (pure):** every condition operator and `all` / `any` / `not` nesting; section visibility from Tab 1 changes; required-when; issue generation at field, section, tab and engagement scope; severity -> `ReadyToSubmit`; reconciliation (percentage and value thresholds, zero expected value, missing values, rounding); value parsing and type checks; patch diffing (no-op detection); catalogue integrity (unique keys, every rule target exists, no condition references an unknown fact); summary hash stability; access cache (hit, miss, eviction); `IfMatch`; upload validation (size, name, scope).
**Integration (real SQL Server):**
- provisioning is idempotent (same `EngagementCreated` twice gives one set of rows) and seeded engagements get data matching their status;
- section save bumps the version; a stale version returns 409 with the current version; **two parallel saves with the same version: exactly one wins**;
- the tab PATCH is atomic (one stale section rolls everything back);
- a Tab 1 change flips visibility, writes `ConfigurationChanged` with the affected sections and re-validates; hidden values come back when re-enabled;
- `ValidationSummaryChanged` is written only when the summary changed;
- attachment replace keeps one current row per scope (unique index) and `AttachmentAdded` updates presence and validation;
- engagement handlers: Draft -> In Progress, Rejected -> In Progress, `EngagementStatusChanged` written, ignored when Under Review or Approved; `PATCH /engagements` Draft-only with 409.
**Manual end to end:** the demo script on a clean stack (frontend verification stays with you).

## 6. Defaults for the Open Decisions (proposed; you decide)
1. **Reconciliation:** per asset class, *expected* = book value, *actual* = ledger value; value variance = actual - expected; percentage = |variance| / |expected|.
   Indicator is the worst of the percentage and value level: percentage **under 1% OK, 1 to 5% Warning, over 5% Significant**; absolute value **under 10,000 OK, 10,000 to 100,000 Warning, over 100,000 Significant**.
   A missing value is an Error; expected = 0 with a non-zero actual is Significant. Warnings and Significant variances raise issues (Significant is an Error); thresholds live in the rule set.
2. **Attachment storage:** local volume behind `IFileStore`, 10 MB limit, any file type, served as a download.
3. **Rule engine:** the JSON rule set described in 3.2, stored in `RuleSets`, version pinned per engagement.
4. **Comment threading:** unchanged (Phase 3).
5. **Audit trail:** unchanged; events carry actor, time and correlation id; no field-level history.
6. **Approved engagements:** fully locked (writes return 403 because the capability is gone).

## 7. Risks and mitigations
| Risk | Mitigation |
|---|---|
| Rule / template scope creep | small fixed DSL, one JSON catalogue, a catalogue integrity test; no admin UI |
| Eventual consistency: first edit returns before the engagement turns *In Progress*; workflow provisions shortly after create | UI shows a provisioning state and refetches; documented in ADR 0006; SSE in Phase 3 removes the lag |
| Lost updates under concurrency | rowversion check inside the transaction + a parallel-save integration test |
| Recompute cost per save | the whole catalogue is about 13 sections; a single pass in memory; measure in Phase 4 |
| Stale permission cache | 30 s TTL plus eviction on `EngagementStatusChanged`; writes also rely on the state machine guard |
| File handling (size, names, content types, orphans) | limit, sanitized metadata, GUID storage keys, download as attachment with `nosniff`, delete replaced files after commit |
| Cross-service call for section scope (attachment -> workflow) | short timeout; clear 502 on failure; scope rules are tiny and cached briefly |
| Hidden-section values surprising users | documented; they are excluded from validation and display and restored on re-enable |
| Seed data realism | generated from the same catalogue, covered by a test per status |
| Larger frontend surface | one renderer for all fields; no client-side rule logic |

## 8. Decisions needed (defaults in bold)
1. **Section catalogue and wording** as drafted in 3.2 (13 sections). Anything to rename, add or drop for your domain?
2. **Reconciliation thresholds** as in 6.1 (percentage 1% / 5%, value 10,000 / 100,000).
3. **Hidden section values are kept**, not deleted, when Tab 1 hides them.
4. **Edits on a locked engagement return 403** (the capability is absent), while version conflicts return 409.
5. **Tab PATCH is an atomic batch of section patches**; versions live on sections (no tab rowversion).
6. **Draft -> In Progress is event-driven and slightly delayed** until Phase 3 SSE (the alternative is a synchronous call from workflow to engagement).
7. **`PATCH /engagements/{id}`:** name, period type, region and reviewer, **Draft only**.
8. **Attachments:** 10 MB, any type, local volume.
9. **Extras beyond the PRD list:** internal endpoints (`submission-readiness`, section policy) and the `editDetails` action.
10. **Git flow:** commits per step on `feature/phase-2-workflow-core`; push and PR when you say.

## 9. Delivered: deviations from this plan

The ten decisions in section 8 were accepted with their defaults. While building, these things differed from the text above:

| Plan | Built | Why |
|---|---|---|
| `WorkflowTabs` table | none; tabs come from the rule set | five static rows per engagement carried no information |
| `EngagementConfigurations.ConfigTypes/Options` json | `PeriodType`, `Region`, `RuleSetVersion`, `RowVer` only | Tab 1 fields are the single source of the configuration |
| `ValidationIssues`, `ReconciliationSummaries` tables | not stored; evaluated on every read and save (ADR 0005); `ValidationSummaries` keeps only the counts and hash | no stale rows, less code; the engine is pure and cheap |
| (not planned) per-engagement lock | `UPDLOCK, HOLDLOCK` on the engagement configuration row for every write transaction (ADR 0006) | the whole-engagement recompute needs a stable snapshot |
| (not planned) `Recomputer` | shared by saves and by the `AttachmentAdded` / `EngagementUpdated` consumers | one place persists visibility, the configuration version and the summary |
| Tab 1 provisioning mapping | a `source` property on Tab 1 fields (`config:Equities`, `option:includeReconciliation`) | keeps the mapping in the rule set |
| `409` body | adds `resource` (the conflicting section) next to `currentVersion` | a tab save touches several sections |
| Polly retry on internal calls | 5 s timeout, no retry | not worth a dependency for the POC |
| Seeded attachments | attachment-service consumes `EngagementCreated` (seeded) and attaches sample files; its subscription gained that rule | late-stage seeded engagements validate cleanly through the real event path |
| Playwright / UI run | the UI is type-checked and built, not exercised in a browser | you verify the UI yourself |

Known gaps (accepted for the POC): the validation summary still lists "attachment required" messages for Viewers (no file names or
content, but the existence of a requirement); a crash between writing a file and committing its row can leave an orphan file;
`EngagementUpdated` arriving before provisioning is retried by the broker rather than buffered.
