using Docugen.Contracts.V1;
using Docugen.Core;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using IReportCatalog = Docugen.Core.Reports.IReportCatalog;

namespace Docugen.Api.Services;

[Authorize]
public sealed class ReportGrpcService(IReportCatalog catalog) : ReportService.ReportServiceBase
{
    public override Task<ListReportsResponse> ListReports(ListReportsRequest request, ServerCallContext context)
    {
        var user = context.GetHttpContext().User;
        var response = new ListReportsResponse();
        response.Reports.AddRange(catalog.All
            .Where(r => ReportAccess.CanRun(user, r))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(GrpcMapping.ToSummary));
        return Task.FromResult(response);
    }

    public override Task<Report> GetReport(GetReportRequest request, ServerCallContext context)
    {
        var report = GrpcErrors.FindRunnable(catalog, request.Key, context.GetHttpContext().User);
        return Task.FromResult(GrpcMapping.ToReport(report));
    }
}
