using System.Threading.RateLimiting;
using Docugen.Api;
using Docugen.Api.Endpoints;
using Docugen.Core;
using Docugen.Core.Reports;
using Docugen.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Scalar.AspNetCore;

// "dotnet Docugen.Api.dll install-browsers" downloads Chromium for PDF rendering (used by the Dockerfile).
if (args is ["install-browsers", ..])
{
    return PlaywrightBrowserInstaller.Install(withDependencies: args.Contains("--with-deps"));
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDocugen(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DocugenExceptionHandler>();
builder.Services.AddOpenApi();

// Token settings come from Authentication:Schemes:Bearer (Authority, ValidAudiences, ...).
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddAuthorization();

var limits = builder.Configuration.GetSection($"{DocugenOptions.SectionName}:Limits").Get<LimitOptions>() ?? new LimitOptions();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddConcurrencyLimiter(ReportEndpoints.RenderRateLimiter, limiter =>
    {
        limiter.PermitLimit = limits.MaxConcurrentRenders;
        limiter.QueueLimit = limits.MaxQueuedRenders;
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
});

var app = builder.Build();

// Load and validate every report definition now so a broken definition stops the deployment.
app.Services.GetRequiredService<IReportCatalog>();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapReportEndpoints();
app.MapHealthChecks("/health");

app.Run();
return 0;
