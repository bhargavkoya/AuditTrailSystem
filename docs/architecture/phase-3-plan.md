# Phase 3: Review and Real-Time (FR-7, FR-9, FR-10, FR-11): detailed plan

Status: **delivered** (see section 8 for what changed during the build). Builds on `implementation-plan.md` and the delivered Phases 1-2.
Branch: `feature/phase-3-review-realtime` (cut from `main` after Phase 2's PR #3 merged).

## 1. Goal and demo

> Submit an engagement, review it on a separate screen, approve or reject with a mandatory comment, rework and resubmit,
> and watch other users' browsers update live over SSE without ever receiving field values.

**Demo script (5 minutes)**
1. **Alice** (Auditor) opens **ENG-1002 (In Progress)** with blocking errors: *Submit* returns 422 and the banner lists the errors. Fix them, submit: status becomes **Under Review**, every edit control is locked.
2. **Rachel** (Reviewer) opens the **review screen** (distinct colour scheme): read-only tabs, validation summary, adds **tab-level comments** on two tabs.
3. Rachel clicks **Reject with no comment**: 400, the UI shows the server message. With a comment: engagement becomes **Rejected**, history shows the decision.
4. Two browsers (Alice and Co-Auditor Carl) on the same engagement: Alice edits a section, Carl's page shows *"updated by Alice"* and refetches **only that section** within about a second. A payload sniffed in dev tools contains names, versions and field **keys**, never values.
5. Alice's first edit moves **Rejected -> In Progress** (already built in Phase 2); she resubmits (cycle 2); Rachel approves: **Approved**, everything read-only, history shows both cycles.
6. **Victor** (Viewer): sees tab content and validation summary, but his stream never carries `AttachmentAdded` or `ReviewerCommentAdded`, and `/reviews/{id}` is 403.
7. Kill the browser's network for 10 s and restore: the stream reconnects with `Last-Event-ID` and replays what was missed.

**Out of scope:** observability dashboards, load test, CI (Phase 4); SignalR (never); comment replies / resolve (decision 4 of Open Decisions: flat comments); reviewer editing anything.

## 2. How the pieces fit

```
Browser -> gateway -> engagement-service  POST /submit (sync readiness call to workflow), state machine, applies review decisions
                   -> review-service      GET review view, POST comments / approve / reject  (owns comments + decisions)
                   -> notification-service GET /engagements/{id}/events/stream  (SSE)

sync:    engagement --(forward token)--> workflow /internal/.../submission-readiness
         review     --(forward token)--> engagement /internal/.../access, workflow tabs + validation-summary (compose the screen)
events:  EngagementSubmitted    --> review (open cycle N), notification
         ReviewerCommentAdded   --> notification (not Viewers)
         ReviewApproved/Rejected--> engagement (state machine applies), notification
         EngagementStatusChanged--> review (drop cached access), workflow, attachment, notification
         *all events*           --> notification: DomainEventLog -> SSE tailer -> subscribers
```

## 3. Deliverables by area

### 3.1 Submit (engagement-service, FR-9)
- `POST /engagements/{id}/submit` with `If-Match`. Access check (`submit` capability), then sync call to workflow `GET /internal/engagements/{id}/submission-readiness` (5 s timeout, no retry, 502 on failure).
- `EngagementStateMachine.Evaluate(InProgress, Submit, ValidationPassed)` (already built and tested). Not ready -> **422** ProblemDetails with the Error list from readiness.
- Persist `UPDATE ... SET Status, SubmissionCount = SubmissionCount + 1 WHERE Status = @from AND RowVer = @ver`; 0 rows -> 409. Outbox in the same transaction: `EngagementSubmitted{submittedBy, cycle}` and `EngagementStatusChanged{InProgress -> UnderReview, reason: Submit}`.
- Response: new `EngagementDetailDto` (status, version, allowedActions). Known gap kept from the master plan: an edit can slip in between readiness and transition (documented, not fixed).
- New handlers: `ReviewApproved` / `ReviewRejected` -> `Trigger.Approve` / `Trigger.Reject` through the state machine; the actor is the reviewer carried in the event; ignored and logged if the engagement is not Under Review (idempotent, plus the `ProcessedMessage` guard). Each emits `EngagementStatusChanged`.

### 3.2 review-service (FR-10): new
Database `AuditFlow_Review` with outbox and processed-message tables from `AddAuditFlowData`, plus:

| Table | Columns / constraints |
|---|---|
| `ReviewCycles` | `EngagementId, Cycle` PK, `SubmittedBy, SubmittedAt` (created by the `EngagementSubmitted` consumer) |
| `TabComments` | `CommentId, EngagementId, TabNo (1-5), Cycle, AuthorUserId, AuthorName, Body, CreatedAt`; append-only; index `(EngagementId, Cycle, TabNo)` |
| `ReviewDecisions` | `DecisionId, EngagementId, Cycle, Decision ('Approved'/'Rejected'), ReviewerUserId, ReviewerName, Comment, DecidedAt`; `UNIQUE (EngagementId, Cycle)`; `CHECK (Decision <> 'Rejected' OR LEN(LTRIM(RTRIM(Comment))) > 0)` |

Endpoints (all through the gateway; `/reviews/{engagementId}` per the PRD):

| Endpoint | Rule |
|---|---|
| `GET /reviews/{engagementId}` | participant who is Reviewer / Auditor / Co-Auditor (Viewer 403). Returns `{engagement header, tabs (read-only view models), validationSummary, comments per tab (current cycle + earlier), history[], canComment, canDecide, cycle}` |
| `POST /reviews/{engagementId}/comments` | `comment` capability (assigned Reviewer, Under Review); body `{tabNo, body}`; non-empty, max 2,000 chars; emits `ReviewerCommentAdded{commentId, tabNo, authorId}` |
| `POST /reviews/{engagementId}/approve` | `review` capability; inserts the decision + `ReviewApproved` in one transaction |
| `POST /reviews/{engagementId}/reject` | `review` capability; **comment mandatory -> 400** (validator, plus the CHECK constraint as a backstop); `ReviewRejected` |

- Composition: calls engagement `/internal/.../access` (shared cached client), workflow `GET /engagements/{id}/tabs` and `/validation-summary` with the caller's token. If a section call fails the response marks that tab `unavailable` instead of failing the whole screen (degrade by section, per the risk register).
- Duplicate or late decisions: the unique index returns **409** ("already decided for this cycle"); an `UnderReview` precheck returns 403 (capability absent).
- Approve / reject return `202`-style body `{decisionId, cycle, decision}`; engagement status is applied by engagement-service from the event, so it flips within about a second (the UI waits for the `EngagementStatusChanged` SSE event, with a refetch fallback). This is the "eventual status" trade-off already recorded in ADR 0006.
- Seeded data: review-service consumes the seeded `EngagementCreated` (adds a subscription rule, same pattern as attachment-service) and writes: cycle 1 + two tab comments for the Under Review engagement; cycle 1 + comments + decision for Approved and Rejected.

### 3.3 notification-service SSE (FR-11)
- `GET /engagements/{id}/events/stream`, `text/event-stream`, Bearer auth (fetch-based client; no ticket fallback, see decision 5).
- On connect: `/internal/.../access` -> 403 if no role; the capabilities decide the filter (no `attach.view` -> drop `AttachmentAdded`; no `comment.view` -> drop `ReviewerCommentAdded`). Viewers therefore never see either.
- **Delivery source = `DomainEventLog`.** A singleton `EventLogTailer` (`BackgroundService`) polls `Seq > lastSeq` every 250 ms and fans out to per-engagement bounded `Channel<EventFrame>` subscribers. Because it reads committed rows, nothing uncommitted is ever pushed, and the same code path serves live and replay.
- Frame: `id: <Seq>`, `event: <EventType>`, `data: <envelope JSON>`. Envelopes already carry only names, versions and ids (Phase 2 contracts), so no field values leave the service.
- Reconnect: `Last-Event-ID` -> replay `Seq > id` for that engagement (cap 500). Cap exceeded, row purged or subscriber channel overflow (capacity 256) -> `event: resync`, the client refetches tabs + validation summary.
- Heartbeat comment every 15 s; token expiry -> `event: reauth` then close; `X-Accel-Buffering: no`, `Cache-Control: no-cache`.
- The Phase 1 `/dev/events/recent` endpoint is removed (as promised in `Program.cs`).

### 3.4 Gateway
- `/reviews/{**rest}` -> review; `/engagements/{id}/events/stream` -> notification (Order 0, above the catch-all, with a long `ActivityTimeout` and no response buffering). Test early, this is the riskiest infrastructure piece.
- Compose: review and notification subscriptions get the new filter rules (review: `EngagementCreated`; engagement: already has `ReviewApproved` / `ReviewRejected`); review-service and notification-service wired with service URLs.

### 3.5 Contracts
- `contracts/api`: `SubmitResponse` (reuses `EngagementDetailDto`), `ReviewViewDto`, `TabCommentDto`, `AddCommentRequest`, `ReviewDecisionRequest` / `ReviewDecisionDto`, `ReviewHistoryItemDto`.
- `contracts/events`: payloads for `EngagementSubmitted`, `ReviewerCommentAdded`, `ReviewApproved`, `ReviewRejected` (v1; ids and names only, the reject comment text is **not** in the event: consumers read it from review-service).

### 3.6 Frontend (UI is type-checked and built only; you verify it)
- `shared/stream/useEngagementStream.ts`: `@microsoft/fetch-event-source`, token header, backoff, `Last-Event-ID`, handles `resync` / `reauth`. Reconciliation rules from the master plan: ignore if the cached version >= event version; otherwise invalidate **only that section's query**; if the user has unsaved edits in that section show *"changed by X, reload?"* instead of overwriting. `EngagementStatusChanged` refetches the engagement header (status, `allowedActions`).
- `features/engagement`: `SubmitBar` (renders from `allowedActions`; a 422 shows the backend error list as banners; no readiness logic in the client), live indicator.
- `features/review`: `ReviewLayout` (distinct colour scheme), `ReadOnlyTabView` (reuses the section renderer with `editable: false` from the backend), `TabCommentThread` (form only when `canComment`), `DecisionPanel` (buttons only when `canDecide`; reject shows the server's 400), `ReviewHistory`, `ValidationSummaryPanel`. Replaces the Phase 1 placeholder `ReviewPage`.
- Dashboard: refetch when the user returns to it (no per-row SSE in this phase).

### 3.7 Tests (xUnit + Moq)
- **State machine / permissions** (extend existing): approve/reject by non-assigned reviewer, reject without comment, Submit from every other state.
- **Submit service:** readiness failing -> 422 with errors; success bumps `SubmissionCount` and emits both events in one transaction; stale `If-Match` -> 409; workflow down -> 502.
- **Review service:** reject without comment -> 400; comment outside Under Review / by non-reviewer / by Viewer; second decision same cycle -> 409; history ordering; per-tab degrade when workflow fails; comment max length.
- **Idempotent consumers:** `EngagementSubmitted` (review), `ReviewApproved` / `ReviewRejected` (engagement, including wrong-state ignore), redelivery inserts nothing twice.
- **SSE:** capability-based filtering (Viewer never gets attachment/comment events), replay after `Last-Event-ID`, `resync` on overflow and on a gap, payload contains no field values, heartbeat.
- **Integration (Docker, `Category=Integration`):** submit -> review comment -> reject -> rework edit -> resubmit -> approve across real services and the emulator, asserting the stream frames received by a second client.

## 4. Implementation steps (small commits, build + tests after each)

| Step | Work |
|---|---|
| 3.1 | Contracts (API DTOs, event payloads) + docs stubs |
| 3.2 | engagement-service submit endpoint + readiness client + events; tests |
| 3.3 | review-service data layer + migrations + `EngagementSubmitted` consumer; tests |
| 3.4 | review-service endpoints (comments, approve, reject, composed `GET`); tests |
| 3.5 | engagement-service `ReviewApproved` / `ReviewRejected` handlers; tests |
| 3.6 | review seed data via seeded `EngagementCreated`; subscription rules |
| 3.7 | notification SSE: tailer, hub, endpoint, filtering, replay, heartbeat; tests; remove dev endpoint |
| 3.8 | Gateway routes (reviews, SSE unbuffered) + compose wiring; smoke test of the stream through the gateway |
| 3.9 | Frontend: `useEngagementStream`, `SubmitBar`, live reconciliation |
| 3.10 | Frontend: review screen |
| 3.11 | Integration tests, ADRs (0008 SSE from the event log, 0009 review decisions applied via events), `docs/api` and event docs, README walkthrough, plan deviations |

## 5. How you will verify (given at the end of the phase)
`./infra/local/up.ps1`, log in as Alice / Carl / Rachel / Victor in separate browser profiles and follow the demo script in section 1; `dotnet test --filter "Category!=Integration"` for the unit suites and `dotnet test` for the Docker ones; `curl -N` against the gateway stream URL to watch raw frames.

## 6. Risks

| Risk | Mitigation |
|---|---|
| YARP buffers or times out the SSE response | dedicated route, long `ActivityTimeout`, tested in step 3.8 before any UI work |
| Browser 6-connection limit per origin (HTTP/1.1) | one stream per open engagement; closed on navigation |
| Tailer polling latency / load | 250 ms indexed `Seq` query, bounded batch; fine for ~100 users; documented limit for multi-instance (one tailer per instance, each fans out to its own subscribers) |
| Status flips late after approve/reject (event-applied) | UI waits for `EngagementStatusChanged`, refetch fallback; ADR 0006 already accepts eventual status |
| Edit slips in between readiness check and submit | known gap, accepted; the fix is to revalidate inside the transition |
| Reviewer sees stale tab data | review screen always reads live from workflow; locked while Under Review, so content cannot change under the reviewer |
| Reject comment leaking through events | event carries `decisionId` only; text stays in review-service behind the access check |
| Seed realism for review history | generated by the same seeded-event path as attachments, one test per status |

## 7. Decisions (all accepted with the defaults in bold)
1. **Approve / reject are applied by engagement-service from the `ReviewApproved` / `ReviewRejected` events** (eventual, about 1 s), as in the master plan. Alternative: review-service calls engagement-service synchronously (instant status, tighter coupling).
2. **SSE fan-out reads committed rows from `DomainEventLog` with a 250 ms tailer.** Alternative: push from the consumer handler (lower latency, but risks pushing before commit).
3. **The review screen composes tab content live from workflow-service** with the caller's token (per-tab degrade). Alternative: review-service keeps its own snapshot at submit time (more data, stale-proof history, more code).
4. **Auditors / Co-Auditors can open the review screen read-only** (to read reviewer comments after a rejection); it shows no comment form or decision buttons. Viewers get 403.
5. **No SSE ticket fallback**: the fetch-based client sends the Bearer header, so the ticket endpoint from the master plan is dropped.
6. **A Reviewer may open the review screen before submission** (read-only, `canComment=false`), per the accepted PRD default.
7. **Decisions carry no `If-Match`**; double decisions are blocked by `UNIQUE(EngagementId, Cycle)` (409) instead.
8. **Comment limits:** flat, append-only, 2,000 characters, per tab and per cycle; earlier cycles are shown collapsed.
9. **Dashboard** refreshes on navigation only; no live push there in Phase 3.
10. **Git flow:** commits per step on `feature/phase-3-review-realtime`; push and PR when you say.

## 8. Delivered: deviations from this plan

| Plan | Built | Why |
|---|---|---|
| Submit with `If-Match`, stale -> 409 | `If-Match` optional; the guard is `WHERE Status = 'InProgress'` (a stale version is still a 409 when sent) | every ordinary save touches the engagement row, so a strict check rejected most submits; the status guard already makes double submits safe (ADR 0009) |
| `ReviewViewDto` tab list | adds `available` per tab; `validation` may be null | degrade by section instead of failing the screen |
| Reject without comment = 400, invalid comment = 400 | reject without comment is 400; an invalid comment (bad tab, blank, too long) is **422** like every other validation in the repo | consistency with the existing error mapping |
| (not planned) | new `409 Conflict` exception type, `viewReview` action, `422` body carries `issues[]` | review conflicts needed their own mapping; auditors need a backend-provided link to the read-only review screen; the UI lists the blocking issues verbatim |
| SSE: `ready` not mentioned | `ready` frame (with the current position) on a fresh connection | closes the gap between the first fetch and the stream opening |
| Heartbeat 15 s, `reauth` on expiry | as planned; `Last-Event-ID` is also accepted as `?lastEventId=` | fetch clients can send either |
| Replay "purged" case | not built (the log is never purged); a client ahead of the log gets `resync` | nothing deletes rows in the POC |
| Dashboard: refetch on navigation only | dashboard also refetches when a status event arrives for an engagement the user has open (shared `['engagements']` key) | free with the stream hook; no per-row streams |
| Gateway SSE test "early" | verified against the Docker stack through the gateway (Alice and Victor streams, live comment / reject / status events, Viewer filtering) | done before the UI work |
| Playwright / UI run | the UI is type-checked and built, not exercised in a browser | you verify the UI yourself |

Known gaps (accepted for the POC): an edit can slip in between the readiness check and the submit transition; `DomainEventLog` is never purged;
the SSE hub is per process; the review screen reads live from workflow-service, so it is only as available as that service (a failing tab degrades,
a failing engagement-service does not).
