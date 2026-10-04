using Docugen.Core;

namespace Docugen.Infrastructure;

internal static class DocugenOptionsExtensions
{
    /// <summary>Finds a data source by name, ignoring case (configuration keys are case-insensitive).</summary>
    public static DataSourceOptions? FindDataSource(this DocugenOptions options, string name)
    {
        if (options.DataSources.TryGetValue(name, out var dataSource))
        {
            return dataSource;
        }

        return options.DataSources
            .FirstOrDefault(d => d.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Value;
    }
}
