using Docugen.Api.Security;
using Docugen.Api.Services;
using Docugen.Infrastructure;
using Microsoft.AspNetCore.Authentication;

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

// Callers identify themselves with an API key in the x-api-key metadata; keys come from Docugen:ApiKeys.
builder.Services.AddSingleton(sp => ApiKeys.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization();

var app = builder.Build();

// Refuse to start without valid API keys rather than fail on the first call.
app.Services.GetRequiredService<ApiKeys>();

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
