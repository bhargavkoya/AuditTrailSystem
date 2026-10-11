-- One row per submission of an engagement (cycle 1 = first submission, 2 = after one rejection and rework, ...).
CREATE TABLE dbo.ReviewCycles
(
    EngagementId    NVARCHAR(32)     NOT NULL,
    Cycle           INT              NOT NULL,
    SubmittedBy     UNIQUEIDENTIFIER NULL,
    SubmittedByName NVARCHAR(200)    NULL,
    SubmittedAt     DATETIMEOFFSET   NOT NULL,
    CONSTRAINT PK_ReviewCycles PRIMARY KEY (EngagementId, Cycle)
);

-- Tab-level reviewer comments: flat, append-only, per review cycle.
CREATE TABLE dbo.TabComments
(
    CommentId    UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_TabComments PRIMARY KEY,
    EngagementId NVARCHAR(32)     NOT NULL,
    Cycle        INT              NOT NULL,
    TabNo        INT              NOT NULL CONSTRAINT CK_TabComments_Tab CHECK (TabNo BETWEEN 1 AND 5),
    AuthorUserId UNIQUEIDENTIFIER NOT NULL,
    AuthorName   NVARCHAR(200)    NOT NULL,
    Body         NVARCHAR(2000)   NOT NULL CONSTRAINT CK_TabComments_Body CHECK (LEN(LTRIM(RTRIM(Body))) > 0),
    CreatedAt    DATETIMEOFFSET   NOT NULL,
    CONSTRAINT FK_TabComments_Cycle FOREIGN KEY (EngagementId, Cycle) REFERENCES dbo.ReviewCycles (EngagementId, Cycle)
);
CREATE INDEX IX_TabComments_Engagement ON dbo.TabComments (EngagementId, Cycle, TabNo, CreatedAt);

-- The auditable record of approve / reject. One decision per cycle (UNIQUE); a rejection needs a comment (CHECK).
CREATE TABLE dbo.ReviewDecisions
(
    DecisionId     UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_ReviewDecisions PRIMARY KEY,
    EngagementId   NVARCHAR(32)     NOT NULL,
    Cycle          INT              NOT NULL,
    Decision       NVARCHAR(20)     NOT NULL CONSTRAINT CK_ReviewDecisions_Decision CHECK (Decision IN (N'Approved', N'Rejected')),
    ReviewerUserId UNIQUEIDENTIFIER NOT NULL,
    ReviewerName   NVARCHAR(200)    NOT NULL,
    Comment        NVARCHAR(2000)   NULL,
    DecidedAt      DATETIMEOFFSET   NOT NULL,
    CONSTRAINT UQ_ReviewDecisions_Cycle UNIQUE (EngagementId, Cycle),
    CONSTRAINT FK_ReviewDecisions_Cycle FOREIGN KEY (EngagementId, Cycle) REFERENCES dbo.ReviewCycles (EngagementId, Cycle),
    CONSTRAINT CK_ReviewDecisions_RejectComment CHECK (Decision <> N'Rejected' OR LEN(LTRIM(RTRIM(ISNULL(Comment, N'')))) > 0)
);
