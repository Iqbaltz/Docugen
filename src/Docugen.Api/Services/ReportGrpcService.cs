using Docugen.Contracts.V1;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using IReportCatalog = Docugen.Core.Reports.IReportCatalog;

namespace Docugen.Api.Services;

[Authorize]
public sealed class ReportGrpcService(IReportCatalog catalog) : ReportService.ReportServiceBase
{
    public override Task<ListReportsResponse> ListReports(ListReportsRequest request, ServerCallContext context)
    {
        var response = new ListReportsResponse();
        response.Reports.AddRange(catalog.All
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(GrpcMapping.ToSummary));
        return Task.FromResult(response);
    }

    public override Task<Report> GetReport(GetReportRequest request, ServerCallContext context) =>
        Task.FromResult(GrpcMapping.ToReport(GrpcErrors.FindReport(catalog, request.Key)));
}
