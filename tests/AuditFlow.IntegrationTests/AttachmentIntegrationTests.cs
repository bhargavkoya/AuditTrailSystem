using System.Text;
using System.Text.Json;
using AuditFlow.Attachment.Api.Clients;
using AuditFlow.Attachment.Api.Data;
using AuditFlow.Attachment.Api.Domain;
using AuditFlow.Attachment.Api.Handlers;
using AuditFlow.Attachment.Api.Storage;
using AuditFlow.BuildingBlocks.Access;
using AuditFlow.BuildingBlocks.Errors;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.BuildingBlocks.Security;
using AuditFlow.Contracts.Api;
using AuditFlow.Contracts.Events;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using static AuditFlow.Contracts.Api.DemoData;

namespace AuditFlow.IntegrationTests;

[Collection(SqlCollection.Name)]
[Trait("Category", "Integration")]
public class AttachmentIntegrationTests(SqlServerFixture sql) : IDisposable
{
    private const string Eng = "ENG-8001";
    private readonly string root = Path.Combine(Path.GetTempPath(), "auditflow-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class Env
    {
        public required TestDatabase Db { get; init; }
        public required LocalFileStore Store { get; init; }
        public required string Root { get; init; }
        public SectionPolicyDto Policy { get; set; } = new(true, true, 2, true);

        public AttachmentService ServiceFor(DemoUser user, string[] capabilities)
        {
            var access = new Mock<IEngagementAccessClient>();
            access.Setup(a => a.GetAccessAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, CancellationToken _) => new EngagementAccessDto(id, user.HomeRole, EngagementStatus.InProgress, "v", capabilities));
            var workflow = new Mock<IWorkflowClient>();
            workflow.Setup(w => w.GetSectionPolicyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Policy);
            var current = new Mock<ICurrentUser>();
            current.SetupGet(u => u.UserId).Returns(user.UserId);
            current.SetupGet(u => u.Name).Returns(user.DisplayName);

            var repository = new AttachmentRepository(Db.Connections);
            var writer = new AttachmentWriter(repository, Store, new SqlOutbox(), TimeProvider.System);
            return new AttachmentService(repository, writer, Store, workflow.Object, access.Object, Db.UnitOfWork, current.Object,
                NullLogger<AttachmentService>.Instance);
        }

        public AttachmentService Auditor => ServiceFor(Alice, WorkflowApi.AuditorCaps);

        public int FilesOnDisk => Directory.Exists(Root) ? Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Length : 0;
    }

    private async Task<Env> CreateAsync()
    {
        var db = await sql.CreateDatabaseAsync(typeof(AttachmentRepository).Assembly);
        return new Env
        {
            Db = db,
            Root = root,
            Store = new LocalFileStore(Options.Create(new FileStoreOptions { Root = root }))
        };
    }

