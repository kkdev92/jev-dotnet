using System.Net;
using Kkdev92.Jev.DependencyInjection;
using Kkdev92.Jev.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Kkdev92.Jev.Tests;

/// <summary>
/// What <c>AddJev</c> registers, and the transport settings it takes responsibility for.
/// </summary>
/// <remarks>
/// The two settings on the <see cref="HttpClient"/> are the ones a typed client would otherwise get
/// wrong silently: the factory's default 100-second timeout would race the client's own deadline,
/// and a handler that follows redirects would replay the Authorization header to wherever a
/// redirect pointed. Each is read back from what the container actually builds.
/// </remarks>
public sealed class DependencyInjectionTests
{
    private static readonly StaticJevCredential Key = new("sk-test-not-a-real-key");

    private static ServiceProvider Build(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        register(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void TheClientResolvesAsATypedClient()
    {
        using var provider = Build(s => s.AddJev(o => o.Credential = Key));

        var first = provider.GetRequiredService<JevClient>();
        var second = provider.GetRequiredService<JevClient>();

        // Transient, as a typed client is: the HttpClient under it is what the factory pools.
        Assert.NotSame(first, second);
    }

    [Fact]
    public void TheHttpClientHasNoTimeoutOfItsOwn()
    {
        using var provider = Build(s => s.AddJev(o => o.Credential = Key));

        using var httpClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient(JevServiceCollectionExtensions.HttpClientName);

        Assert.Equal(Timeout.InfiniteTimeSpan, httpClient.Timeout);
    }

    [Fact]
    public void ThePrimaryHandlerDoesNotFollowRedirectsOrDecompress()
    {
        using var provider = Build(s => s.AddJev(o => o.Credential = Key));

        HttpMessageHandler? handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(JevServiceCollectionExtensions.HttpClientName);

        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler;
        }

        var primary = Assert.IsType<SocketsHttpHandler>(handler);

        Assert.False(primary.AllowAutoRedirect);
        Assert.Equal(DecompressionMethods.None, primary.AutomaticDecompression);
    }

    [Fact]
    public void OtherNamedClientsAreLeftAlone()
    {
        using var provider = Build(s =>
        {
            s.AddJev(o => o.Credential = Key);
            s.AddHttpClient("someone-else");
        });

        using var other = provider.GetRequiredService<IHttpClientFactory>().CreateClient("someone-else");

        Assert.Equal(TimeSpan.FromSeconds(100), other.Timeout);
    }

    [Fact]
    public void AMissingCredentialFailsWhenTheClientIsResolved()
    {
        using var provider = Build(s => s.AddJev(_ => { }));

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<JevClient>());

        Assert.Contains("Credential", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A host that validates options on start fails there, before the first call.</summary>
    [Fact]
    public void InvalidOptionsFailAtStartUp()
    {
        using var provider = Build(s => s.AddJev(o =>
        {
            o.Credential = Key;
            o.BaseAddress = new Uri("http://api.typesafe.ai/");
        }));

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("BaseAddress", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheValidationMessageDoesNotEchoTheAddress()
    {
        // A base address with user information carries a credential, so the message names the
        // option and never repeats the value.
        using var provider = Build(s => s.AddJev(o =>
        {
            o.Credential = Key;
            o.BaseAddress = new Uri("https://user:hunter2@api.typesafe.ai/");
        }));

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<JevClient>());

        Assert.DoesNotContain("hunter2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheConfigureDelegateCanResolveServices()
    {
        var time = new FakeTimeProvider();

        using var provider = Build(s =>
        {
            s.AddSingleton<TimeProvider>(time);
            s.AddJev((services, o) =>
            {
                o.Credential = Key;
                o.TimeProvider = services.GetRequiredService<TimeProvider>();
            });
        });

        Assert.Same(time, provider.GetRequiredService<IOptions<JevClientOptions>>().Value.TimeProvider);
        _ = provider.GetRequiredService<JevClient>();
    }

    /// <summary>
    /// Every client the container creates shares one concurrency limit.
    /// </summary>
    /// <remarks>
    /// Typed clients are created per resolution, so a gate created per client would bound nothing:
    /// two services each resolving a client would each get a limit of their own. Proved by
    /// occupying the only permit through one client and watching another client's call be
    /// refused, which it could only be if both were counting against the same gate.
    /// </remarks>
    [Fact]
    public async Task EveryResolvedClientSharesOneConcurrencyLimit()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FakeHttpMessageHandler((_, _) =>
        {
            entered.TrySetResult();
            return release.Task;
        });

        using var provider = Build(s => s.AddJev(o =>
            {
                o.Credential = Key;
                o.ConcurrencyLimit = 1;
                o.MaxQueuedRequests = 0;
            })
            .ConfigurePrimaryHttpMessageHandler(() => handler));

        var (plan, _, _, _) = TestPlans.Triage();
        var first = provider.GetRequiredService<JevClient>();
        var second = provider.GetRequiredService<JevClient>();

        var pending = first.EvaluateAsync("The invoice was charged twice.", plan, cancellationToken: TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var refused = await Assert.ThrowsAsync<JevLimitException>(
            () => second.EvaluateAsync("The invoice was charged twice.", plan, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(JevLimit.Queue, refused.Limit);
        Assert.Equal(JevRequestTransmission.NotStarted, refused.Transmission);

        release.SetResult(FakeResponses.Json(TestPlans.TriageResponse()));
        _ = await pending;

        Assert.Single(handler.Requests);
    }

    [Fact]
    public void WithoutALimitThereIsNoGate()
    {
        using var provider = Build(s => s.AddJev(o => o.Credential = Key));

        Assert.Null(provider.GetRequiredService<JevAdmission>().Gate);
    }

    [Fact]
    public void NullArgumentsAreRefused()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => services.AddJev((Action<JevClientOptions>)null!));
        Assert.Throws<ArgumentNullException>(() => services.AddJev((Action<IServiceProvider, JevClientOptions>)null!));
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddJev(_ => { }));
    }
}
