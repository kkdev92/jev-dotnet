using System.Globalization;
using Kkdev92.Jev.Decoding;
using Kkdev92.Jev.Planning;

namespace Kkdev92.Jev;

/// <summary>
/// The answers to one evaluation, with the model that produced them and what it used.
/// </summary>
/// <remarks>
/// <para>
/// Every question of the plan has exactly one answer here; a response missing one is refused
/// rather than returned in part. Read an answer with the handle its question was added with.
/// </para>
/// <para>
/// A plain heap object: it owns its data outright, needs no disposal, and holds no reference to a
/// buffer, a connection or the response it came from. Safe to keep, and to read from any thread.
/// </para>
/// </remarks>
public sealed class JevResult
{
    private readonly DecodedResponse _decoded;

    internal JevResult(JevDecisionPlan plan, string requestedModel, DecodedResponse decoded, JevResultMetadata metadata, ReadOnlyMemory<byte>? rawResponseBody)
    {
        Plan = plan;
        RequestedModel = requestedModel;
        _decoded = decoded;
        Metadata = metadata;
        RawResponseBody = rawResponseBody;
    }

    /// <summary>The plan this result answers.</summary>
    public JevDecisionPlan Plan { get; }

    /// <summary>The model name the request sent: an alias such as <c>jev-latest</c>, or a versioned id.</summary>
    public string RequestedModel { get; }

    /// <summary>
    /// The model that answered, as the service reported it. For an alias this is the version it
    /// resolved to, such as <c>jev-1.13.0</c> — the value to log next to a decision.
    /// </summary>
    public string ActualModel => _decoded.Model;

    /// <summary>The token counts the service reported. Input tokens are what TypeSafe bills.</summary>
    public JevUsage Usage => _decoded.Usage;

    /// <summary>What the SDK observed about the call.</summary>
    public JevResultMetadata Metadata { get; }

    /// <summary>
    /// The response body exactly as received, when <see cref="JevClientOptions.CaptureRawResponse"/>
    /// or <see cref="JevRequestOptions.CaptureRawResponse"/> asked for it. Otherwise <see langword="null"/>.
    /// </summary>
    /// <remarks>An independent copy, kept for as long as this result is.</remarks>
    public ReadOnlyMemory<byte>? RawResponseBody { get; }

    /// <summary>The answer to a choice question.</summary>
    /// <typeparam name="T">The option value type.</typeparam>
    /// <param name="handle">The handle <see cref="JevDecisionPlanBuilder.AddChoice{T}"/> returned.</param>
    /// <exception cref="InvalidOperationException">The handle is default, or belongs to another plan.</exception>
    public ChoiceAnswer<T> Get<T>(JevChoiceHandle<T> handle)
        where T : notnull
    {
        var definition = handle.Definition;
        var slot = SlotOf(definition);
        return new ChoiceAnswer<T>(definition!, _decoded.Probabilities, _decoded.Slots[slot]);
    }

    /// <summary>The answer to a score question.</summary>
    /// <param name="handle">The handle <see cref="JevDecisionPlanBuilder.AddScore"/> returned.</param>
    /// <exception cref="InvalidOperationException">The handle is default, or belongs to another plan.</exception>
    public ScoreAnswer Get(JevScoreHandle handle)
    {
        var definition = handle.Definition;
        var slot = SlotOf(definition);
        return new ScoreAnswer(definition!, _decoded.Probabilities, _decoded.Slots[slot], _decoded.Legends![slot]);
    }

    /// <summary>The answer to a noul question.</summary>
    /// <param name="handle">The handle <see cref="JevDecisionPlanBuilder.AddNoul"/> returned.</param>
    /// <exception cref="InvalidOperationException">The handle is default, or belongs to another plan.</exception>
    public NoulAnswer Get(JevNoulHandle handle)
    {
        var slot = SlotOf(handle.Definition);
        return new NoulAnswer(_decoded.Slots[slot].Value);
    }

    /// <summary>The answer count and the model. Never an answer.</summary>
    public override string ToString() => $"JevResult ({Plan.QuestionCount.ToString(CultureInfo.InvariantCulture)} answers from {ActualModel})";

    /// <summary>
    /// The slot a handle's question occupies, after proving the handle belongs to this result's plan.
    /// </summary>
    /// <remarks>
    /// Ownership is checked by reference before the slot is touched, so a handle from another plan
    /// that happens to share an id — or a slot number — cannot read the wrong answer.
    /// </remarks>
    private int SlotOf(QuestionDefinition? definition)
    {
        if (definition is null)
        {
            throw new InvalidOperationException("The handle is default and identifies no question.");
        }

        if (!ReferenceEquals(definition.Owner, Plan.Token))
        {
            throw new InvalidOperationException("The handle belongs to a different plan than the one this result answers.");
        }

        return definition.Slot;
    }
}

/// <summary>The token counts of one evaluation, as the service reported them.</summary>
/// <param name="InputTokens">Input tokens, which TypeSafe bills.</param>
/// <param name="OutputTokens">Output tokens. TypeSafe documents them as currently free of charge, which is not the same as zero.</param>
public readonly record struct JevUsage(long InputTokens, long OutputTokens);

/// <summary>What the SDK observed about a successful call.</summary>
public readonly struct JevResultMetadata
{
    internal JevResultMetadata(string? requestId, int attempts, Version httpVersion, TimeSpan duration, IReadOnlyList<JevConsistencyWarning> warnings)
    {
        RequestId = requestId;
        Attempts = attempts;
        HttpVersion = httpVersion;
        Duration = duration;
        Warnings = warnings;
    }

    /// <summary>The <c>x-typesafe-request-id</c> of the response, when it had one of a plausible shape.</summary>
    public string? RequestId { get; }

    /// <summary>How many HTTP requests the call made: 1, plus one per retry.</summary>
    public int Attempts { get; }

    /// <summary>The HTTP version of the response that answered. The client asks for HTTP/2 and accepts HTTP/1.1.</summary>
    public Version HttpVersion { get; }

    /// <summary>The whole call, from its start to the decoded result, including any wait for admission and any backoff.</summary>
    public TimeSpan Duration { get; }

    /// <summary>
    /// Answers whose numbers did not agree with each other within the tolerance, in
    /// <see cref="JevNumericalConsistency.Report"/> mode. Empty when they all did.
    /// </summary>
    public IReadOnlyList<JevConsistencyWarning> Warnings { get; }
}

/// <summary>An answer whose numbers disagree with each other beyond the configured tolerance.</summary>
/// <param name="QuestionId">The question whose answer it is.</param>
/// <param name="Check">Which check failed.</param>
/// <param name="Deviation">How far off it was, in the unit of the check.</param>
public readonly record struct JevConsistencyWarning(string QuestionId, JevConsistencyCheck Check, double Deviation);

/// <summary>A numerical consistency check.</summary>
public enum JevConsistencyCheck
{
    /// <summary>The probabilities of an answer sum to one. The deviation is the sum minus one.</summary>
    ProbabilitySum = 1,

    /// <summary>A choice is its most probable option. The deviation is how much more probable the most probable option is.</summary>
    ChoiceIsMostProbable = 2,

    /// <summary>A score is the probability-weighted mean of its levels. The deviation is the score minus that mean.</summary>
    ScoreIsExpectation = 3,
}
