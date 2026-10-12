using AuditFlow.Attachment.Api.Clients;
using AuditFlow.Attachment.Api.Data;
using AuditFlow.Attachment.Api.Domain;
using AuditFlow.Attachment.Api.Endpoints;
using AuditFlow.Attachment.Api.Handlers;
using AuditFlow.Attachment.Api.Storage;
using AuditFlow.BuildingBlocks.Access;
using AuditFlow.BuildingBlocks.Hosting;
using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.BuildingBlocks.Security;
using AuditFlow.Contracts.Events;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
builder.AddAuditFlowDefaults("attachment-service");
builder.AddAuditFlowAuthentication();
builder.AddAuditFlowEventBus();
builder.AddAuditFlowData(typeof(Program).Assembly);   // own database + shared outbox / processed-message tables
builder.AddAuditFlowMessaging(publishes: true, consumes: true);
builder.AddEngagementAccessClient();

builder.Services.Configure<FileStoreOptions>(builder.Configuration.GetSection(FileStoreOptions.Section));
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = UploadRules.MaxBytes + 1024 * 1024);

builder.Services.AddSingleton<IFileStore, LocalFileStore>();
builder.Services.AddSingleton<IAttachmentRepository, AttachmentRepository>();
builder.Services.AddScoped<AttachmentWriter>();
builder.Services.AddScoped<AttachmentService>();
builder.Services.AddHttpClient<IWorkflowClient, WorkflowClient>(client =>
{
    client.BaseAddress = builder.ServiceUrl("Workflow", "http://localhost:5003");
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddEventHandler<EngagementCreatedPayload, SeedAttachmentsHandler>(EventTypes.EngagementCreated);

var app = builder.Build();
app.UseAuditFlowDefaults();
app.MapAttachmentEndpoints();
app.Run();

public partial class Program;
