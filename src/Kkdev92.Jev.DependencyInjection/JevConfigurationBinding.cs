using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Kkdev92.Jev.DependencyInjection;

internal static class JevConfigurationBinding
{
    private enum Key
    {
        BaseAddress,
        DefaultModel,
        UseLongContext,
        AuthenticationMode,
        AllowLoopbackHttp,
        Timeout,
        AdditionalRetries,
        RetryBackoffInitial,
        RetryBackoffMaximum,
        MaximumServerRetryWait,
        RetryNetworkFailures,
        MaxRequestBodyBytes,
        MaxResponseBodyBytes,
        MaxErrorBodyBytes,
        ConcurrencyLimit,
        MaxQueuedRequests,
        CaptureRawResponse,
        CaptureErrorBody,
        NumericalConsistency,
        NumericalAbsoluteTolerance,
        NumericalRelativeTolerance,
        AllowedInsecureHttpOrigins,
    }

    public static void Apply(IConfiguration configuration, JevClientOptions options)
    {
        foreach (var section in configuration.GetChildren())
        {
            var key = Symbol<Key>(section.Key, "option name");
            var value = section.Value;
            if (key == Key.AllowedInsecureHttpOrigins)
            {
                options.AllowedInsecureHttpOrigins = Origins(section, value);
                continue;
            }

            if (string.IsNullOrEmpty(value) || section.GetChildren().Any())
            {
                throw Invalid(key);
            }

            switch (key)
            {
                case Key.BaseAddress:
                    options.BaseAddress = Uri.TryCreate(value, UriKind.Absolute, out var address) ? address : throw Invalid(key);
                    break;
                case Key.DefaultModel:
                    options.DefaultModel = value;
                    break;
                case Key.UseLongContext:
                    options.UseLongContext = Boolean(value, key);
                    break;
                case Key.AuthenticationMode:
                    options.AuthenticationMode = Symbol<JevAuthenticationMode>(value, key);
                    break;
                case Key.AllowLoopbackHttp:
                    options.AllowLoopbackHttp = Boolean(value, key);
                    break;
                case Key.Timeout:
                    options.Timeout = string.Equals(value, "Infinite", StringComparison.OrdinalIgnoreCase) ? Timeout.InfiniteTimeSpan : Duration(value, key);
                    break;
                case Key.AdditionalRetries:
                    options.AdditionalRetries = Integer(value, key);
                    break;
                case Key.RetryBackoffInitial:
                    options.RetryBackoffInitial = Duration(value, key);
                    break;
                case Key.RetryBackoffMaximum:
                    options.RetryBackoffMaximum = Duration(value, key);
                    break;
                case Key.MaximumServerRetryWait:
                    options.MaximumServerRetryWait = Duration(value, key);
                    break;
                case Key.RetryNetworkFailures:
                    options.RetryNetworkFailures = Boolean(value, key);
                    break;
                case Key.MaxRequestBodyBytes:
                    options.MaxRequestBodyBytes = Integer(value, key);
                    break;
                case Key.MaxResponseBodyBytes:
                    options.MaxResponseBodyBytes = Integer(value, key);
                    break;
                case Key.MaxErrorBodyBytes:
                    options.MaxErrorBodyBytes = Integer(value, key);
                    break;
                case Key.ConcurrencyLimit:
                    options.ConcurrencyLimit = Integer(value, key);
                    break;
                case Key.MaxQueuedRequests:
                    options.MaxQueuedRequests = Integer(value, key);
                    break;
                case Key.CaptureRawResponse:
                    options.CaptureRawResponse = Boolean(value, key);
                    break;
                case Key.CaptureErrorBody:
                    options.CaptureErrorBody = Boolean(value, key);
                    break;
                case Key.NumericalConsistency:
                    options.NumericalConsistency = Symbol<JevNumericalConsistency>(value, key);
                    break;
                case Key.NumericalAbsoluteTolerance:
                    options.NumericalAbsoluteTolerance = Floating(value, key);
                    break;
                case Key.NumericalRelativeTolerance:
                    options.NumericalRelativeTolerance = Floating(value, key);
                    break;
            }
        }
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<Uri> Origins(IConfiguration section, string? value)
    {
        const Key key = Key.AllowedInsecureHttpOrigins;
        if (value is not null)
        {
            throw Invalid(key);
        }

        var entries = section.GetChildren().ToArray();
        var origins = new Uri[entries.Length];
        foreach (var entry in entries)
        {
            var index = entry.Key;
            if (!int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out int i)
                || (uint)i >= (uint)origins.Length || index != i.ToString(CultureInfo.InvariantCulture)
                || origins[i] is not null || entry.GetChildren().Any()
                || !Uri.TryCreate(entry.Value, UriKind.Absolute, out var uri))
            {
                throw Invalid(key);
            }

            origins[i] = uri;
        }

        return Array.AsReadOnly(origins);
    }

    private static T Symbol<T>(string value, object key) where T : struct, Enum
        => Enum.TryParse<T>(value, true, out var result) && string.Equals(Enum.GetName(result), value, StringComparison.OrdinalIgnoreCase)
            ? result : throw Invalid(key);

    private static bool Boolean(string value, Key key) =>
        bool.TryParse(value, out var result) ? result : throw Invalid(key);

    private static int Integer(string value, Key key) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : throw Invalid(key);

    private static double Floating(string value, Key key) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : throw Invalid(key);

    private static TimeSpan Duration(string value, Key key) =>
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var result) ? result : throw Invalid(key);

    private static OptionsValidationException Invalid(object knownKey) =>
        new(Options.DefaultName, typeof(JevClientOptions), [$"Invalid Jev configuration {knownKey}."]);
}
