# Event contracts

Events are wrapped in an envelope (`AuditFlow.BuildingBlocks.Eventing.EventEnvelope<T>`), serialized as camelCase JSON, and
published to the Service Bus topic `auditflow-events` with `MessageId = eventId` and `Subject = eventType`.

```json
{ "eventId": "guid", "eventType": "EngagementCreated", "schemaVersion": 1, "engagementId": "ENG-1006",
  "occurredAt": "2026-10-10T20:55:51Z", "correlationId": "corr-123",
  "actor": { "userId": "guid", "name": "Alice Auditor" }, "payload": { } }
```

Rules: payloads carry only what consumers need, are scoped to the change, and are versioned through `schemaVersion`.
Events are written to the publisher's outbox in the same transaction as the state change (see
`docs/decisions/0003-outbox-and-idempotent-consumers.md`). Consumers must be idempotent.

## Catalog

| Event | Publisher | Consumers | Status |
|---|---|---|---|
| `EngagementCreated` | engagement | workflow (provisions), attachment (demo files), notification | published (Phase 1) |
| `EngagementUpdated` | engagement | workflow (period / region are rule inputs), notification | **published in Phase 2** (PATCH details) |
| `ConfigurationChanged` | workflow | engagement (work started), notification | **published in Phase 2** (Tab 1 changed) |
| `SectionUpdated` | workflow | engagement (work started), notification | **published in Phase 2** |
| `AttachmentAdded` | attachment | workflow (presence + revalidate), notification | **published in Phase 2** |
| `ValidationSummaryChanged` | workflow | notification | **published in Phase 2**, only when the summary hash changed |
| `EngagementStatusChanged` | engagement | workflow, attachment, review (cache eviction), notification | **published in Phase 2** (StartWork / StartRework); **Phase 3**: Submit, Approve, Reject |
| `EngagementSubmitted` | engagement | review (opens a cycle), notification | **published in Phase 3** |
| `ReviewerCommentAdded` | review | notification (not forwarded to Viewers) | **published in Phase 3** |
| `ReviewApproved` / `ReviewRejected` | review | engagement (applies the transition), notification | **published in Phase 3** |

Type names live in `AuditFlow.Contracts.Events.EventTypes`; payload records in the same project.

## EngagementCreated (schemaVersion 1)

```json
{ "name": "...", "periodType": "Quarterly", "region": "EMEA", "status": "Draft", "seeded": false,
  "ownerUserId": "guid", "reviewerUserId": "guid",
  "participants": [ { "userId": "guid", "role": "Auditor", "displayName": "..." } ],
  "configuration": { "configTypes": ["Equities"], "options": { "includeReconciliation": true } } }
```

`status` and `seeded` let consumers provision seeded engagements in their current state.

## Phase 2 payloads (schemaVersion 1; names and versions only, never field values)

```json
// EngagementUpdated: new rule inputs only when they changed
{ "changedFields": ["name", "region"], "version": "AAAAAAAAB9E=", "periodType": null, "region": "APAC" }
// SectionUpdated
{ "tabNo": 2, "sectionKey": "section-2-2", "changedFields": ["bookValue"], "version": "AAAAAAAAB9F=" }
// ConfigurationChanged: Tab 1 field names that changed and the sections whose visibility flipped
{ "changedKeys": ["cfgDerivatives"], "affectedSections": [ { "sectionKey": "section-2-4", "visible": true } ], "version": "..." }
// ValidationSummaryChanged: version = summary hash
{ "errors": 3, "warnings": 1, "infos": 2, "readyToSubmit": false, "version": "9F3A..." }
// AttachmentAdded
{ "scope": "Section", "tabNo": null, "sectionKey": "section-2-1", "attachmentId": "guid", "fileName": "extract.csv" }
// EngagementStatusChanged
{ "from": "Draft", "to": "InProgress", "reason": "StartWork", "version": "..." }
```

## Phase 3 payloads (schemaVersion 1; ids and names only)

```json
// EngagementSubmitted: cycle = the submission count (1, 2, ...)
{ "submittedBy": "guid", "submittedByName": "Alice Auditor", "cycle": 1 }
// ReviewerCommentAdded: the comment text stays in review-service
{ "commentId": "guid", "tabNo": 2, "authorId": "guid", "cycle": 1 }
// ReviewApproved / ReviewRejected: the decision text (and the mandatory rejection comment) stays in review-service
{ "decisionId": "guid", "reviewerId": "guid", "cycle": 1 }
// EngagementStatusChanged now also carries reason "Submit", "Approve" or "Reject"
{ "from": "InProgress", "to": "UnderReview", "reason": "Submit", "version": "..." }
```

The stream (`GET /engagements/{id}/events/stream`) forwards these envelopes unchanged (see `docs/api`), except that attachment and
comment events are withheld from callers without the matching capability.

## Subscriptions (`infra/local/servicebus/Config.json`)

| Subscription | Receives |
|---|---|
| `notification` | all events |
| `engagement` | SectionUpdated, ConfigurationChanged, ReviewApproved, ReviewRejected |
| `workflow` | EngagementCreated, EngagementUpdated, AttachmentAdded, EngagementStatusChanged |
| `attachment` | EngagementCreated (demo files), EngagementStatusChanged |
| `review` | EngagementCreated (demo review history), EngagementSubmitted, EngagementStatusChanged |

Filters are correlation rules on the message Subject (one rule per event type).
