using System.Data.Common;

namespace Docugen.Infrastructure.Data;

public interface IDbConnectionFactory
{
    IReadOnlyCollection<string> DataSourceNames { get; }

    /// <summary>Creates an unopened connection for a configured data source.</summary>
    DbConnection Create(string dataSource);
}
