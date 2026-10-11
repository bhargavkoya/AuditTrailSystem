# 0008: SSE fans out committed rows from the event log

Status: accepted (Phase 3).

## Context
Browsers need to learn about other people's changes within about a second, without receiving field values, with replay after a
dropped connection, and without the client bypassing backend authorization. notification-service already persists every domain event
(`DomainEventLog`, idempotent on `EventId`).

## Decision
- **Delivery source = the log.** An `EventLogTailer` polls `DomainEventLog` every 250 ms and hands rows to an in-memory `EventHub`
  (one bounded channel per connected browser). Because it reads only committed rows, nothing is pushed before it is durable, and live
  delivery and `Last-Event-ID` replay use the same table and the same `Seq` (the SSE `id`).
  Alternative considered: push from the consumer handler. Lower latency, but the handler runs inside the consumer's transaction and
  would announce events that might still roll back.
- **Out-of-order commits.** `Seq` is an identity column and consumers run concurrently, so a lower `Seq` can commit after a higher one.
  Each poll re-reads 200 rows behind the last published one and skips what it already sent; each connection also remembers what it wrote.
- **Frames carry the envelope** (names, ids, versions). The client re-reads the changed section through the normal API, so the same
  authorization applies as for any read. Callers without `attachment.view` / `comment.view` never receive `AttachmentAdded` /
  `ReviewerCommentAdded`; capabilities come from engagement-service at connect time.
- **Control events.** `ready` (refetch to close the gap since your last read), `resync` (gap over 500 events, or the client fell
  behind its 256-event buffer: refetch everything), `reauth` (token expired). Heartbeat comment every 15 s.
- **Auth.** A fetch-based client (`@microsoft/fetch-event-source`) sends the Bearer header, so the single-use ticket fallback from the
  master plan was not built.
- **Gateway.** A dedicated YARP route for the stream with a 10 minute activity timeout; the 15 s heartbeat keeps it active, and the
  service sets `Cache-Control: no-cache` and disables response buffering.

## Consequences
- Latency is the poll interval (about 250 ms) plus the consumer hop; fine for ~100 users.
- One process fans out in memory. With several instances each runs its own tailer and serves its own browsers (it works because every
  instance reads the same table); a cross-instance bus or SignalR backplane is not needed. Documented, not built.
- The log grows without bound in the POC; a retention job would have to keep the `resync` path in mind.
- Browsers allow about six HTTP/1.1 connections per origin, so the UI opens one stream per open engagement and closes it on navigation.
