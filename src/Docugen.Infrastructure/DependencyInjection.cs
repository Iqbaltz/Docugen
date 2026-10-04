using Docugen.Core;
using Docugen.Core.Data;
using Docugen.Core.Rendering;
using Docugen.Core.Reports;
using Docugen.Infrastructure.Catalog;
using Docugen.Infrastructure.Data;
using Docugen.Infrastructure.Rendering;
using Docugen.Infrastructure.Rendering.Pdf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Docugen.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers the report catalog, SQL data access, the CSV/XLSX/PDF renderers and the generator.</summary>
    public static IServiceCollection AddDocugen(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DocugenOptions>().Bind(configuration.GetSection(DocugenOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<LiquidTemplateEngine>();
        services.AddSingleton<IReportCatalog, FileReportCatalog>();
        services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
        services.AddSingleton<IReportDataSource, SqlReportDataSource>();

        services.AddSingleton<ReportAssets>();
        services.AddSingleton<ChromiumPdfConverter>();
        services.AddSingleton<IDocumentRenderer, CsvRenderer>();
        services.AddSingleton<IDocumentRenderer, ExcelRenderer>();
        services.AddSingleton<IDocumentRenderer, PdfRenderer>();
        services.AddSingleton<ReportGenerator>();

        services.AddHostedService<SqliteSeeder>();
        services.AddHealthChecks().AddCheck<DataSourcesHealthCheck>("data-sources");
        return services;
    }
}
