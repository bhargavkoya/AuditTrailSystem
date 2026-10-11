-- Rule sets are immutable per version; an engagement keeps the version it was provisioned with.
CREATE TABLE dbo.RuleSets
(
    Version   INT            NOT NULL CONSTRAINT PK_RuleSets PRIMARY KEY,
    Json      NVARCHAR(MAX)  NOT NULL,
    IsActive  BIT            NOT NULL,
    CreatedAt DATETIMEOFFSET NOT NULL
);

-- One row per provisioned engagement: rule inputs that do not live in Tab 1 (period, region), the pinned rule set
-- version, and a rowversion that moves when configuration changes. Also the per-engagement write lock.
CREATE TABLE dbo.EngagementConfigurations
(
    EngagementId   NVARCHAR(32)   NOT NULL CONSTRAINT PK_EngagementConfigurations PRIMARY KEY,
    PeriodType     NVARCHAR(30)   NOT NULL,
    Region         NVARCHAR(30)   NOT NULL,
    RuleSetVersion INT            NOT NULL,
    ProvisionedAt  DATETIMEOFFSET NOT NULL,
    RowVer         ROWVERSION     NOT NULL,
    CONSTRAINT FK_EngagementConfigurations_RuleSets FOREIGN KEY (RuleSetVersion) REFERENCES dbo.RuleSets (Version)
);

-- The concurrency unit: every save and every visibility flip moves RowVer.
CREATE TABLE dbo.WorkflowSections
(
    EngagementId    NVARCHAR(32)     NOT NULL,
    SectionKey      NVARCHAR(50)     NOT NULL,
    TabNo           TINYINT          NOT NULL,
    IsVisible       BIT              NOT NULL,
    UpdatedAt       DATETIMEOFFSET   NOT NULL,
    UpdatedByUserId UNIQUEIDENTIFIER NULL,
    UpdatedByName   NVARCHAR(200)    NULL,
    RowVer          ROWVERSION       NOT NULL,
    CONSTRAINT PK_WorkflowSections PRIMARY KEY (EngagementId, SectionKey),
    CONSTRAINT FK_WorkflowSections_Configurations FOREIGN KEY (EngagementId) REFERENCES dbo.EngagementConfigurations (EngagementId),
    CONSTRAINT CK_WorkflowSections_Tab CHECK (TabNo BETWEEN 1 AND 5)
);

-- Values only. Labels, types shown to users, required flags and rules come from the rule set at read time.
-- Values of hidden sections are kept.
CREATE TABLE dbo.SectionFields
(
    EngagementId NVARCHAR(32)  NOT NULL,
    SectionKey   NVARCHAR(50)  NOT NULL,
    FieldKey     NVARCHAR(50)  NOT NULL,
    FieldType    NVARCHAR(20)  NOT NULL,
    ValueText    NVARCHAR(4000) NULL,
    ValueBool    BIT           NULL,
    ValueDate    DATE          NULL,
    CONSTRAINT PK_SectionFields PRIMARY KEY (EngagementId, SectionKey, FieldKey),
    CONSTRAINT FK_SectionFields_Sections FOREIGN KEY (EngagementId, SectionKey) REFERENCES dbo.WorkflowSections (EngagementId, SectionKey)
);

-- Last published validation outcome; lets ValidationSummaryChanged be emitted only when something changed.
CREATE TABLE dbo.ValidationSummaries
(
    EngagementId  NVARCHAR(32)   NOT NULL CONSTRAINT PK_ValidationSummaries PRIMARY KEY,
    Errors        INT            NOT NULL,
    Warnings      INT            NOT NULL,
    Infos         INT            NOT NULL,
    ReadyToSubmit BIT            NOT NULL,
    Hash          NVARCHAR(32)   NOT NULL,
    UpdatedAt     DATETIMEOFFSET NOT NULL,
    CONSTRAINT FK_ValidationSummaries_Configurations FOREIGN KEY (EngagementId) REFERENCES dbo.EngagementConfigurations (EngagementId)
);

-- Fed by AttachmentAdded so "attachment required" rules never read the attachment database.
-- No foreign key: the event can arrive before the engagement is provisioned.
CREATE TABLE dbo.AttachmentPresence
(
    EngagementId NVARCHAR(32)   NOT NULL,
    ScopeKey     NVARCHAR(80)   NOT NULL,   -- engagement | tab:3 | section:section-2-1
    AddedAt      DATETIMEOFFSET NOT NULL,
    CONSTRAINT PK_AttachmentPresence PRIMARY KEY (EngagementId, ScopeKey)
);
