-- Shared messaging tables. Run by every service in its own database.
CREATE TABLE dbo.OutboxMessage
(
    Seq           BIGINT IDENTITY(1,1) NOT NULL,
    Id            UNIQUEIDENTIFIER     NOT NULL,           -- == envelope EventId == Service Bus MessageId
    EventType     NVARCHAR(100)        NOT NULL,
    EngagementId  NVARCHAR(32)         NOT NULL,
    Envelope      NVARCHAR(MAX)        NOT NULL,
    CorrelationId NVARCHAR(64)         NULL,
    OccurredAt    DATETIMEOFFSET       NOT NULL,
    PublishedAt   DATETIMEOFFSET       NULL,
    Attempts      INT                  NOT NULL CONSTRAINT DF_OutboxMessage_Attempts DEFAULT 0,
    LastError     NVARCHAR(1000)       NULL,
    CONSTRAINT PK_OutboxMessage PRIMARY KEY CLUSTERED (Seq),
    CONSTRAINT UQ_OutboxMessage_Id UNIQUE (Id)
);

CREATE INDEX IX_OutboxMessage_Unpublished ON dbo.OutboxMessage (Seq) WHERE PublishedAt IS NULL;

CREATE TABLE dbo.ProcessedMessage
(
    ConsumerName NVARCHAR(100)  NOT NULL,
    MessageId    UNIQUEIDENTIFIER NOT NULL,
    ProcessedAt  DATETIMEOFFSET NOT NULL,
    CONSTRAINT PK_ProcessedMessage PRIMARY KEY (ConsumerName, MessageId)
);
