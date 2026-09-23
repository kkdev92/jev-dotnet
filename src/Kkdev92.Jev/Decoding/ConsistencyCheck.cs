using Kkdev92.Jev.Planning;

namespace Kkdev92.Jev.Decoding;

/// <summary>
/// Checks that each answer's numbers agree with each other, within a tolerance, and changes none of them.
/// </summary>
/// <remarks>
/// <para>
/// The contract describes three relationships: a distribution sums to approximately one, the chosen
/// option is the one with the highest probability, and a score is the probability-weighted mean of
/// its level indices. A response that breaks one is either rounded more coarsely than the tolerance
/// or wrong, and the SDK cannot tell which — so by default it says so on the result and returns the
/// values untouched. It never renormalises a distribution, re-derives a confidence, or replaces a
/// score with the mean it computed.
/// </para>
/// <para>
/// The relationships TypeSafe explicitly does not promise — a noul and a two-option choice asking
/// the same thing, a question and its negation — are not checked. Its own model notes say they do
/// not hold.
/// </para>
/// </remarks>
internal static class ConsistencyCheck
{
    public static IReadOnlyList<JevConsistencyWarning> Run(
        JevDecisionPlan plan,
        DecodedResponse decoded,
        double absolute,
        double relative,
        JevNumericalConsistency mode,
        string? requestId)
    {
        List<JevConsistencyWarning>? warnings = null;

        foreach (var question in plan.Questions)
        {
            if (question.Kind == QuestionKind.Noul)
            {
                continue;
            }

            var slot = decoded.Slots[question.Slot];
            var distribution = decoded.Probabilities.AsSpan(question.ProbabilityOffset, question.OutcomeCount);

            var sum = 0.0;
            var mean = 0.0;
            var max = double.NegativeInfinity;

            for (var i = 0; i < distribution.Length; i++)
            {
                sum += distribution[i];
                mean += i * distribution[i];
                max = Math.Max(max, distribution[i]);
            }

            if (Math.Abs(sum - 1) > absolute + relative)
            {
                (warnings ??= []).Add(new JevConsistencyWarning(question.Id, JevConsistencyCheck.ProbabilitySum, sum - 1));
            }

            if (question.Kind == QuestionKind.Choice)
            {
                // A tie at the top is consistent with choosing either.
                var gap = max - distribution[slot.Selected];

                if (gap > absolute + (relative * max))
                {
                    (warnings ??= []).Add(new JevConsistencyWarning(question.Id, JevConsistencyCheck.ChoiceIsMostProbable, gap));
                }
            }
            else
            {
                var deviation = slot.Value - mean;

                if (Math.Abs(deviation) > absolute + (relative * Math.Max(Math.Abs(slot.Value), Math.Abs(mean))))
                {
                    (warnings ??= []).Add(new JevConsistencyWarning(question.Id, JevConsistencyCheck.ScoreIsExpectation, deviation));
                }
            }
        }

        if (warnings is null)
        {
            return [];
        }

        return mode == JevNumericalConsistency.Strict
            ? throw new JevProtocolException(JevOperation.Evaluate, JevProtocolError.NumericalInconsistency, requestId)
            : warnings;
    }
}
