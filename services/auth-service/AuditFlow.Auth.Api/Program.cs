using AuditFlow.Auth.Api.Data;
using AuditFlow.Auth.Api.Endpoints;
using AuditFlow.Auth.Api.Security;
using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Hosting;
using AuditFlow.BuildingBlocks.Secrets;
using AuditFlow.BuildingBlocks.Security;
using Microsoft.AspNetCore.Identity;

var builder = WebApplication.CreateBuilder(args);
builder.AddAuditFlowDefaults("auth-service");
builder.AddAuditFlowSecrets();
builder.AddAuditFlowAuthentication();
builder.AddAuditFlowData(typeof(Program).Assembly);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasswordHasher<UserRow>, PasswordHasher<UserRow>>();
builder.Services.AddSingleton<IUserRepository, UserRepository>();
builder.Services.AddSingleton<ISigningKeyService, SigningKeyService>();
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddSingleton<LoginService>();
builder.Services.AddScoped<IDataSeeder, DemoUserSeeder>();

var app = builder.Build();
app.UseAuditFlowDefaults();
app.MapAuthEndpoints();
app.Run();

public partial class Program;
