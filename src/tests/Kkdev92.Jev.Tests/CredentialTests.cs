namespace Kkdev92.Jev.Tests;

public sealed class CredentialTests
{
    [Fact]
    public async Task AStaticKeyIsReturnedAsGiven()
    {
        var credential = new StaticJevCredential("sk-live-abc123");
        Assert.Equal("sk-live-abc123", await credential.GetApiKeyAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" sk")]
    [InlineData("sk ")]
    [InlineData("sk\nlive")]
    [InlineData("sk\tlive")]
    [InlineData("sk-ключ")]
    public void AKeyThatCannotBeSentIsRefusedWithoutBeingEchoed(string key)
    {
        var exception = Assert.Throws<ArgumentException>(() => new StaticJevCredential(key));

        if (key.Trim().Length > 0)
        {
            Assert.DoesNotContain(key.Trim(), exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ToStringNeverShowsTheKey() => Assert.Equal("StaticJevCredential", new StaticJevCredential("sk-secret").ToString());

    [Fact]
    public async Task AnEnvironmentVariableIsReadOnlyWhenAsked()
    {
        var name = "JEV_TEST_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "sk-from-env\n");

        try
        {
            var credential = StaticJevCredential.FromEnvironmentVariable(name);
            Assert.Equal("sk-from-env", await credential.GetApiKeyAsync(CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void AMissingOrUnusableVariableIsAClearError()
    {
        var name = "JEV_TEST_KEY_" + Guid.NewGuid().ToString("N");

        Assert.Throws<InvalidOperationException>(() => StaticJevCredential.FromEnvironmentVariable(name));

        Environment.SetEnvironmentVariable(name, "has a space");

        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => StaticJevCredential.FromEnvironmentVariable(name));
            Assert.DoesNotContain("has a space", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}
