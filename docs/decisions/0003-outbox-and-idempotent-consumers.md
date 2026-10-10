# 0003: Transactional outbox and idempotent consumers

Status: accepted (Phase 1)

## Context
Events must be published only after the state change commits, and must not be lost or double-applied when the bus or a
service restarts. Writing to the database and publishing to Service Bus cannot share a transaction.

## Decision
**Publishing (outbox).** A service writes an `OutboxMessage` row in the *same transaction* as its state change
(`IOutbox.EnqueueAsync(uow, envelope)`). `OutboxDispatcher` polls unpublished rows in order and publishes through
`IEventBus` (MessageId = EventId), then marks them published.
- Delivery is at-least-once.
- A failing row is retried with exponential backoff (1 s doubling to 30 s, up to 100 attempts) and **blocks newer rows**, so
  events for an engagement are never reordered by a retry.

**Consuming (inbox).** `EventDispatcher` runs, in one transaction: insert `(consumer, messageId)` into `ProcessedMessage`,
then the handlers. A duplicate delivery hits the primary key and is skipped; a handler failure rolls the marker back so the
retry reprocesses. Handlers receive the same unit of work, so their writes commit atomically with the marker.

**Subscriptions.** One topic (`auditflow-events`) with one subscription per consuming service. Filters are correlation rules on
the message Subject (one rule per event type). `ServiceBusSubscriberHost` waits for its subscription to exist before starting the
processor (found necessary when the emulator is still loading).

## Consequences
- Events are visible to consumers shortly after commit, not instantly (poll interval 500 ms).
- Consumers must be idempotent by design; the marker makes that mechanical.
- Concurrent handlers (4) can process two events for the same engagement out of order. Phase 3 live updates must compare
  versions (or use sessions) before relying on arrival order.
- Verified against the real emulator, including a cold start where the bus was not ready for the first publishes.
