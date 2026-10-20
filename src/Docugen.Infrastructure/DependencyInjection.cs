using Docugen.Core;
using Docugen.Core.Data;
using Docugen.Core.Rendering;
using Docugen.Core.Reports;
using Docugen.Infrastructure.Catalog;
using Docugen.Infrastructure.Data;
using Docugen.Infrastructure.Exports;
using Docugen.Infrastructure.Rendering;
using Docugen.Infrastructure.Rendering.Pdf;
using Docugen.Infrastructure.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Docugen.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the store-backed report catalog, SQL data access, the CSV/XLSX/PDF renderers, the generator, the
    /// exporter and the export log.
    /// </summary>
    public static IServiceCollection AddDocugen(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DocugenOptions>()
            .Bind(configuration.GetSection(DocugenOptions.SectionName))
            .Validate(o => o.Catalog.RefreshInterval > TimeSpan.Zero, "Docugen:Catalog:RefreshInterval must be positive.")
            .Validate(o => o.Limits.MaxConcurrentExports > 0, "Docugen:Limits:MaxConcurrentExports must be positive.")
            .Validate(o => o.Limits.MaxQueuedExports >= 0, "Docugen:Limits:MaxQueuedExports cannot be negative.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<StoreDatabase>();
        services.AddSingleton<LiquidTemplateEngine>();
        services.AddSingleton<SqlReportCatalog>();
        services.AddSingleton<IReportCatalog>(sp => sp.GetRequiredService<SqlReportCatalog>());
        services.AddSingleton<IReportAssets>(sp => sp.GetRequiredService<SqlReportCatalog>());
        services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
        services.AddSingleton<IReportDataSource, SqlReportDataSource>();

        services.AddSingleton<ChromiumPdfConverter>();
        services.AddSingleton<IDocumentRenderer, CsvRenderer>();
        services.AddSingleton<IDocumentRenderer, ExcelRenderer>();
        services.AddSingleton<IDocumentRenderer, PdfRenderer>();
        services.AddSingleton<ReportGenerator>();

        services.AddSingleton<IExportLog, SqlExportLog>();
        services.AddSingleton<ReportExporter>();

        // Hosted services start in this order, so Sqlite databases exist before the catalog loads.
        services.AddHostedService<SqliteSeeder>();
        services.AddHostedService<ReportCatalogRefresher>();

        services.AddHealthChecks()
            .AddCheck<StoreHealthCheck>("store")
            .AddCheck<DataSourcesHealthCheck>("data-sources");
        return services;
    }
}
