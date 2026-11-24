using Kkdev92.Jev.TestSupport;
using static Kkdev92.Jev.ContractTests.Clients;

namespace Kkdev92.Jev.ContractTests;

public sealed class EndpointContractTests
{
    [Theory]
    [InlineData("https://api.typesafe.ai", "/")]
    [InlineData("https://api.typesafe.ai/", "/")]
    [InlineData("https://proxy.example/gateway", "/gateway/")]
    [InlineData("https://proxy.example/gateway/", "/gateway/")]
    [InlineData("https://proxy.example/a/b", "/a/b/")]
    [InlineData("https://proxy.example/team%20one/", "/team%20one/")]
    [InlineData("https://proxy.example/gateway//", "/gateway//")]
    [InlineData("https://proxy.example//gateway", "//gateway/")]
    [InlineData("https://proxy.example/v1/", "/v1/")]
    [InlineData("https://例え.テスト/prefix", "/prefix/")]
    [InlineData("https://[fe80::1%25eth0]/prefix", "/prefix/")]
    [InlineData("https://[fe80::1%25eth0]", "/")]
    [InlineData("https://127.1", "/")]
    [InlineData("https://loopback", "/")]
    [InlineData("http://LOCALHOST:11434", "/")]
    [InlineData("http://127.0.0.1:11434/prefix", "/prefix/")]
    [InlineData("http://[::1]:8888/prefix/", "/prefix/")]
    [InlineData("http://[0:0:0:0:0:0:0:1]", "/")]
    public async Task BothEndpointsKeepThePrefixAndAuthority(string spelling, string prefix)
    {
        var address = new Uri(spelling);
        var handler = FakeHttpMessageHandler.Sequence(() => FakeResponses.Json(Answer()), () => FakeResponses.Json(FakeResponses.Models()));
        var client = Create(handler, o =>
        {
            o.BaseAddress = address;
            o.AllowLoopbackHttp = true;
        });
        var token = TestContext.Current.CancellationToken;
        await client.EvaluateAsync("text", Plan().Plan, cancellationToken: token);
        await client.GetModelsAsync(cancellationToken: token);
        Assert.Equal([prefix + "v1/systemone", prefix + "v1/models"], handler.Requests.Select(r => r.Uri.AbsolutePath));
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal(address.IdnHost, r.Uri.IdnHost);
            Assert.Equal(address.Scheme, r.Uri.Scheme);
            Assert.Equal(address.Port, r.Uri.Port);
        });
    }

    [Theory]
    [InlineData("http://127.0.0.2")]
    [InlineData("http://[::ffff:127.0.0.1]")]
    [InlineData("http://localhost.")]
    [InlineData("http://sub.localhost")]
    [InlineData("http://localhost.evil.example")]
    [InlineData("http://192.168.1.3")]
    [InlineData("http://machine.lan")]
    public async Task OtherHttpOriginsNeedAnExactPortAndName(string spelling)
    {
        var handler = FakeHttpMessageHandler.Always(FakeResponses.Models());
        var origins = new[] { new Uri("http://localhost:9898") };
        var options = new JevClientOptions
        {
            AuthenticationMode = JevAuthenticationMode.None,
            BaseAddress = new Uri(spelling + "/prefix"),
            AllowLoopbackHttp = true,
            AllowedInsecureHttpOrigins = origins,
        };
        using var http = new HttpClient(handler);
        Assert.Throws<ArgumentException>(() => new JevClient(http, options));
        origins[0] = new Uri(spelling + ":9898");
        Assert.Throws<ArgumentException>(() => new JevClient(http, options));
        origins[0] = new Uri(spelling + ":80");
        var client = new JevClient(http, options);
        origins[0] = new Uri("http://elsewhere.test");
        await client.GetModelsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(options.BaseAddress.IdnHost, Assert.Single(handler.Requests).Uri.IdnHost);
    }

    [Theory]
    [InlineData("http://127.1")]
    [InlineData("http://2130706433")]
    [InlineData("http://0x7f000001")]
    [InlineData("http://0177.0.0.1")]
    [InlineData("http://loopback")]
    [InlineData("http://ｌｏｃａｌｈｏｓｔ")]
    [InlineData("http://[fe80::1%25eth0]")]
    [InlineData("http://0.0.0.0")]
    [InlineData("http://[::]")]
    [InlineData("https://@host/")]
    [InlineData("https://host:/")]
    [InlineData(" https://host/")]
    [InlineData("https://host/ ")]
    [InlineData("https://host/a/../")]
    [InlineData("https://host/a/%2e%2e/b")]
    [InlineData("https://host/a/./b")]
    [InlineData("https://host/a%2fb")]
    [InlineData("https://host/a%5cb")]
    [InlineData("https://host/a\\b")]
    [InlineData("https://host/x?")]
    [InlineData("https://host/x#")]
    public void DisallowedSpellingsFailBeforeSend(string spelling)
    {
        var handler = FakeHttpMessageHandler.Always(Answer());
        Assert.Throws<ArgumentException>(() => Create(handler, o =>
        {
            o.AllowLoopbackHttp = true;
            o.BaseAddress = new Uri(spelling);
            if (o.BaseAddress.Scheme == "http")
            {
                o.AllowedInsecureHttpOrigins = [o.BaseAddress];
            }
        }));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(false, null, false, "client")]
    [InlineData(false, null, true, "request")]
    [InlineData(false, JevAuthenticationMode.ApiKey, false, "client")]
    [InlineData(false, JevAuthenticationMode.ApiKey, true, "request")]
    [InlineData(false, JevAuthenticationMode.None, false, "anonymous")]
    [InlineData(false, JevAuthenticationMode.None, true, "invalid")]
    [InlineData(true, null, false, "anonymous")]
    [InlineData(true, null, true, "invalid")]
    [InlineData(true, JevAuthenticationMode.ApiKey, false, "invalid")]
    [InlineData(true, JevAuthenticationMode.ApiKey, true, "request")]
    [InlineData(true, JevAuthenticationMode.None, false, "anonymous")]
    [InlineData(true, JevAuthenticationMode.None, true, "invalid")]
    public async Task OnlyTheEffectiveCredentialIsConsulted(bool anonymousClient, JevAuthenticationMode? mode, bool overrideKey, string expected)
    {
        var clientKey = new CountingCredential("client");
        var requestKey = new CountingCredential("request");
        var handler = FakeHttpMessageHandler.Always(FakeResponses.Models());
        var client = Create(handler, o =>
        {
            o.AuthenticationMode = anonymousClient ? JevAuthenticationMode.None : JevAuthenticationMode.ApiKey;
            o.Credential = anonymousClient ? null : clientKey;
        });
        var request = new JevRequestOptions { AuthenticationMode = mode, Credential = overrideKey ? requestKey : null };
        var token = TestContext.Current.CancellationToken;
        if (expected == "invalid")
        {
            await Assert.ThrowsAsync<ArgumentException>(() => client.GetModelsAsync(request, token));
            Assert.Empty(handler.Requests);
        }
        else
        {
            await client.GetModelsAsync(request, token);
            var headers = Assert.Single(handler.Requests).Headers;
            Assert.Equal(expected != "anonymous", headers.TryGetValue("Authorization", out var value));
            Assert.Equal(expected == "anonymous" ? null : "Bearer " + expected, value);
        }

        Assert.Equal(expected == "client" ? 1 : 0, clientKey.Reads);
        Assert.Equal(expected == "request" ? 1 : 0, requestKey.Reads);
    }

    [Fact]
    public async Task DefaultAuthorizationIsRefusedAtConstructionCallAndRetry()
    {
        using var http = new HttpClient(FakeHttpMessageHandler.Always(Answer()));
        var options = new JevClientOptions { AuthenticationMode = JevAuthenticationMode.None };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer private-sentinel");
        var ex = Assert.Throws<InvalidOperationException>(() => new JevClient(http, options));
        Assert.DoesNotContain("private-sentinel", ex.ToString(), StringComparison.Ordinal);
        var key = new CountingCredential("client");
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeResponses.Overloaded()));
        using var borrowed = new HttpClient(handler);
        var bearer = new JevClient(borrowed, new JevClientOptions
        {
            Credential = key,
            AdditionalRetries = 1,
            JitterSource = () =>
            {
                borrowed.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer private-sentinel");
                return 0;
            },
        });
        var request = new JevRequestOptions { AuthenticationMode = JevAuthenticationMode.None };
        var token = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<InvalidOperationException>(() => bearer.GetModelsAsync(request, token));
        Assert.Single(handler.Requests);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bearer.GetModelsAsync(request, token));
        Assert.Single(handler.Requests);
        Assert.Equal(0, key.Reads);
        Assert.Equal("Bearer private-sentinel", borrowed.DefaultRequestHeaders.Authorization?.ToString());
    }

    private sealed class CountingCredential(string value) : IJevCredential
    {
        public int Reads { get; private set; }

        public ValueTask<string> GetApiKeyAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return ValueTask.FromResult(value);
        }
    }
}
