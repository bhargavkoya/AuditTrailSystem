-- W3C trace context captured when the event is enqueued, so the async consumer's span is a child of the request that caused it.
ALTER TABLE dbo.OutboxMessage ADD TraceParent VARCHAR(64) NULL, TraceState VARCHAR(512) NULL;
