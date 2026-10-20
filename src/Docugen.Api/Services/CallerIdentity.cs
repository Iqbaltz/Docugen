using System.Security.Claims;
using Docugen.Infrastructure.Exports;
using Grpc.Core;

namespace Docugen.Api.Services;

internal static class CallerIdentity
{
    /// <summary>The calling application: the name its API key is registered under in <c>Docugen:ApiKeys</c>.</summary>
    public static ExportCaller From(ClaimsPrincipal user)
    {
        var name = user.Identity?.Name
            ?? throw new RpcException(new Status(StatusCode.Unauthenticated, "The call is not signed in."));
        return new ExportCaller(name, name);
    }
}
