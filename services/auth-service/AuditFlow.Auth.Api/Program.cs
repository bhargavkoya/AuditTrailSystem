using AuditFlow.BuildingBlocks.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.AddAuditFlowDefaults("auth-service");
builder.AddAuditFlowEventBus();

var app = builder.Build();
app.UseAuditFlowDefaults();
app.Run();

public partial class Program;
