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
| `EngagementCreated` | engagement | notification (Phase 1); workflow (Phase 2) | **published in Phase 1** (also for seeded engagements) |
| `EngagementUpdated` | engagement | workflow, notification | declared, Phase 2 |
| `ConfigurationChanged` | workflow | engagement, notification | declared, Phase 2 |
| `SectionUpdated` | workflow | engagement, notification | declared, Phase 2 |
| `AttachmentAdded` | attachment | workflow, notification | declared, Phase 2 |
| `ValidationSummaryChanged` | workflow | notification | declared, Phase 2 |
| `EngagementSubmitted` | engagement | review, notification | declared, Phase 3 |
| `ReviewerCommentAdded` | review | notification | declared, Phase 3 |
| `ReviewApproved` / `ReviewRejected` | review | engagement, notification | declared, Phase 3 |
| `EngagementStatusChanged` | engagement | workflow, attachment, review, notification | declared, Phase 3 |

Type names live in `AuditFlow.Contracts.Events.EventTypes`; payload records in the same project.

## EngagementCreated (schemaVersion 1)

```json
{ "name": "...", "periodType": "Quarterly", "region": "EMEA", "status": "Draft", "seeded": false,
  "ownerUserId": "guid", "reviewerUserId": "guid",
  "participants": [ { "userId": "guid", "role": "Auditor", "displayName": "..." } ],
  "configuration": { "configTypes": ["Equities"], "options": { "includeReconciliation": true } } }
```

`status` and `seeded` let consumers provision seeded engagements in their current state.

## Subscriptions (`infra/local/servicebus/Config.json`)

| Subscription | Receives |
|---|---|
| `notification` | all events |
| `engagement` | SectionUpdated, ConfigurationChanged, ReviewApproved, ReviewRejected |
| `workflow` | EngagementCreated, EngagementUpdated, AttachmentAdded, EngagementStatusChanged |
| `attachment` | EngagementStatusChanged |
| `review` | EngagementSubmitted, EngagementStatusChanged |

Filters are correlation rules on the message Subject (one rule per event type).
