using Docugen.Core.Parameters;
using Docugen.Core.Reports;

namespace Docugen.Core.Data;

public interface IReportDataSource
{
    /// <summary>Runs the report query. The caller owns the returned data and must dispose it.</summary>
    Task<ReportData> ExecuteAsync(ReportDefinition report, IReadOnlyList<BoundParameter> parameters, CancellationToken cancellationToken);

    /// <summary>Runs the report's <see cref="ReportDefinition.CountQuery"/> and returns its single value.</summary>
    Task<long> CountAsync(ReportDefinition report, IReadOnlyList<BoundParameter> parameters, CancellationToken cancellationToken);
}
