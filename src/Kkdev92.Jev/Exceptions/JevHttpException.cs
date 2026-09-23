using System.Globalization;
using System.Text;

namespace Kkdev92.Jev;

/// <summary>The service answered with a status that is not success.</summary>
/// <remarks>
/// <para>
/// The message carries the operation, the status, the machine-readable error type when it is one
/// this SDK knows, the validation error types of a 422, and the request id. It never carries the
/// server's own message: that text can quote the request.
/// </para>
/// <para>
/// The body is read under a byte limit and kept only when <see cref="JevClientOptions.CaptureErrorBody"/>
/// or <see cref="JevRequestOptions.CaptureErrorBody"/> asks for it.
/// </para>
/// </remarks>
public sealed class JevHttpException : JevException
{
    /// <summary>The error types the service has been seen to send, and that are therefore safe to put in a message.</summary>
    private static readonly HashSet<string> KnownErrorTypes = new(StringComparer.Ordinal)
    {
        "authentication_error",
    };

    internal JevHttpException(
        JevOperation operation,
        int statusCode,
        string? requestId,
        string? errorType,
        TimeSpan? retryAfter,
        IReadOnlyList<JevValidationError> validationErrors,
        ReadOnlyMemory<byte>? errorBody)
        : base(Describe(operation, statusCode, requestId, errorType, retryAfter, validationErrors), operation, JevRequestTransmission.Started)
    {
        StatusCode = statusCode;
        RequestId = requestId;
        ErrorType = errorType;
        RetryAfter = retryAfter;
        ValidationErrors = validationErrors;
        ErrorBody = errorBody;
    }

    /// <summary>The HTTP status.</summary>
    public int StatusCode { get; }

    /// <summary>The <c>x-typesafe-request-id</c> response header, when the service sent one of a plausible shape. Quote it when asking TypeSafe about a failure.</summary>
    public string? RequestId { get; }

    /// <summary>
    /// The machine-readable <c>detail.error_type</c> of the body, such as <c>authentication_error</c>.
    /// </summary>
    /// <remarks>
    /// Passed through as the service sent it, when it is lower-case letters, digits and underscores,
    /// so that code can branch on a value this SDK has not seen yet. Only values this SDK knows
    /// appear in <see cref="Exception.Message"/>. The shape is observed, not documented: the OpenAPI
    /// document describes only the 422 body.
    /// </remarks>
    public string? ErrorType { get; }

    /// <summary>How long the service asked the client to wait, from <c>retry-after-ms</c> or <c>Retry-After</c>.</summary>
    /// <remarks>
    /// <c>retry-after-ms</c> is read first when the service sends both. A wait longer than a
    /// <see cref="TimeSpan"/> holds is <see cref="TimeSpan.MaxValue"/>.
    /// </remarks>
    public TimeSpan? RetryAfter { get; }

    /// <summary>
    /// The entries of a 422 body: where each invalid value was, and the machine-readable reason.
    /// </summary>
    /// <remarks>
    /// Empty when the body carried none. The service's human-readable text for each entry, and the
    /// input it quotes, are not kept here; they are in <see cref="ErrorBody"/> when captured.
    /// </remarks>
    public IReadOnlyList<JevValidationError> ValidationErrors { get; }

    /// <summary>The body as received, up to the error-body limit, when capturing was asked for. Otherwise <see langword="null"/>.</summary>
    public ReadOnlyMemory<byte>? ErrorBody { get; }

    /// <summary>True for <c>429 Too Many Requests</c>.</summary>
    public bool IsRateLimited => StatusCode == 429;

    /// <summary>True for <c>529</c>, which TypeSafe documents as temporarily overloaded.</summary>
    public bool IsOverloaded => StatusCode == Wire.JevContract.OverloadedStatus;

    private static string Describe(
        JevOperation operation,
        int statusCode,
        string? requestId,
        string? errorType,
        TimeSpan? retryAfter,
        IReadOnlyList<JevValidationError> validationErrors)
    {
        var builder = new StringBuilder("The TypeSafe API returned ");
        builder.Append(statusCode.ToString(CultureInfo.InvariantCulture));

        if (ReasonPhrase(statusCode) is { } phrase)
        {
            builder.Append(" (").Append(phrase).Append(')');
        }

        builder.Append(" for ").Append(Operations.Describe(operation)).Append('.');

        if (errorType is not null && KnownErrorTypes.Contains(errorType))
        {
            builder.Append(" Error type: ").Append(errorType).Append('.');
        }

        if (validationErrors.Count > 0)
        {
            // Types only, and only a handful. Locations can name a question or a label.
            var types = validationErrors.Select(e => e.Type).Distinct(StringComparer.Ordinal).Take(5);
            builder.Append(" Validation error types: ").AppendJoin(", ", types).Append('.');
        }

        if (retryAfter is { } wait)
        {
            builder.Append(" Retry after ").Append(wait.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)).Append(" s.");
        }

        if (requestId is not null)
        {
            builder.Append(" Request id: ").Append(requestId).Append('.');
        }

        return builder.ToString();
    }

    /// <summary>Fixed phrases rather than the response's own, which the server writes.</summary>
    private static string? ReasonPhrase(int statusCode) => statusCode switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        408 => "Request Timeout",
        413 => "Content Too Large",
        422 => "Unprocessable Entity",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        529 => "Overloaded",
        _ => null,
    };
}

/// <summary>One entry of a 422 body.</summary>
/// <remarks>
/// <see cref="Location"/> can contain question ids and option labels, which are yours but may be
/// sensitive; it is kept off the exception message for that reason.
/// </remarks>
public sealed class JevValidationError
{
    internal JevValidationError(IReadOnlyList<string> location, string type)
    {
        Location = location;
        Type = type;
    }

    /// <summary>The path to the invalid value, such as <c>body</c>, <c>questions</c>, a question id, <c>criteria</c>. Array indices appear as their decimal text.</summary>
    public IReadOnlyList<string> Location { get; }

    /// <summary>The machine-readable reason, such as <c>missing</c>.</summary>
    public string Type { get; }

    /// <summary>The type only. The location can contain your own identifiers.</summary>
    public override string ToString() => Type;
}
