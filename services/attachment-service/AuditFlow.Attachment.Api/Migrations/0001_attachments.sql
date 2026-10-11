-- Only the latest file per scope is current; replaced files are marked not current and removed from the store (no history).
CREATE TABLE dbo.Attachments
(
    AttachmentId     UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Attachments PRIMARY KEY,
    EngagementId     NVARCHAR(32)     NOT NULL,
    ScopeType        NVARCHAR(12)     NOT NULL,
    TabNo            TINYINT          NULL,
    SectionKey       NVARCHAR(50)     NULL,
    ScopeKey         AS (CASE ScopeType
                             WHEN N'Engagement' THEN N'engagement'
                             WHEN N'Tab'        THEN N'tab:' + CAST(TabNo AS NVARCHAR(3))
                             ELSE                    N'section:' + SectionKey
                         END) PERSISTED,
    FileName         NVARCHAR(260)    NOT NULL,
    ContentType      NVARCHAR(100)    NOT NULL,
    SizeBytes        BIGINT           NOT NULL,
    StorageKey       NVARCHAR(200)    NOT NULL,   -- generated GUID path, never derived from the user-supplied name
    UploadedByUserId UNIQUEIDENTIFIER NOT NULL,
    UploadedByName   NVARCHAR(200)    NOT NULL,
    UploadedAt       DATETIMEOFFSET   NOT NULL,
    IsCurrent        BIT              NOT NULL,
    CONSTRAINT CK_Attachments_Scope CHECK (
        (ScopeType = N'Engagement' AND TabNo IS NULL     AND SectionKey IS NULL) OR
        (ScopeType = N'Tab'        AND TabNo BETWEEN 1 AND 5 AND SectionKey IS NULL) OR
        (ScopeType = N'Section'    AND TabNo IS NULL     AND SectionKey IS NOT NULL))
);

-- The database enforces "latest only": at most one current row per scope.
CREATE UNIQUE INDEX UX_Attachments_Current ON dbo.Attachments (EngagementId, ScopeKey) WHERE IsCurrent = 1;

CREATE INDEX IX_Attachments_Engagement ON dbo.Attachments (EngagementId) WHERE IsCurrent = 1;
