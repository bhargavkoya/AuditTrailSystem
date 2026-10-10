CREATE SEQUENCE dbo.EngagementSeq AS INT START WITH 1001 INCREMENT BY 1;

CREATE TABLE dbo.Engagements
(
    EngagementId    NVARCHAR(32)     NOT NULL CONSTRAINT PK_Engagements PRIMARY KEY,   -- 'ENG-1001'
    Name            NVARCHAR(200)    NOT NULL,
    PeriodType      NVARCHAR(30)     NOT NULL,
    Region          NVARCHAR(30)     NOT NULL,
    OwnerUserId     UNIQUEIDENTIFIER NOT NULL,
    ReviewerUserId  UNIQUEIDENTIFIER NOT NULL,
    Status          NVARCHAR(20)     NOT NULL,
    SubmissionCount INT              NOT NULL CONSTRAINT DF_Engagements_SubmissionCount DEFAULT 0,
    CreatedAt       DATETIMEOFFSET   NOT NULL,
    LastActivityAt  DATETIMEOFFSET   NOT NULL,
    RowVer          ROWVERSION       NOT NULL,
    CONSTRAINT CK_Engagements_Status CHECK (Status IN (N'Draft', N'InProgress', N'UnderReview', N'Approved', N'Rejected'))
);

CREATE INDEX IX_Engagements_Status ON dbo.Engagements (Status);

CREATE TABLE dbo.EngagementParticipants
(
    EngagementId NVARCHAR(32)     NOT NULL,
    UserId       UNIQUEIDENTIFIER NOT NULL,
    Role         NVARCHAR(20)     NOT NULL,
    DisplayName  NVARCHAR(200)    NOT NULL,   -- snapshot taken from auth-service at creation (no cross-service reads)
    AddedAt      DATETIMEOFFSET   NOT NULL,
    CONSTRAINT PK_EngagementParticipants PRIMARY KEY (EngagementId, UserId),
    CONSTRAINT FK_EngagementParticipants_Engagements FOREIGN KEY (EngagementId) REFERENCES dbo.Engagements (EngagementId),
    CONSTRAINT CK_EngagementParticipants_Role CHECK (Role IN (N'Auditor', N'CoAuditor', N'Reviewer', N'Viewer'))
);

-- Dashboard: "my engagements".
CREATE INDEX IX_EngagementParticipants_User ON dbo.EngagementParticipants (UserId, EngagementId) INCLUDE (Role);
