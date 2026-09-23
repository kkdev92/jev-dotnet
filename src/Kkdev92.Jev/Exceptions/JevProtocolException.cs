namespace Kkdev92.Jev;

/// <summary>
/// The service answered with success, and the answer does not satisfy the contract: malformed
/// JSON, a missing or extra answer, an answer of the wrong type, a label or level that was never
/// asked about, a number out of range.
/// </summary>
/// <remarks>
/// <para>
/// A 200 is not proof of a usable answer. Rather than fill a gap with a default or map an unknown
/// label to the nearest option, the SDK refuses the whole response. The request was processed and
/// probably billed, which <see cref="JevException.Transmission"/> reflects.
/// </para>
/// <para>
/// Fields the SDK does not know are ignored, so a response that grows is not a protocol error; a
/// response whose known fields change meaning is.
/// </para>
/// </remarks>
public sealed class JevProtocolException : JevException
{
    internal JevProtocolException(JevOperation operation, JevProtocolError error, string? requestId = null)
        : base(Describe(operation, error, requestId), operation, JevRequestTransmission.Started)
    {
        Error = error;
        RequestId = requestId;
    }

    /// <summary>What was wrong with the response.</summary>
    public JevProtocolError Error { get; }

    /// <summary>The <c>x-typesafe-request-id</c> of the response, when it had one of a plausible shape.</summary>
    public string? RequestId { get; }

    private static string Describe(JevOperation operation, JevProtocolError error, string? requestId)
    {
        var what = error switch
        {
            JevProtocolError.InvalidUtf8 => "the body is not valid UTF-8",
            JevProtocolError.MalformedJson => "the body is not a single well-formed JSON value",
            JevProtocolError.UnexpectedShape => "a field has a JSON type the contract does not allow",
            JevProtocolError.MissingField => "a required field is missing",
            JevProtocolError.DuplicateField => "a field appears twice",
            JevProtocolError.MissingAnswer => "a question has no answer",
            JevProtocolError.UnexpectedAnswer => "an answer names a question that was not asked",
            JevProtocolError.AnswerTypeMismatch => "an answer's type differs from its question's",
            JevProtocolError.UnknownAnswerType => "an answer has a type this SDK does not know",
            JevProtocolError.UnknownLabel => "an answer names an option that was not offered",
            JevProtocolError.MissingProbability => "an answer leaves out the probability of an option or level",
            JevProtocolError.InvalidLevel => "a score answer names a level that was not defined",
            JevProtocolError.MissingLegend => "a score answer leaves out the description of a level",
            JevProtocolError.NumberOutOfRange => "a number is outside the range the contract documents",
            JevProtocolError.NonFiniteNumber => "a number is too large to represent",
            JevProtocolError.InvalidTokenCount => "a token count is not a non-negative integer",
            JevProtocolError.NumericalInconsistency => "the probabilities are inconsistent with the answer beyond the configured tolerance",
            _ => "it does not satisfy the contract",
        };

        var message = $"The TypeSafe API's response to {Operations.Describe(operation)} was refused: {what}.";

        return requestId is null ? message : $"{message} Request id: {requestId}.";
    }
}

/// <summary>What was wrong with a response.</summary>
public enum JevProtocolError
{
    /// <summary>Anything not covered by a more specific value.</summary>
    Unknown = 0,

    /// <summary>The body is not valid UTF-8.</summary>
    InvalidUtf8,

    /// <summary>The body is not one well-formed JSON value, or it nests too deeply.</summary>
    MalformedJson,

    /// <summary>A known field holds a JSON type the contract does not allow, such as a string where a number belongs.</summary>
    UnexpectedShape,

    /// <summary>A required field is missing.</summary>
    MissingField,

    /// <summary>A known field appears twice, in any spelling.</summary>
    DuplicateField,

    /// <summary>A question in the plan has no answer.</summary>
    MissingAnswer,

    /// <summary>An answer is keyed by an id that is not in the plan.</summary>
    UnexpectedAnswer,

    /// <summary>An answer's <c>type</c> is not its question's.</summary>
    AnswerTypeMismatch,

    /// <summary>An answer's <c>type</c> is not one this SDK knows.</summary>
    UnknownAnswerType,

    /// <summary>A choice answer names a label, or gives a probability for a label, that the question did not offer.</summary>
    UnknownLabel,

    /// <summary>An option or level has no probability.</summary>
    MissingProbability,

    /// <summary>A score answer is keyed by something other than a level index of the question.</summary>
    InvalidLevel,

    /// <summary>A score answer's legend leaves out a level.</summary>
    MissingLegend,

    /// <summary>A probability, confidence or score is outside its documented range.</summary>
    NumberOutOfRange,

    /// <summary>A number overflowed: JSON cannot spell infinity, so the service did not send one.</summary>
    NonFiniteNumber,

    /// <summary>A token count is negative, fractional or too large.</summary>
    InvalidTokenCount,

    /// <summary>
    /// In <see cref="JevNumericalConsistency.Strict"/> mode, probabilities that do not add up, a
    /// choice that is not the most probable option, or a score that is not the expectation of its
    /// levels.
    /// </summary>
    NumericalInconsistency,
}
