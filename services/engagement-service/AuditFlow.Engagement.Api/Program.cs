using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Hosting;
using AuditFlow.BuildingBlocks.Security;
using AuditFlow.Engagement.Api.Clients;
using AuditFlow.Engagement.Api.Data;
using AuditFlow.Engagement.Api.Domain;
using AuditFlow.Engagement.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);
builder.AddAuditFlowDefaults("engagement-service");
builder.AddAuditFlowAuthentication();
builder.AddAuditFlowEventBus();
builder.AddAuditFlowData(typeof(Program).Assembly);
builder.AddAuditFlowMessaging(publishes: true, consumes: false);

builder.Services.Configure<CreationCatalogue>(builder.Configuration.GetSection("Engagement:Catalogue"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IEngagementRepository, EngagementRepository>();
builder.Services.AddScoped<EngagementService>();
builder.Services.AddScoped<IDataSeeder, DemoEngagementSeeder>();
builder.Services.AddHttpClient<IUserDirectoryClient, UserDirectoryClient>(client =>
    client.BaseAddress = new Uri(builder.Configuration["Services:Auth:BaseUrl"] ?? "http://localhost:5001"));

var app = builder.Build();
app.UseAuditFlowDefaults();
app.MapEngagementEndpoints();
app.Run();

public partial class Program;
