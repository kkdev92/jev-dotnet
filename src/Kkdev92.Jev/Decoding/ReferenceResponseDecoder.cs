using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Kkdev92.Jev.Planning;
using Kkdev92.Jev.Wire;

namespace Kkdev92.Jev.Decoding;

/// <summary>
/// Decodes a <c>POST /v1/systemone</c> response through the source-generated wire models, then
/// maps it onto the plan.
/// </summary>
/// <remarks>
/// <para>
/// The straightforward way to read the response: <see cref="JsonSerializer"/> with the generated
/// <see cref="JevWireJsonContext"/>, whose options refuse duplicate properties, read the
/// discriminator wherever it appears and enforce nullability; then the generated
/// <c>Validate</c> methods for what the serializer does not check; then the mapping onto slots.
/// </para>
/// <para>
/// It exists as the reference the handwritten <see cref="SystemOneResponseReader"/> is tested
/// against, and as the baseline it is measured against. It allocates a dictionary per answer and
/// per distribution, which is the cost the reader removes. Where the two disagree on whether a body
/// is acceptable, one of them is wrong, and a test says which.
/// </para>
/// <para>
/// One such test says it is this one. The serializer reserves names beginning with <c>$</c> for
/// its own metadata inside a polymorphic type, so an answer carrying such a field — which the
/// contract says to ignore — is refused here and kept by the reader. That is the only difference
/// the tests excuse.
/// </para>
/// </remarks>
internal static class ReferenceResponseDecoder
{
    public static DecodedResponse Decode(ReadOnlySpan<byte> body, JevDecisionPlan plan, double tolerance)
    {
        SystemOneResponse? response;

        try
        {
            response = JsonSerializer.Deserialize(body, JevWireJsonContext.Default.SystemOneResponse);
        }
        catch (JsonException)
        {
            throw Error(JevProtocolError.MalformedJson);
        }
        catch (NotSupportedException)
        {
            // What the serializer throws for a polymorphic object with no discriminator.
            throw Error(JevProtocolError.MissingField);
        }

        if (response is null)
        {
            throw Error(JevProtocolError.UnexpectedShape);
        }

        var validation = new WireValidation();
        response.Validate(validation, "$");

        if (!validation.IsValid)
        {
            throw Error(validation.Violations[0].Rule switch
            {
                WireRule.Missing => JevProtocolError.MissingField,
                WireRule.NotFinite => JevProtocolError.NonFiniteNumber,
                WireRule.TooFew => JevProtocolError.MissingAnswer,
                _ => JevProtocolError.UnexpectedShape,
            });
        }

        var questions = plan.Questions;
        var slots = new AnswerSlot[questions.Length];
        var probabilities = new double[plan.ProbabilityCount];
        var legends = plan.HasScores ? new ImmutableArray<JsonElement>[questions.Length] : null;

        foreach (var (id, answer) in response.Answers)
        {
            var slot = plan.SlotOf(id);

            if (slot < 0)
            {
                throw Error(JevProtocolError.UnexpectedAnswer);
            }

            slots[slot] = (questions[slot], answer) switch
            {
                (NoulQuestionDefinition, Wire.NoulAnswer noul) => new AnswerSlot
                {
                    Value = InRange(noul.Noul, 1, tolerance),
                    Confidence = double.NaN,
                    Selected = -1,
                },
                (ChoiceQuestionDefinition question, Wire.ChoiceAnswer choice) => MapChoice(question, choice, probabilities, tolerance),
                (ScoreQuestionDefinition question, Wire.ScoreAnswer score) => MapScore(question, score, probabilities, out legends![slot], tolerance),
                _ => throw Error(JevProtocolError.AnswerTypeMismatch),
            };
        }

        // The serializer refused duplicate keys and every id mapped to a distinct slot, so equal
        // counts mean every question was answered.
        if (response.Answers.Count != questions.Length)
        {
            throw Error(JevProtocolError.MissingAnswer);
        }

        if (response.Usage.InputTokens < 0 || response.Usage.OutputTokens < 0)
        {
            throw Error(JevProtocolError.InvalidTokenCount);
        }

        return new DecodedResponse(response.Model, new JevUsage(response.Usage.InputTokens, response.Usage.OutputTokens), slots, probabilities, legends);
    }

    private static AnswerSlot MapChoice(ChoiceQuestionDefinition question, Wire.ChoiceAnswer answer, double[] probabilities, double tolerance)
    {
        var selected = question.IndexOf(answer.Choice);

        if (selected < 0)
        {
            throw Error(JevProtocolError.UnknownLabel);
        }

        foreach (var (label, probability) in answer.Probabilities)
        {
            var index = question.IndexOf(label);

            if (index < 0)
            {
                throw Error(JevProtocolError.UnknownLabel);
            }

            probabilities[question.ProbabilityOffset + index] = InRange(probability, 1, tolerance);
        }

        if (answer.Probabilities.Count != question.Labels.Length)
        {
            throw Error(JevProtocolError.MissingProbability);
        }

        return new AnswerSlot
        {
            Value = double.NaN,
            Confidence = InRange(answer.Confidence, 1, tolerance),
            Selected = selected,
        };
    }

    private static AnswerSlot MapScore(ScoreQuestionDefinition question, Wire.ScoreAnswer answer, double[] probabilities, out ImmutableArray<JsonElement> legend, double tolerance)
    {
        var levels = question.Levels.Length;

        foreach (var (key, probability) in answer.Probabilities)
        {
            probabilities[question.ProbabilityOffset + Level(key, levels)] = InRange(probability, 1, tolerance);
        }

        if (answer.Probabilities.Count != levels)
        {
            throw Error(JevProtocolError.MissingProbability);
        }

        var values = new JsonElement[levels];

        foreach (var (key, value) in answer.Legend)
        {
            values[Level(key, levels)] = value;
        }

        if (answer.Legend.Count != levels)
        {
            throw Error(JevProtocolError.MissingLegend);
        }

        legend = ImmutableCollectionsMarshal.AsImmutableArray(values);

        return new AnswerSlot
        {
            Value = InRange(answer.Score, levels - 1, tolerance),
            Confidence = InRange(answer.Confidence, 1, tolerance),
            Selected = -1,
        };
    }

    /// <summary>A level key, matched as canonical decimal text.</summary>
    private static int Level(string key, int levels)
    {
        for (var level = 0; level < levels; level++)
        {
            if (key == Keys[level])
            {
                return level;
            }
        }

        throw Error(JevProtocolError.InvalidLevel);
    }

    private static readonly string[] Keys = [.. Enumerable.Range(0, JevContract.ScoreLevelsMaximum).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))];

    private static double InRange(double value, double maximum, double tolerance)
        => value >= -tolerance && value <= maximum + tolerance
            ? value
            : throw Error(JevProtocolError.NumberOutOfRange);

    private static JevProtocolException Error(JevProtocolError error) => new(JevOperation.Evaluate, error);
}
