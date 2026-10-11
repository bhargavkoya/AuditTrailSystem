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
| `EngagementStatusChanged` | engagement | workflow, attachment (cache eviction), review, notification | **published in Phase 2** (StartWork / StartRework); Submit / Approve / Reject in Phase 3 |
| `EngagementSubmitted` | engagement | review, notification | declared, Phase 3 |
| `ReviewerCommentAdded` | review | notification | declared, Phase 3 |
| `ReviewApproved` / `ReviewRejected` | review | engagement, notification | declared, Phase 3 |

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

## Subscriptions (`infra/local/servicebus/Config.json`)

| Subscription | Receives |
|---|---|
| `notification` | all events |
| `engagement` | SectionUpdated, ConfigurationChanged, ReviewApproved, ReviewRejected |
| `workflow` | EngagementCreated, EngagementUpdated, AttachmentAdded, EngagementStatusChanged |
| `attachment` | EngagementCreated (demo files), EngagementStatusChanged |
| `review` | EngagementSubmitted, EngagementStatusChanged |

Filters are correlation rules on the message Subject (one rule per event type).
