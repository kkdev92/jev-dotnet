using System.Globalization;

namespace Kkdev92.Jev;

/// <summary>A local limit refused the call: a body too large to send or to read, or a full admission queue.</summary>
/// <remarks>
/// These are the SDK's own safety limits, set in <see cref="JevClientOptions"/>. They are not the
/// service's limits: a body under <see cref="JevClientOptions.MaxRequestBodyBytes"/> can still be
/// over the model's context length, which is measured in tokens the SDK cannot count.
/// </remarks>
public sealed class JevLimitException : JevException
{
    internal JevLimitException(JevOperation operation, JevLimit limit, long? limitValue, JevRequestTransmission transmission)
        : base(Describe(operation, limit, limitValue), operation, transmission)
    {
        Limit = limit;
        LimitValue = limitValue;
    }

    /// <summary>Which limit was reached.</summary>
    public JevLimit Limit { get; }

    /// <summary>The configured value of that limit, in bytes or requests.</summary>
    public long? LimitValue { get; }

    private static string Describe(JevOperation operation, JevLimit limit, long? value)
    {
        var configured = value?.ToString("N0", CultureInfo.InvariantCulture) ?? "?";

        return limit switch
        {
            JevLimit.RequestBody => $"The request body for {Operations.Describe(operation)} exceeds {configured} bytes (JevClientOptions.MaxRequestBodyBytes). Nothing was sent.",
            JevLimit.ResponseBody => $"The response to {Operations.Describe(operation)} exceeds {configured} bytes (JevClientOptions.MaxResponseBodyBytes).",
            JevLimit.Queue => $"{Operations.Describe(operation)} was refused because all {configured} queue slots are taken (JevClientOptions.MaxQueuedRequests). Nothing was sent.",
            _ => $"A local limit refused {Operations.Describe(operation)}.",
        };
    }
}

/// <summary>A local limit.</summary>
public enum JevLimit
{
    /// <summary>Anything not covered by a more specific value.</summary>
    Unknown = 0,

    /// <summary><see cref="JevClientOptions.MaxRequestBodyBytes"/>.</summary>
    RequestBody,

    /// <summary><see cref="JevClientOptions.MaxResponseBodyBytes"/>.</summary>
    ResponseBody,

    /// <summary><see cref="JevClientOptions.MaxQueuedRequests"/>: every permit was taken and so was every queue slot.</summary>
    Queue,
}
