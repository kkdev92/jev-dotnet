using Kkdev92.Jev.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Kkdev92.Jev.Tests;

public sealed class ConfigurationImportTests
{
    [Theory]
    [InlineData("ApiKey", "private-sentinel")]
    [InlineData("private-sentinel", "unknown")]
    [InlineData("TIMEOUT", "private-sentinel")]
    [InlineData("MaxQueuedRequests", "2147483648")]
    [InlineData("NumericalConsistency", "1")]
    [InlineData("DefaultModel", null)]
    [InlineData("DefaultModel:child", "x")]
    [InlineData("AllowedInsecureHttpOrigins:01", "http://localhost")]
    [InlineData("AllowedInsecureHttpOrigins:1", "http://localhost")]
    [InlineData("BaseAddress", "https://private-sentinel@host/")]
    public void InvalidSectionsFailStartupWithoutDisclosingData(string key, string? value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();
        var services = new ServiceCollection();
        services.AddJev(config, o => o.AuthenticationMode = JevAuthenticationMode.None);
        using var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
        Assert.DoesNotContain("private-sentinel", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ParsedSettingsPrecedeDelegateAndAreFrozenAtResolution()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TIMEOUT"] = "00:00:00",
            ["BaseAddress"] = "http://machine.lan:8888/path",
            ["AllowedInsecureHttpOrigins:0"] = "http://machine.lan:8888",
            ["AuthenticationMode"] = "nOnE",
            ["AdditionalRetries"] = "2",
        }).Build();
        var services = new ServiceCollection();
        services.AddJev(config, (_, o) => { o.AdditionalRetries = 0; o.Timeout = TimeSpan.FromSeconds(3); });
        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<IOptions<JevClientOptions>>().Value;
        Assert.Equal(0, settings.AdditionalRetries);
        Assert.Equal(TimeSpan.FromSeconds(3), settings.Timeout);
        config["TIMEOUT"] = "invalid after resolution";
        Assert.NotNull(provider.GetRequiredService<JevClient>());
    }

    [Fact]
    public void AutoIdsAvoidManualNamesAndRegistrationFailuresDoNotConsumeThem()
    {
        var builder = new JevDecisionPlanBuilder();
        builder.AddNoul("q0", default);
        Assert.Throws<ArgumentException>(() => builder.AddScore("rate", []));
        Assert.Equal("q1", builder.AddNoul("question").Id);
        Assert.Equal("q2", builder.AddChoice<int>("choose", [new(1, "left"), new(2, "right")]).Id);
        Assert.Equal("q3", builder.AddScore("rate", ["low", "high"]).Id);
        Assert.Throws<ArgumentException>(() => builder.AddNoul("q1", default));
        Assert.Equal(4, builder.Build().QuestionCount);
    }
}
