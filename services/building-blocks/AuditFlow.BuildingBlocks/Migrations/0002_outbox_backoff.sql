-- Exponential backoff for failed publishes: a row is retried no earlier than NextAttemptAt.
ALTER TABLE dbo.OutboxMessage ADD NextAttemptAt DATETIMEOFFSET NULL;
