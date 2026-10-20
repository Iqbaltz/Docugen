using Docugen.Api.Services;
using Docugen.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;

// "dotnet Docugen.Api.dll install-browsers" downloads Chromium for PDF rendering (used by the Dockerfile).
if (args is ["install-browsers", ..])
{
    return PlaywrightBrowserInstaller.Install(withDependencies: args.Contains("--with-deps"));
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDocugen(builder.Configuration);
builder.Services.AddGrpc(options => options.EnableDetailedErrors = builder.Environment.IsDevelopment());
builder.Services.AddGrpcHealthChecks();
builder.Services.AddGrpcReflection();

// Token settings come from Authentication:Schemes:Bearer (Authority, ValidAudiences, ...).
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGrpcService<ReportGrpcService>();
app.MapGrpcService<ExportGrpcService>();
app.MapGrpcHealthChecksService();

// Lets tools such as Postman and grpcurl discover the services without the .proto file.
if (app.Environment.IsDevelopment())
{
    app.MapGrpcReflectionService();
}

app.Run();
return 0;
