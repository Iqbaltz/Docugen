namespace Docugen.Core.Reports;

public interface IReportCatalog
{
    IReadOnlyCollection<ReportDefinition> All { get; }

    ReportDefinition? Find(string key);
}
