using System.Net;
using Kkdev92.Jev.Internal;
using Kkdev92.Jev.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Kkdev92.Jev.DependencyInjection;

/// <summary>Registers <see cref="JevClient"/> as a typed client of <see cref="IHttpClientFactory"/>.</summary>
public static class JevServiceCollectionExtensions
{
    /// <summary>The name of the <see cref="HttpClient"/> the factory creates for <see cref="JevClient"/>.</summary>
    public const string HttpClientName = "Kkdev92.Jev";

    /// <summary>
    /// Registers <see cref="JevClient"/>, configured by a delegate.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">
    /// Sets the options. <see cref="JevClientOptions.Credential"/> must be set here: nothing is read
    /// from configuration or the environment on its own.
    /// </param>
    /// <returns>The <see cref="IHttpClientBuilder"/> of the typed client, for adding handlers.</returns>
    /// <remarks>
    /// <para>
    /// The client is a typed client: resolved transiently, over an <see cref="HttpClient"/> whose
    /// handler the factory pools and rotates. Its <see cref="HttpClient.Timeout"/> is set to
    /// <see cref="Timeout.InfiniteTimeSpan"/> so that <see cref="JevClientOptions.Timeout"/> is the
    /// one deadline on a call, and its primary handler does not follow redirects, so a key is never
    /// replayed to another host. Both are set on this client only.
    /// </para>
    /// <para>
    /// Invalid options fail when a <see cref="JevClient"/> is resolved, and at start-up for a host that
    /// validates options on start. A <see cref="JevClientOptions.ConcurrencyLimit"/> is shared by
    /// every client the container creates, not applied per resolution.
    /// </para>
    /// <para>
    /// Do not add a retrying handler as well as setting <see cref="JevClientOptions.AdditionalRetries"/>:
    /// the two would multiply, and each retry can be billed.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IHttpClientBuilder AddJev(this IServiceCollection services, Action<JevClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return services.AddJev((_, options) => configure(options));
    }

    /// <summary>
    /// Registers <see cref="JevClient"/>, configured by a delegate that can resolve services — a
    /// credential backed by a secret store, a <see cref="TimeProvider"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets the options, with the service provider to resolve what they need.</param>
    /// <returns>The <see cref="IHttpClientBuilder"/> of the typed client, for adding handlers.</returns>
    /// <remarks>See <see cref="AddJev(IServiceCollection, Action{JevClientOptions})"/>.</remarks>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IHttpClientBuilder AddJev(this IServiceCollection services, Action<IServiceProvider, JevClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<JevClientOptions>()
            .Configure<IServiceProvider>((options, provider) => configure(provider, options))
            .ValidateOnStart();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<JevClientOptions>, JevClientOptionsValidator>());
        services.TryAddSingleton<JevAdmission>();

        return services
            .AddHttpClient(HttpClientName)
            .AddTypedClient<JevClient>((HttpClient httpClient, IServiceProvider provider) => new JevClient(
                httpClient,
                provider.GetRequiredService<IOptions<JevClientOptions>>().Value,
                provider.GetRequiredService<JevAdmission>().Gate))
            .ConfigureHttpClient(httpClient => httpClient.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
            });
    }
}

/// <summary>Validates the options the same way <see cref="JevClient"/> does, so a host that validates on start fails there.</summary>
internal sealed class JevClientOptionsValidator : IValidateOptions<JevClientOptions>
{
    public ValidateOptionsResult Validate(string? name, JevClientOptions options)
    {
        try
        {
            ClientSettings.From(options);
            return ValidateOptionsResult.Success;
        }
        catch (ArgumentException ex)
        {
            return ValidateOptionsResult.Fail(ex.Message);
        }
    }
}

/// <summary>The one admission gate every client of the container shares, when a limit is set.</summary>
/// <remarks>
/// Typed clients are created per resolution. A gate created per client would bound nothing, so it
/// is created once, from the options, and handed to each client.
/// </remarks>
internal sealed class JevAdmission(IOptions<JevClientOptions> options)
{
    private readonly Lazy<AdmissionGate?> _gate = new(() => options.Value.ConcurrencyLimit is { } limit
        ? new AdmissionGate(limit, options.Value.MaxQueuedRequests)
        : null);

    public AdmissionGate? Gate => _gate.Value;
}
