namespace Kkdev92.Jev;

/// <summary>
/// Overrides for one call. Anything left <see langword="null"/> falls back to the client's
/// <see cref="JevClientOptions"/>.
/// </summary>
/// <remarks>
/// Read once, when the call starts. Changing the instance while the call runs has no effect on it,
/// so one instance can be shared across calls.
/// </remarks>
public sealed class JevRequestOptions
{
    /// <summary>The model for this call, instead of <see cref="JevClientOptions.DefaultModel"/>.</summary>
    public string? Model { get; set; }

    /// <summary>The deadline for this call, instead of <see cref="JevClientOptions.Timeout"/>.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>The retry count for this call, instead of <see cref="JevClientOptions.AdditionalRetries"/>. 0 is a real value, not "unset".</summary>
    public int? AdditionalRetries { get; set; }

    /// <summary>
    /// The credential for this call, instead of <see cref="JevClientOptions.Credential"/>. Lets one
    /// client serve several accounts without the key of one lingering where another could send it.
    /// </summary>
    public IJevCredential? Credential { get; set; }

    /// <summary>Whether to keep the successful response body for this call.</summary>
    public bool? CaptureRawResponse { get; set; }

    /// <summary>Whether to keep the error body for this call.</summary>
    public bool? CaptureErrorBody { get; set; }
}
