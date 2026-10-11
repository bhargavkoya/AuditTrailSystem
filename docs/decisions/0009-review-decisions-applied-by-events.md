# 0009: Review decisions are recorded in review-service and applied by engagement-service

Status: accepted (Phase 3).

## Context
Only engagement-service may write `Engagements.Status` (the one state machine). Approve / reject are reviewer actions that must be
permission-gated and auditable, and the rejection comment is mandatory.

## Decision
- **review-service records the decision** (`ReviewDecisions`: one row per engagement and review cycle, `UNIQUE (EngagementId, Cycle)`,
  `CHECK` that a rejection has a non-blank comment) and writes `ReviewApproved` / `ReviewRejected` to its outbox in the same
  transaction. The row plus the persisted event are the audit trail (who, when, which cycle, correlation id).
- **engagement-service applies it** by consuming the event and running `EngagementStateMachine.Evaluate` (assigned reviewer,
  Under Review) with the guarded `UPDATE ... WHERE Status = @from`, then publishes `EngagementStatusChanged`. A decision for an
  engagement that is no longer Under Review, or from someone who is not the assigned reviewer, is logged and ignored; redelivery is
  absorbed by the idempotent consumer pipeline.
- **Status is eventually consistent** (about a second), as for work started / rework started (ADR 0006). The approve / reject
  response is `202` with the decision; the UI waits for the `EngagementStatusChanged` stream event with timed refetches as a fallback.
- **Events carry ids only.** The rejection comment is not in the event; consumers read it from review-service behind its access check.
- **Review cycles.** `EngagementSubmitted` opens cycle N in review-service (`ReviewCycles`); comments and the decision belong to a
  cycle, so a second round after a rejection starts clean while the history keeps both.
- **Submit** (engagement-service) asks workflow-service for readiness synchronously (5 s timeout, no retry) and refuses with 422 plus
  the blocking issues. `If-Match` is optional on submit: ordinary saves also touch the engagement row, so a strict version check would
  reject most submits; the status guard (`WHERE Status = 'InProgress'`) is what makes a double submit safe.
- **Review screen** is composed live from engagement-service (header, capabilities) and workflow-service (tabs, validation summary)
  with the caller's token; a failing tab degrades to `available: false`. Auditors may open it read-only; Viewers get 403.

## Consequences
- Review-service never touches the engagement database and cannot move the status itself; a bug there can at worst record a decision
  that engagement-service ignores.
- A reviewer's decision shows as "recorded, status updating" for a moment.
- Known gap (accepted for the POC): an edit can slip in between the readiness check and the submit transition.
