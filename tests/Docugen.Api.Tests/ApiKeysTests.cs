using Docugen.Api.Security;

namespace Docugen.Api.Tests;

public sealed class ApiKeysTests
{
    private readonly ApiKeys _keys = new(new Dictionary<string, string?>
    {
        ["BillingApp"] = "billing-secret-0123456789",
        ["HrPortal"] = "hr-portal-secret-0123456789",
    });

    [Fact]
    public void Finds_the_application_a_key_belongs_to()
    {
        Assert.Equal("BillingApp", _keys.FindCaller("billing-secret-0123456789"));
        Assert.Equal("HrPortal", _keys.FindCaller("hr-portal-secret-0123456789"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("billing-secret-012345678")]
    [InlineData("BILLING-SECRET-0123456789")]
    [InlineData("billing-secret-0123456789 ")]
    public void Rejects_anything_but_an_exact_key(string? key) => Assert.Null(_keys.FindCaller(key));

    [Fact]
    public void Refuses_to_start_without_keys()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new ApiKeys(new Dictionary<string, string?>()));

        Assert.Contains("No API keys are configured", error.Message);
    }

    [Fact]
    public void Refuses_short_or_shared_keys()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new ApiKeys(new Dictionary<string, string?>
        {
            ["Short"] = "too-short",
            ["First"] = "the-same-secret-0123456789",
            ["Second"] = "the-same-secret-0123456789",
        }));

        Assert.Contains("'Short' must be at least 16 characters", error.Message);
        Assert.Contains("'First' and 'Second' share the same API key", error.Message);
    }
}
