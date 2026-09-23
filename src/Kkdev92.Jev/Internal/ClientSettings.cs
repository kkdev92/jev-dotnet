using System.Globalization;

namespace Kkdev92.Jev.Internal;

/// <summary>
/// An immutable, validated copy of <see cref="JevClientOptions"/>, taken when the client is created.
/// </summary>
internal sealed class ClientSettings
{
    /// <summary>More retries than this is a loop, not a policy.</summary>
    public const int MaxAdditionalRetries = 10;

    /// <summary>The smallest body limit that makes sense: an empty plan's request is larger.</summary>
    public const int MinBodyLimit = 1024;

    /// <summary>The longest timeout a <see cref="CancellationTokenSource"/> can represent.</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private ClientSettings(JevClientOptions options)
    {
        Credential = options.Credential!;
        DefaultModel = options.DefaultModel;
        BaseAddress = options.BaseAddress;
        Timeout = options.Timeout;
        AdditionalRetries = options.AdditionalRetries;
        RetryBackoffInitial = options.RetryBackoffInitial;
        RetryBackoffMaximum = options.RetryBackoffMaximum;
        MaximumServerRetryWait = options.MaximumServerRetryWait;
        RetryNetworkFailures = options.RetryNetworkFailures;
        MaxRequestBodyBytes = options.MaxRequestBodyBytes;
        MaxResponseBodyBytes = options.MaxResponseBodyBytes;
        MaxErrorBodyBytes = options.MaxErrorBodyBytes;
        ConcurrencyLimit = options.ConcurrencyLimit;
        MaxQueuedRequests = options.MaxQueuedRequests;
        CaptureRawResponse = options.CaptureRawResponse;
        CaptureErrorBody = options.CaptureErrorBody;
        NumericalConsistency = options.NumericalConsistency;
        AbsoluteTolerance = options.NumericalAbsoluteTolerance;
        RelativeTolerance = options.NumericalRelativeTolerance;
        TimeProvider = options.TimeProvider;
        Jitter = options.JitterSource ?? Random.Shared.NextDouble;
    }

    public IJevCredential Credential { get; }

    public string DefaultModel { get; }

    public Uri BaseAddress { get; }

    public TimeSpan Timeout { get; }

    public int AdditionalRetries { get; }

    public TimeSpan RetryBackoffInitial { get; }

    public TimeSpan RetryBackoffMaximum { get; }

    public TimeSpan MaximumServerRetryWait { get; }

    public bool RetryNetworkFailures { get; }

    public int MaxRequestBodyBytes { get; }

    public int MaxResponseBodyBytes { get; }

    public int MaxErrorBodyBytes { get; }

    public int? ConcurrencyLimit { get; }

    public int MaxQueuedRequests { get; }

    public bool CaptureRawResponse { get; }

    public bool CaptureErrorBody { get; }

    public JevNumericalConsistency NumericalConsistency { get; }

    public double AbsoluteTolerance { get; }

    public double RelativeTolerance { get; }

    public TimeProvider TimeProvider { get; }

    public Func<double> Jitter { get; }

    /// <summary>Validates and copies. Every failure names the option, and none echoes a value that could be a secret.</summary>
    public static ClientSettings From(JevClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Credential is null)
        {
            throw new ArgumentException("JevClientOptions.Credential is required. The client never looks for an API key on its own.", nameof(options));
        }

        ValidateModel(options.DefaultModel, $"{nameof(JevClientOptions)}.{nameof(JevClientOptions.DefaultModel)}");
        ValidateBaseAddress(options.BaseAddress);
        ValidateTimeout(options.Timeout, $"{nameof(JevClientOptions)}.{nameof(JevClientOptions.Timeout)}");
        ValidateRetries(options.AdditionalRetries, $"{nameof(JevClientOptions)}.{nameof(JevClientOptions.AdditionalRetries)}");

        if (options.RetryBackoffInitial <= TimeSpan.Zero || options.RetryBackoffMaximum < options.RetryBackoffInitial || options.RetryBackoffMaximum > MaxTimeout)
        {
            throw new ArgumentException("JevClientOptions.RetryBackoffInitial must be positive and no greater than RetryBackoffMaximum.", nameof(options));
        }

        if (options.MaximumServerRetryWait < TimeSpan.Zero || options.MaximumServerRetryWait > MaxTimeout)
        {
            throw new ArgumentException("JevClientOptions.MaximumServerRetryWait must not be negative.", nameof(options));
        }

        if (options.MaxRequestBodyBytes < MinBodyLimit || options.MaxResponseBodyBytes < MinBodyLimit)
        {
            throw new ArgumentException($"JevClientOptions.MaxRequestBodyBytes and MaxResponseBodyBytes must be at least {MinBodyLimit.ToString(CultureInfo.InvariantCulture)}.", nameof(options));
        }

        if (options.MaxErrorBodyBytes < 0)
        {
            throw new ArgumentException("JevClientOptions.MaxErrorBodyBytes must not be negative.", nameof(options));
        }

        if (options.ConcurrencyLimit is < 1)
        {
            throw new ArgumentException("JevClientOptions.ConcurrencyLimit must be at least 1, or null for no limit.", nameof(options));
        }

