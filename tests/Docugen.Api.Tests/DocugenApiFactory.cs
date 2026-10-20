using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Docugen.Api.Tests;

/// <summary>
/// Runs the real gRPC service, with real API keys, against Sqlite copies of the store (with the sample reports)
/// and the sales data.
/// </summary>
public sealed class DocugenApiFactory : WebApplicationFactory<Program>
{
    /// <summary>The API key of the "TestApp" caller.</summary>
    public const string TestAppKey = "test-app-key-0123456789";

    /// <summary>The API key of the "DeadlineApp" caller, used where a test looks up its own exports in the log.</summary>
    public const string DeadlineAppKey = "deadline-app-key-0123456789";

    private readonly string _storePath = Path.Combine(Path.GetTempPath(), $"docugen-store-{Guid.NewGuid():N}.db");
    private readonly string _salesPath = Path.Combine(Path.GetTempPath(), $"docugen-sales-{Guid.NewGuid():N}.db");
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"docugen-temp-{Guid.NewGuid():N}");
    private GrpcChannel? _channel;

    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>Where files are rendered before they are sent; empty again once each export has finished.</summary>
    public string TempPath => _tempPath;

    /// <summary>The Sqlite store, for checking the export log directly.</summary>
    public string StoreConnectionString => $"Data Source={_storePath};Pooling=False";

    /// <summary>A channel to the in-memory server. Pass <see cref="WithKey"/> metadata to sign in.</summary>
    public GrpcChannel Channel => _channel ??= GrpcChannel.ForAddress(Server.BaseAddress, new GrpcChannelOptions { HttpHandler = Server.CreateHandler() });

    /// <summary>Call metadata carrying an API key.</summary>
    public static Metadata WithKey(string key) => new() { { "x-api-key", key } };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Docugen:ApiKeys:TestApp"] = TestAppKey,
            ["Docugen:ApiKeys:DeadlineApp"] = DeadlineAppKey,
            ["Docugen:Store:Provider"] = "Sqlite",
            ["Docugen:Store:ConnectionString"] = $"Data Source={_storePath};Pooling=False",
            ["Docugen:Store:SeedScript"] = Path.Combine(RepoRoot, "samples", "sqlite", "docugen-store.sql"),
            ["Docugen:DataSources:SalesDb:Provider"] = "Sqlite",
            ["Docugen:DataSources:SalesDb:ConnectionString"] = $"Data Source={_salesPath};Pooling=False",
            ["Docugen:DataSources:SalesDb:SeedScript"] = Path.Combine(RepoRoot, "samples", "sqlite", "seed.sql"),
            ["Docugen:TempPath"] = _tempPath,
            ["Docugen:Limits:MaxPdfRows"] = "10",
            ["Docugen:Pdf:BrowserExecutablePath"] = Environment.GetEnvironmentVariable("DOCUGEN_BROWSER_PATH"),
        }));
    }

    public override async ValueTask DisposeAsync()
    {
        _channel?.Dispose();
        await base.DisposeAsync();
        File.Delete(_storePath);
        File.Delete(_salesPath);
        if (Directory.Exists(_tempPath))
        {
            Directory.Delete(_tempPath, recursive: true);
        }
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Docugen.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root (Docugen.slnx).");
    }
}
