using AuditFlow.BuildingBlocks.Hosting;

// Infrastructure only: routing, CORS, correlation, telemetry. No business logic.
var builder = WebApplication.CreateBuilder(args);
builder.AddAuditFlowDefaults("gateway");

builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithExposedHeaders("X-Correlation-Id", "ETag")));

var app = builder.Build();
app.UseAuditFlowDefaults();
app.UseCors();
app.MapReverseProxy();
app.Run();

public partial class Program;
