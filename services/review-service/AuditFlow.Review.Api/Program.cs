using AuditFlow.BuildingBlocks.Access;
using AuditFlow.BuildingBlocks.Hosting;
using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.BuildingBlocks.Security;
using AuditFlow.Contracts.Events;
using AuditFlow.Review.Api.Clients;
using AuditFlow.Review.Api.Data;
using AuditFlow.Review.Api.Domain;
using AuditFlow.Review.Api.Endpoints;
using AuditFlow.Review.Api.Handlers;

var builder = WebApplication.CreateBuilder(args);
builder.AddAuditFlowDefaults("review-service");
builder.AddAuditFlowAuthentication();
builder.AddAuditFlowEventBus();
builder.AddAuditFlowData(typeof(Program).Assembly);   // own database + shared outbox / processed-message tables
builder.AddAuditFlowMessaging(publishes: true, consumes: true);
builder.AddEngagementAccessClient();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IReviewRepository, ReviewRepository>();
builder.Services.AddScoped<ReviewService>();
builder.Services.AddHttpClient<IEngagementClient, EngagementClient>(client =>
{
    client.BaseAddress = builder.ServiceUrl("Engagement", "http://localhost:5002");
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHttpClient<IWorkflowViewClient, WorkflowViewClient>(client =>
{
    client.BaseAddress = builder.ServiceUrl("Workflow", "http://localhost:5003");
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddEventHandler<EngagementSubmittedPayload, EngagementSubmittedHandler>(EventTypes.EngagementSubmitted);
builder.Services.AddEventHandler<EngagementCreatedPayload, SeedReviewHandler>(EventTypes.EngagementCreated);

var app = builder.Build();
app.UseAuditFlowDefaults();
app.MapReviewEndpoints();
app.Run();

public partial class Program;
