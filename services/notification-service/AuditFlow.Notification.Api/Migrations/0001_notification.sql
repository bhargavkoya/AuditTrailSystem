-- Every domain event this service has received. Becomes the SSE replay source (Last-Event-ID) in Phase 3.
CREATE TABLE dbo.DomainEventLog
(
    Seq           BIGINT IDENTITY(1,1) NOT NULL,
    EventId       UNIQUEIDENTIFIER     NOT NULL,
    EventType     NVARCHAR(100)        NOT NULL,
    EngagementId  NVARCHAR(32)         NOT NULL,
    Envelope      NVARCHAR(MAX)        NOT NULL,
    CorrelationId NVARCHAR(64)         NULL,
    OccurredAt    DATETIMEOFFSET       NOT NULL,
    ReceivedAt    DATETIMEOFFSET       NOT NULL CONSTRAINT DF_DomainEventLog_ReceivedAt DEFAULT SYSDATETIMEOFFSET(),
    CONSTRAINT PK_DomainEventLog PRIMARY KEY CLUSTERED (Seq),
    CONSTRAINT UQ_DomainEventLog_EventId UNIQUE (EventId)
);

CREATE INDEX IX_DomainEventLog_Engagement ON dbo.DomainEventLog (EngagementId, Seq);
