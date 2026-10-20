using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Docugen.Api.Security;

/// <summary>
/// Signs a call in as the application whose key is in the <c>x-api-key</c> metadata (an HTTP header). Calls without
/// a valid key get UNAUTHENTICATED.
/// </summary>
internal sealed partial class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    ApiKeys keys) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "x-api-key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var caller = values.Count == 1 ? keys.FindCaller(values[0]) : null;
        if (caller is null)
        {
            LogRejected(Logger, Context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, caller), new Claim(ClaimTypes.NameIdentifier, caller)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected a call with an invalid API key from {RemoteAddress}")]
    private static partial void LogRejected(ILogger logger, string remoteAddress);
}
