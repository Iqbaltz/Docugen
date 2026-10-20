using System.Security.Claims;
using Docugen.Infrastructure.Exports;
using Grpc.Core;

namespace Docugen.Api.Services;

internal static class CallerIdentity
{
    /// <summary>
    /// The caller's stable id (the token subject) and a readable name: the user name for user tokens, the client
    /// application id for app-only tokens (Entra client credentials).
    /// </summary>
    public static ExportCaller From(ClaimsPrincipal user)
    {
        var id = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub")
            ?? user.FindFirstValue("oid")
            ?? user.Identity?.Name
            ?? throw new RpcException(new Status(StatusCode.Unauthenticated, "The token identifies no caller (no sub claim)."));

        var name = user.FindFirstValue("preferred_username")
            ?? user.Identity?.Name
            ?? user.FindFirstValue("name")
            ?? user.FindFirstValue("azp")
            ?? user.FindFirstValue("appid")
            ?? id;

        return new ExportCaller(id, name);
    }
}
