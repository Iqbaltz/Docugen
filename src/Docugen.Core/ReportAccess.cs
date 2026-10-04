using System.Security.Claims;
using Docugen.Core.Reports;

namespace Docugen.Core;

public static class ReportAccess
{
    /// <summary>True when the user is authenticated and holds one of the report's roles (or the report allows everyone).</summary>
    public static bool CanRun(ClaimsPrincipal user, ReportDefinition report) =>
        user.Identity?.IsAuthenticated == true
        && (report.AllowedRoles.Count == 0 || report.AllowedRoles.Any(user.IsInRole));
}
