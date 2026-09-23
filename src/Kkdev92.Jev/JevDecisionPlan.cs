using System.Globalization;
using Kkdev92.Jev.Planning;

namespace Kkdev92.Jev;

/// <summary>
/// A fixed set of questions, validated and encoded once, ready to be asked about any number of states.
/// </summary>
/// <remarks>
/// <para>
/// Built by <see cref="JevDecisionPlanBuilder"/>. Everything about the questions that does not
/// depend on the state — checking ids and labels, copying options, encoding the <c>questions</c>
/// object — happens once, in <see cref="JevDecisionPlanBuilder.Build"/>. A call then writes the
/// model and the state and copies the prepared bytes in after them.
/// </para>
/// <para>
/// That is a saving in this process, not at the service: the questions are still sent, and billed,
/// with every call. Nothing here caches a result or a prompt.
/// </para>
/// <para>
/// Immutable and safe to share across threads and calls. It holds no credential, no
/// <see cref="HttpClient"/> and no state. Keep one per use case for as long as the use case lives;
/// the SDK keeps no global cache of plans.
/// </para>
/// </remarks>
public sealed class JevDecisionPlan
{
    private readonly Dictionary<string, int> _slotById;
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _slotLookup;

    internal JevDecisionPlan(PlanToken token, QuestionDefinition[] questions, byte[] encodedQuestions)
    {
        Token = token;
        Questions = questions;
        EncodedQuestions = encodedQuestions;

        _slotById = new Dictionary<string, int>(questions.Length, StringComparer.Ordinal);
        var offset = 0;

        for (var i = 0; i < questions.Length; i++)
        {
            _slotById.Add(questions[i].Id, i);
            questions[i].ProbabilityOffset = offset;
            offset += questions[i].OutcomeCount;
            HasScores |= questions[i].Kind == QuestionKind.Score;
        }

        ProbabilityCount = offset;
        _slotLookup = _slotById.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>How many questions the plan asks.</summary>
    public int QuestionCount => Questions.Length;

    /// <summary>The size of the encoded <c>questions</c> object, in bytes: what every call sends besides the model and the state.</summary>
    public int EncodedQuestionsLength => EncodedQuestions.Length;

    internal PlanToken Token { get; }

    internal QuestionDefinition[] Questions { get; }

    /// <summary>The UTF-8 JSON of the <c>questions</c> object, written and validated by the builder.</summary>
    internal byte[] EncodedQuestions { get; }

    /// <summary>How many probabilities a result holds across all questions.</summary>
    internal int ProbabilityCount { get; }

    internal bool HasScores { get; }

    /// <summary>The slot of a question id, or -1.</summary>
    internal int SlotOf(ReadOnlySpan<char> id) => _slotLookup.TryGetValue(id, out var slot) ? slot : -1;

    /// <summary>The slot of a question id, or -1.</summary>
    internal int SlotOf(string id) => _slotById.TryGetValue(id, out var slot) ? slot : -1;

    /// <summary>The question count. Never the questions.</summary>
    public override string ToString() => $"JevDecisionPlan ({QuestionCount.ToString(CultureInfo.InvariantCulture)} questions)";
}
