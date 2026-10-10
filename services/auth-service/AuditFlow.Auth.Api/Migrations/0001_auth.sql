CREATE TABLE dbo.Roles
(
    RoleName NVARCHAR(20) NOT NULL CONSTRAINT PK_Roles PRIMARY KEY
);

INSERT INTO dbo.Roles (RoleName) VALUES (N'Auditor'), (N'CoAuditor'), (N'Reviewer'), (N'Viewer');

CREATE TABLE dbo.Users
(
    UserId       UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
    Email        NVARCHAR(256)    NOT NULL CONSTRAINT UQ_Users_Email UNIQUE,
    DisplayName  NVARCHAR(200)    NOT NULL,
    PasswordHash NVARCHAR(500)    NOT NULL,
    HomeRole     NVARCHAR(20)     NOT NULL CONSTRAINT FK_Users_Roles REFERENCES dbo.Roles (RoleName),
    CreatedAt    DATETIMEOFFSET   NOT NULL CONSTRAINT DF_Users_CreatedAt DEFAULT SYSDATETIMEOFFSET()
);

-- Dev-grade key storage. With Key Vault configured, the key is read through ISecretProvider instead.
CREATE TABLE dbo.SigningKeys
(
    KeyId      NVARCHAR(64)   NOT NULL CONSTRAINT PK_SigningKeys PRIMARY KEY,
    PrivatePem NVARCHAR(MAX)  NOT NULL,
    CreatedAt  DATETIMEOFFSET NOT NULL CONSTRAINT DF_SigningKeys_CreatedAt DEFAULT SYSDATETIMEOFFSET()
);
