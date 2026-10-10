using AuditFlow.BuildingBlocks.Hosting;
using AuditFlow.BuildingBlocks.Security;

var builder = WebApplication.CreateBuilder(args);
builder.AddAuditFlowDefaults("workflow-service");
builder.AddAuditFlowAuthentication();
builder.AddAuditFlowData(typeof(Program).Assembly);   // own database + shared outbox / processed-message tables

var app = builder.Build();
app.UseAuditFlowDefaults();
app.Run();

public partial class Program;
