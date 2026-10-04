using Docugen.Infrastructure.Catalog;

namespace Docugen.Infrastructure.Tests;

public class SqlParameterScannerTests
{
    [Fact]
    public void Finds_parameters_and_ignores_comments_literals_globals_and_locals()
    {
        const string sql = """
            -- @CommentedOut
            /* @AlsoCommented */
            DECLARE @Local INT = 1, @Other NVARCHAR(10) = N'x';
            SELECT '@NotAParam', [@Bracketed], @@ROWCOUNT, @Local, @Other
            FROM Orders
            WHERE OrderDate >= @FromDate AND Region = @region AND Email = 'a@b.com'
            """;

        var parameters = SqlParameterScanner.FindParameters(sql);

        Assert.Equal(["FromDate", "region"], parameters.Order(StringComparer.Ordinal));
    }
}