    private static UploadedFile File(string name = "report.pdf", string content = "hello", string? contentType = "application/pdf")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new UploadedFile(name, contentType, bytes.Length, new MemoryStream(bytes));
    }

    private static async Task<string> ReadAsync(FileDownload download)
    {
        using var reader = new StreamReader(download.Content);
        return await reader.ReadToEndAsync();
    }

    // ---- Scopes ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Files_can_be_attached_at_section_tab_and_engagement_level()
    {
        var env = await CreateAsync();
        var service = env.Auditor;

        var section = await service.UploadAsync(Eng, AttachmentScope.Section, null, "section-2-1", File("extract.csv"), default);
        var tab = await service.UploadAsync(Eng, AttachmentScope.Tab, 3, null, File("procedures.docx"), default);
        var engagement = await service.UploadAsync(Eng, AttachmentScope.Engagement, null, null, File("cover.pdf"), default);

        var listed = await service.ListAsync(Eng, default);
        Assert.Equal(3, listed.Count);
        Assert.Equal([AttachmentScope.Engagement, AttachmentScope.Tab, AttachmentScope.Section], listed.Select(a => a.Scope));
        Assert.Equal("section-2-1", section.SectionKey);
        Assert.Equal(3, tab.TabNo);
        Assert.Null(engagement.TabNo);
        Assert.Equal(Alice.DisplayName, section.UploadedByName);
        Assert.Equal(3, env.FilesOnDisk);
    }

    [Fact]
    public async Task A_new_upload_at_the_same_scope_replaces_the_old_one()
    {
        var env = await CreateAsync();
        var service = env.Auditor;
        var first = await service.UploadAsync(Eng, AttachmentScope.Section, null, "section-2-1", File("v1.txt", "first"), default);

        var second = await service.UploadAsync(Eng, AttachmentScope.Section, null, "section-2-1", File("v2.txt", "second"), default);

        var listed = await service.ListAsync(Eng, default);
        Assert.Equal("v2.txt", Assert.Single(listed).FileName);
        Assert.Equal("second", await ReadAsync(await service.DownloadAsync(Eng, second.AttachmentId, default)));
        await Assert.ThrowsAsync<NotFoundException>(() => service.DownloadAsync(Eng, first.AttachmentId, default)); // no history
        Assert.Equal(1, env.FilesOnDisk); // the replaced file is gone
        Assert.Equal(2, await env.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Attachments"));
        Assert.Equal(1, await env.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Attachments WHERE IsCurrent = 1"));
    }

    [Fact]
    public async Task Different_scopes_do_not_replace_each_other()
    {
        var env = await CreateAsync();
        var service = env.Auditor;

        await service.UploadAsync(Eng, AttachmentScope.Section, null, "section-2-1", File(), default);
        await service.UploadAsync(Eng, AttachmentScope.Section, null, "section-2-2", File(), default);
        await service.UploadAsync(Eng, AttachmentScope.Tab, 2, null, File(), default);
        await service.UploadAsync("ENG-OTHER", AttachmentScope.Section, null, "section-2-1", File(), default);

        Assert.Equal(3, (await service.ListAsync(Eng, default)).Count);
        Assert.Single(await service.ListAsync("ENG-OTHER", default));
    }

    [Fact]
    public async Task Parallel_uploads_to_one_scope_leave_exactly_one_current_file()
    {
        var env = await CreateAsync();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            env.Auditor.UploadAsync(Eng, AttachmentScope.Section, null, "section-2-1", File($"file-{i}.txt", $"content {i}"), default)));

        Assert.Equal(1, await env.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Attachments WHERE IsCurrent = 1"));
        Assert.Equal(8, await env.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Attachments"));
        Assert.Equal(1, env.FilesOnDisk);
    }

    [Fact]
    public async Task The_database_itself_refuses_two_current_files_for_one_scope()
    {
        var env = await CreateAsync();
        await env.Auditor.UploadAsync(Eng, AttachmentScope.Engagement, null, null, File(), default);

        await Assert.ThrowsAsync<SqlException>(() => env.Db.ExecuteAsync("""
            INSERT INTO dbo.Attachments (AttachmentId, EngagementId, ScopeType, FileName, ContentType, SizeBytes, StorageKey, UploadedByUserId, UploadedByName, UploadedAt, IsCurrent)
            VALUES (NEWID(), 'ENG-8001', 'Engagement', 'x', 'x', 1, 'k', NEWID(), 'n', SYSDATETIMEOFFSET(), 1)
            """));
    }

    // ---- Events ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_upload_publishes_AttachmentAdded_after_commit_without_file_content()
    {
        var env = await CreateAsync();

        await env.Auditor.UploadAsync(Eng, AttachmentScope.Section, null, "section-2-1", File("extract.csv", "secret-body"), default);
        await env.Auditor.UploadAsync(Eng, AttachmentScope.Tab, 4, null, File("notes.txt"), default);

        Assert.Equal(2, await env.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.OutboxMessage WHERE EventType = 'AttachmentAdded'"));
        var envelope = await env.Db.ScalarAsync<string>("SELECT TOP 1 Envelope FROM dbo.OutboxMessage ORDER BY Seq");
        using var json = JsonDocument.Parse(envelope);
        var payload = json.RootElement.GetProperty("payload");
        Assert.Equal("Section", payload.GetProperty("scope").GetString());
        Assert.Equal("section-2-1", payload.GetProperty("sectionKey").GetString());
        Assert.Equal("extract.csv", payload.GetProperty("fileName").GetString());
        Assert.DoesNotContain("secret-body", envelope);
    }

    // ---- Permissions -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Viewers_cannot_upload_list_or_download()
    {
        var env = await CreateAsync();
        var uploaded = await env.Auditor.UploadAsync(Eng, AttachmentScope.Engagement, null, null, File(), default);
        var viewer = env.ServiceFor(Victor, WorkflowApi.ViewerCaps);

        await Assert.ThrowsAsync<ForbiddenException>(() => viewer.UploadAsync(Eng, AttachmentScope.Engagement, null, null, File(), default));
        await Assert.ThrowsAsync<ForbiddenException>(() => viewer.ListAsync(Eng, default));
        await Assert.ThrowsAsync<ForbiddenException>(() => viewer.DownloadAsync(Eng, uploaded.AttachmentId, default));
    }

    [Fact]
    public async Task Reviewers_can_read_attachments_but_not_upload()
    {
        var env = await CreateAsync();
        var uploaded = await env.Auditor.UploadAsync(Eng, AttachmentScope.Engagement, null, null, File("a.txt", "readable"), default);
        var reviewer = env.ServiceFor(Rachel, WorkflowApi.ReviewerCaps);

        Assert.Single(await reviewer.ListAsync(Eng, default));
        Assert.Equal("readable", await ReadAsync(await reviewer.DownloadAsync(Eng, uploaded.AttachmentId, default)));
        await Assert.ThrowsAsync<ForbiddenException>(() => reviewer.UploadAsync(Eng, AttachmentScope.Engagement, null, null, File(), default));
    }

    [Fact]
    public async Task A_locked_engagement_refuses_uploads()
    {
        var env = await CreateAsync();
        var locked = env.ServiceFor(Alice, [Capabilities.View, Capabilities.AttachView]); // Under Review / Approved: no attachment.upload

        await Assert.ThrowsAsync<ForbiddenException>(() => locked.UploadAsync(Eng, AttachmentScope.Section, null, "section-2-1", File(), default));
        Assert.Equal(0, env.FilesOnDisk);
    }

    // ---- Scope and file validation --------------------------------------------------------------------------

    [Fact]
    public async Task Section_scope_is_validated_through_workflow_service()
    {
        var env = await CreateAsync();

        env.Policy = new SectionPolicyDto(false, false, 0, false);
        await Assert.ThrowsAsync<NotFoundException>(() => env.Auditor.UploadAsync(Eng, AttachmentScope.Section, null, "section-9-9", File(), default));

        env.Policy = new SectionPolicyDto(true, false, 2, true); // hidden by Tab 1
        await Assert.ThrowsAsync<ValidationFailedException>(() => env.Auditor.UploadAsync(Eng, AttachmentScope.Section, null, "section-2-4", File(), default));

        env.Policy = new SectionPolicyDto(true, true, 1, false); // section does not take attachments
        await Assert.ThrowsAsync<ValidationFailedException>(() => env.Auditor.UploadAsync(Eng, AttachmentScope.Section, null, "section-1-1", File(), default));

        Assert.Equal(0, env.FilesOnDisk);
        Assert.Equal(0, await env.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Attachments"));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("a b")]
    [InlineData("")]
    public async Task Hostile_section_keys_are_rejected_before_any_lookup(string key)
    {
        var env = await CreateAsync();

        await Assert.ThrowsAsync<ValidationFailedException>(() => env.Auditor.UploadAsync(Eng, AttachmentScope.Section, null, key, File(), default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public async Task Tab_scope_must_be_one_to_five(int tab)
    {
        var env = await CreateAsync();

        await Assert.ThrowsAsync<ValidationFailedException>(() => env.Auditor.UploadAsync(Eng, AttachmentScope.Tab, tab, null, File(), default));
    }

    [Fact]
    public async Task Empty_and_oversized_files_are_rejected_and_nothing_is_stored()
    {
        var env = await CreateAsync();

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            env.Auditor.UploadAsync(Eng, AttachmentScope.Engagement, null, null, new UploadedFile("empty.txt", "text/plain", 0, new MemoryStream()), default));
        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            env.Auditor.UploadAsync(Eng, AttachmentScope.Engagement, null, null,
                new UploadedFile("big.bin", "application/octet-stream", UploadRules.MaxBytes + 1, new MemoryStream()), default));

        Assert.Equal(0, env.FilesOnDisk);
        Assert.Equal(0, await env.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Attachments"));
    }

    [Fact]
    public async Task File_names_are_sanitized_and_storage_keys_are_generated()
    {
        var env = await CreateAsync();

        var stored = await env.Auditor.UploadAsync(Eng, AttachmentScope.Engagement, null, null,
            File(@"..\..\windows\system32\evil<>.exe", "x", contentType: new string('x', 500)), default);

        Assert.Equal("evil.exe", stored.FileName);
        Assert.Equal("application/octet-stream", stored.ContentType);
        var key = await env.Db.ScalarAsync<string>("SELECT StorageKey FROM dbo.Attachments");
        Assert.Matches($"^{Eng}/[0-9a-f]{{32}}$", key);
        Assert.DoesNotContain("evil", key);
    }

    [Fact]
    public async Task A_failure_after_the_file_is_written_removes_the_file()
    {
        var env = await CreateAsync();
        var repository = new Mock<IAttachmentRepository>();
        repository.Setup(r => r.ReplaceCurrentAsync(It.IsAny<AuditFlow.BuildingBlocks.Data.IUnitOfWork>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<NewAttachment>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database down"));
        var writer = new AttachmentWriter(repository.Object, env.Store, new SqlOutbox(), TimeProvider.System);
        await using var uow = await env.Db.UnitOfWork.BeginAsync(default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.AddAsync(uow,
            new NewAttachment(Eng, AttachmentScope.Engagement, null, null, "a.txt", "text/plain", 1, Alice.UserId, "Alice"),
            new MemoryStream([1]), null, default));

        Assert.Equal(0, env.FilesOnDisk);
    }

    [Fact]
    public async Task Download_of_an_unknown_attachment_is_not_found()
    {
        var env = await CreateAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => env.Auditor.DownloadAsync(Eng, Guid.NewGuid(), default));
        await env.Auditor.UploadAsync(Eng, AttachmentScope.Engagement, null, null, File(), default);
        var other = (await env.Auditor.ListAsync(Eng, default)).Single();
        await Assert.ThrowsAsync<NotFoundException>(() => env.Auditor.DownloadAsync("ENG-OTHER", other.AttachmentId, default)); // wrong engagement
    }

    // ---- Demo seeding ----------------------------------------------------------------------------------------

    private static EventEnvelope<EngagementCreatedPayload> Created(EngagementStatus status, bool seeded, bool legal = false)
        => EventEnvelope<EngagementCreatedPayload>.Create(EventTypes.EngagementCreated, "ENG-8100",
            new EngagementCreatedPayload("Seeded", "Annual", "EMEA", status, seeded, Alice.UserId, Rachel.UserId,
                [new ParticipantInfo(Alice.UserId, EngagementRole.Auditor, Alice.DisplayName)],
                new ConfigurationInfo(["Equities"], new Dictionary<string, bool> { ["requiresLegalReview"] = legal })),
            new EventActor(Alice.UserId.ToString(), Alice.DisplayName));

    private async Task<Env> SeedAsync(EventEnvelope<EngagementCreatedPayload> envelope)
    {
        var env = await CreateAsync();
        var repository = new AttachmentRepository(env.Db.Connections);
        var handler = new SeedAttachmentsHandler(new AttachmentWriter(repository, env.Store, new SqlOutbox(), TimeProvider.System),
            NullLogger<SeedAttachmentsHandler>.Instance);
        await using var uow = await env.Db.UnitOfWork.BeginAsync(default);
        await handler.HandleAsync(envelope, uow, default);
        await uow.CommitAsync(default);
        return env;
    }

    [Theory]
    [InlineData(EngagementStatus.UnderReview, false, 1)]
    [InlineData(EngagementStatus.Rejected, false, 1)]
    [InlineData(EngagementStatus.UnderReview, true, 2)]
    [InlineData(EngagementStatus.Approved, true, 3)]
    public async Task Late_stage_seeded_engagements_get_their_sample_files_and_events(EngagementStatus status, bool legal, int expected)
    {
        var env = await SeedAsync(Created(status, seeded: true, legal));

        Assert.Equal(expected, await env.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Attachments WHERE IsCurrent = 1"));
        Assert.Equal(expected, await env.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.OutboxMessage WHERE EventType = 'AttachmentAdded'"));
        Assert.Equal(expected, env.FilesOnDisk);
    }

    [Theory]
    [InlineData(EngagementStatus.Draft, true)]
    [InlineData(EngagementStatus.InProgress, true)]
    [InlineData(EngagementStatus.Approved, false)] // real engagements never get files from the seeder
    public async Task Other_engagements_get_no_sample_files(EngagementStatus status, bool seeded)
    {
        var env = await SeedAsync(Created(status, seeded));

        Assert.Equal(0, await env.Db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Attachments"));
    }
}
