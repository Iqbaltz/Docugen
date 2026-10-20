using System.Security.Claims;
using System.Text.Encodings.Web;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Docugen.Api.Tests;

/// <summary>Runs the real gRPC service against Sqlite copies of the store (with the sample reports) and the sales data.</summary>
public sealed class DocugenApiFactory : WebApplicationFactory<Program>
{
    private readonly string _storePath = Path.Combine(Path.GetTempPath(), $"docugen-store-{Guid.NewGuid():N}.db");
    private readonly string _salesPath = Path.Combine(Path.GetTempPath(), $"docugen-sales-{Guid.NewGuid():N}.db");
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"docugen-temp-{Guid.NewGuid():N}");
    private GrpcChannel? _channel;

    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>Where files are rendered before they are sent; empty again once each export has finished.</summary>
    public string TempPath => _tempPath;

    /// <summary>The Sqlite store, for checking the export log directly.</summary>
    public string StoreConnectionString => $"Data Source={_storePath};Pooling=False";

    /// <summary>A channel to the in-memory server. Pass <see cref="As"/> metadata to sign in.</summary>
    public GrpcChannel Channel => _channel ??= GrpcChannel.ForAddress(Server.BaseAddress, new GrpcChannelOptions { HttpHandler = Server.CreateHandler() });

    /// <summary>Call metadata that signs the call in as <paramref name="user"/> with the given roles.</summary>
    public static Metadata As(string user, params string[] roles) => new()
    {
        { TestAuthHandler.UserHeader, user },
        { TestAuthHandler.RolesHeader, string.Join(',', roles) },
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
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

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultScheme = TestAuthHandler.SchemeName;
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                options.DefaultForbidScheme = TestAuthHandler.SchemeName;
            });
        });
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

/// <summary>Authenticates calls from test metadata instead of a JWT.</summary>
internal sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserHeader = "x-test-user";
    public const string RolesHeader = "x-test-roles";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimTypes.Name, user.ToString()) };
        claims.AddRange(Request.Headers[RolesHeader].ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(role => new Claim(ClaimTypes.Role, role)));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
