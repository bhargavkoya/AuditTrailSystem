using AuditFlow.BuildingBlocks.Access;
using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Hosting;
using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.BuildingBlocks.Security;
using AuditFlow.Contracts.Events;
using AuditFlow.Workflow.Api.Data;
using AuditFlow.Workflow.Api.Domain;
using AuditFlow.Workflow.Api.Endpoints;
using AuditFlow.Workflow.Api.Handlers;

DateOnlyTypeHandler.Register();

var builder = WebApplication.CreateBuilder(args);
builder.AddAuditFlowDefaults("workflow-service");
builder.AddAuditFlowAuthentication();
builder.AddAuditFlowEventBus();
builder.AddAuditFlowData(typeof(Program).Assembly);   // own database + shared outbox / processed-message tables
builder.AddAuditFlowMessaging(publishes: true, consumes: true);
builder.AddEngagementAccessClient();

builder.Services.AddSingleton<IRuleEvaluator, RuleEvaluator>();
builder.Services.AddSingleton<IReconciliationCalculator, ReconciliationCalculator>();
builder.Services.AddSingleton<IValidationEngine, ValidationEngine>();
builder.Services.AddSingleton<IRuleSetProvider, RuleSetProvider>();
builder.Services.AddSingleton<IWorkflowRepository, WorkflowRepository>();
builder.Services.AddScoped<IDataSeeder, RuleSetSeeder>();
builder.Services.AddScoped<ProvisioningService>();
builder.Services.AddScoped<Recomputer>();
builder.Services.AddScoped<WorkflowService>();
builder.Services.AddEventHandler<EngagementCreatedPayload, EngagementCreatedHandler>(EventTypes.EngagementCreated);
builder.Services.AddEventHandler<EngagementUpdatedPayload, EngagementUpdatedHandler>(EventTypes.EngagementUpdated);
builder.Services.AddEventHandler<AttachmentAddedPayload, AttachmentAddedHandler>(EventTypes.AttachmentAdded);
// EngagementStatusChanged (access-cache eviction) is registered by AddEngagementAccessClient.

var app = builder.Build();
app.UseAuditFlowDefaults();
app.MapWorkflowEndpoints();
app.Run();

public partial class Program;