        if (options.MaxQueuedRequests < 0)
        {
            throw new ArgumentException("JevClientOptions.MaxQueuedRequests must not be negative.", nameof(options));
        }

        if (options.MaxQueuedRequests > 0 && options.ConcurrencyLimit is null)
        {
            // A queue in front of an unlimited gate would never be used, and setting one suggests
            // the limit was meant to be set too.
            throw new ArgumentException("JevClientOptions.MaxQueuedRequests needs ConcurrencyLimit: without a limit nothing ever waits.", nameof(options));
        }

        if (!Enum.IsDefined(options.NumericalConsistency))
        {
            throw new ArgumentException("JevClientOptions.NumericalConsistency is not a defined value.", nameof(options));
        }

        ValidateTolerance(options.NumericalAbsoluteTolerance, nameof(JevClientOptions.NumericalAbsoluteTolerance));
        ValidateTolerance(options.NumericalRelativeTolerance, nameof(JevClientOptions.NumericalRelativeTolerance));

        if (options.TimeProvider is null)
        {
            throw new ArgumentException("JevClientOptions.TimeProvider must not be null.", nameof(options));
        }

        return new ClientSettings(options);
    }

    public static void ValidateModel(string? model, string name)
    {
        if (string.IsNullOrEmpty(model))
        {
            throw new ArgumentException($"{name} must name a model, such as jev-latest.", nameof(model));
        }

        TextRules.ThrowIfMalformed(model, nameof(model), "model name");
    }

    public static void ValidateTimeout(TimeSpan timeout, string name)
    {
        if (timeout != System.Threading.Timeout.InfiniteTimeSpan && (timeout <= TimeSpan.Zero || timeout > MaxTimeout))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), $"{name} must be positive and at most {MaxTimeout.TotalDays.ToString("0", CultureInfo.InvariantCulture)} days, or Timeout.InfiniteTimeSpan.");
        }
    }

    public static void ValidateRetries(int retries, string name)
    {
        if (retries is < 0 or > MaxAdditionalRetries)
        {
            throw new ArgumentOutOfRangeException(nameof(retries), $"{name} must be between 0 and {MaxAdditionalRetries.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    private static void ValidateBaseAddress(Uri? address)
    {
        if (address is null
            || !address.IsAbsoluteUri
            || address.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(address.UserInfo)
            || !string.IsNullOrEmpty(address.Query)
            || !string.IsNullOrEmpty(address.Fragment)
            || address.AbsolutePath != "/")
        {
            // The address is not echoed: a URI with user information carries a credential.
            throw new ArgumentException(
                "JevClientOptions.BaseAddress must be an absolute HTTPS origin, such as https://api.typesafe.ai, with no path, query, fragment or user information.",
                nameof(address));
        }
    }

    private static void ValidateTolerance(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0 || value >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"JevClientOptions.{name} must be a finite number in [0, 1).");
        }
    }
}

/// <summary>The settings of one call: the client's, overridden by the call's own options, frozen when the call starts.</summary>
internal readonly struct CallSettings
{
    private CallSettings(string model, TimeSpan timeout, int retries, IJevCredential credential, bool captureRaw, bool captureError)
    {
        Model = model;
        Timeout = timeout;
        AdditionalRetries = retries;
        Credential = credential;
        CaptureRawResponse = captureRaw;
        CaptureErrorBody = captureError;
    }

    public string Model { get; }

    public TimeSpan Timeout { get; }

    public int AdditionalRetries { get; }

    public IJevCredential Credential { get; }

    public bool CaptureRawResponse { get; }

    public bool CaptureErrorBody { get; }

    public static CallSettings Resolve(ClientSettings client, JevRequestOptions? options)
    {
        if (options is null)
        {
            return new CallSettings(client.DefaultModel, client.Timeout, client.AdditionalRetries, client.Credential, client.CaptureRawResponse, client.CaptureErrorBody);
        }

        // Read each property once, so a concurrent change to a shared instance cannot make the
        // value that was validated differ from the value that is used.
        var model = options.Model;
        var timeout = options.Timeout;
        var retries = options.AdditionalRetries;

        if (model is not null)
        {
            ClientSettings.ValidateModel(model, $"{nameof(JevRequestOptions)}.{nameof(JevRequestOptions.Model)}");
        }

        if (timeout is { } t)
        {
            ClientSettings.ValidateTimeout(t, $"{nameof(JevRequestOptions)}.{nameof(JevRequestOptions.Timeout)}");
        }

        if (retries is { } r)
        {
            ClientSettings.ValidateRetries(r, $"{nameof(JevRequestOptions)}.{nameof(JevRequestOptions.AdditionalRetries)}");
        }

        return new CallSettings(
            model ?? client.DefaultModel,
            timeout ?? client.Timeout,
            retries ?? client.AdditionalRetries,
            options.Credential ?? client.Credential,
            options.CaptureRawResponse ?? client.CaptureRawResponse,
            options.CaptureErrorBody ?? client.CaptureErrorBody);
    }
}
