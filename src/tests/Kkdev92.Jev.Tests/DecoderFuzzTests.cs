using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using Kkdev92.Jev.Decoding;
using Kkdev92.Jev.Planning;
using Kkdev92.Jev.Tests.Fuzzing;
using Xunit.Sdk;

namespace Kkdev92.Jev.Tests;

/// <summary>
/// The two response decoders, held to each other — and to the contract — over generated responses.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ResponseDecodingTests"/> pins behaviour case by case. This widens the same judgement
/// to responses nobody wrote by hand: a random plan with awkward ids and labels, a valid response
/// to it, and a variant of that response made by <see cref="ResponseFuzzer"/>. For every case,
/// neither decoder throws anything but <see cref="JevProtocolException"/>; they accept or refuse
/// together; an accepted body decodes to identical values in both; a body changed only in ways
/// JSON says do not matter decodes to exactly what the canonical body does; and a body with a
/// fault the contract forbids is refused.
/// </para>
/// <para>
/// Deterministic: fixed seeds, and each case a pure function of its seed and iteration.
/// <c>JEV_DOTNET_FUZZ_ITERATIONS</c> sets how many cases each seed runs, and
/// <c>JEV_DOTNET_FUZZ_SEED</c> runs that one seed instead of the fixed ones, for a longer search
/// on a developer machine. A failure names its seed and iteration, which is all it takes to
/// reproduce it.
/// </para>
/// <para>
/// Bodies are always valid UTF-8. The client checks the whole body before either decoder sees it
/// (<c>ErrorContractTests</c> covers that refusal), and the reader is only defined for bodies that
/// pass — so this checks the pipeline the client runs, not the reader alone.
/// </para>
/// </remarks>
public sealed class DecoderFuzzTests
{
    private const int DefaultIterations = 1000;

    /// <summary>
    /// A decode of these small bodies takes microseconds. The bound is loose enough for a slow CI
    /// machine and only catches a runaway: something quadratic, or a loop that does not end.
    /// </summary>
    private static readonly TimeSpan SlowDecode = TimeSpan.FromSeconds(5);

    public static TheoryData<int> Seeds => Setting("JEV_DOTNET_FUZZ_SEED") is { } seed ? [seed] : [1, 2, 3, 4];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void GeneratedResponsesAreDecodedAlikeAndAsTheContractSays(int seed)
    {
        var iterations = Setting("JEV_DOTNET_FUZZ_ITERATIONS") ?? DefaultIterations;
        var changes = new Dictionary<string, int>(StringComparer.Ordinal);
        var outcomes = new Dictionary<(FuzzExpectation Expectation, bool Accepted), int>();

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var fuzzCase = ResponseFuzzer.Create(seed, iteration);
            var accepted = Check(fuzzCase);

            outcomes[(fuzzCase.Expectation, accepted)] = outcomes.GetValueOrDefault((fuzzCase.Expectation, accepted)) + 1;

            foreach (var change in fuzzCase.Changes)
            {
                var name = change.Split(": ", 2)[0];
                changes[name] = changes.GetValueOrDefault(name) + 1;
            }
        }

        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"Seed {seed.ToString(CultureInfo.InvariantCulture)}, {iterations.ToString(CultureInfo.InvariantCulture)} cases.");

        foreach (var ((expectation, accepted), count) in outcomes.OrderBy(o => o.Key))
        {
            output?.WriteLine($"  {expectation}, {(accepted ? "accepted" : "refused")}: {count.ToString(CultureInfo.InvariantCulture)}");
        }

        foreach (var (name, count) in changes.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            output?.WriteLine($"  {name}: {count.ToString(CultureInfo.InvariantCulture)}");
        }

        if (iterations >= DefaultIterations)
        {
            // A fuzzer that stopped making some change, or whose unpredicted changes all land on one
            // side, would pass without testing what it claims to.
            var neverMade = ResponseFuzzer.ChangeNames.Where(name => !changes.ContainsKey(name)).ToList();
            Assert.True(neverMade.Count == 0, "Never made: " + string.Join(", ", neverMade));
            Assert.True(outcomes.ContainsKey((FuzzExpectation.Agreed, true)), "No unpredicted change was accepted.");
            Assert.True(outcomes.ContainsKey((FuzzExpectation.Agreed, false)), "No unpredicted change was refused.");
        }
    }

    /// <summary>Runs one case through both decoders and checks every promise; returns whether the body was accepted.</summary>
    private static bool Check(FuzzCase fuzzCase)
    {
        var plan = fuzzCase.Plan;
        var canonical = Encoding.UTF8.GetBytes(fuzzCase.Canonical);
        var body = Encoding.UTF8.GetBytes(fuzzCase.Body);

        // The generator's own promise first: the canonical body is a valid response.
        var expected = Run(fuzzCase, "The client", () => Pipeline(canonical, plan)).Decoded
            ?? throw Failure(fuzzCase, "The client refused the canonical body, which the generator promises is valid.");

        var reader = Run(fuzzCase, "The client", () => Pipeline(body, plan));
        var reference = Run(fuzzCase, "The reference decoder", () => ReferenceResponseDecoder.Decode(body, plan, ResponseFuzzer.Tolerance));

        if (reader.Accepted != reference.Accepted && !(reader.Accepted && HasDollarNameInAnAnswer(body)))
        {
            throw Failure(fuzzCase, $"The decoders disagree: the client {reader}, the reference decoder {reference}.");
        }

        if (reader.Accepted && reference.Accepted)
        {
            Compare(fuzzCase, "The decoders read the body differently", reference.Decoded!, reader.Decoded!, exact: true);
        }

        switch (fuzzCase.Expectation)
        {
            case FuzzExpectation.Unchanged when !reader.Accepted:
                throw Failure(fuzzCase, $"Both decoders refused a body that changed only in ways JSON ignores ({reader}).");

            case FuzzExpectation.Unchanged:
                Compare(fuzzCase, "A change JSON ignores changed what was read", expected, reader.Decoded!, exact: false);
                break;

            case FuzzExpectation.Refused when reader.Accepted:
                throw Failure(fuzzCase, $"The client accepted a body with a fault the contract forbids; the reference decoder {reference}.");
        }

        return reader.Accepted;
    }

    /// <summary>
    /// True when an answer carries a field whose name starts with <c>$</c>: the one input on which
    /// the reference decoder is known to be wrong, and the only disagreement this test excuses.
    /// </summary>
    /// <remarks>
    /// The serializer reserves such names for its own metadata inside a polymorphic type, and
    /// refuses the answer; the contract says an unknown field is ignored, and the client ignores it.
    /// <see cref="ResponseDecodingTests.AFieldNamedWithADollarInsideAnAnswerIsIgnoredThoughTheReferenceRefusesIt"/>
    /// pins both halves. Asked only of a body the client accepted, which therefore parses.
    /// </remarks>
    private static bool HasDollarNameInAnAnswer(byte[] body)
    {
        using var document = JsonDocument.Parse(body);

        return document.RootElement.TryGetProperty("answers"u8, out var answers)
            && answers.EnumerateObject().Any(answer => answer.Value.ValueKind == JsonValueKind.Object
                && answer.Value.EnumerateObject().Any(member => member.Name.StartsWith('$')));
    }

    /// <summary>What the client does with a successful response's body: check the UTF-8, then read.</summary>
    private static DecodedResponse Pipeline(byte[] body, JevDecisionPlan plan)
        => Utf8.IsValid(body)
            ? SystemOneResponseReader.Read(body, plan, ResponseFuzzer.Tolerance)
            : throw new JevProtocolException(JevOperation.Evaluate, JevProtocolError.InvalidUtf8);

    private static Outcome Run(FuzzCase fuzzCase, string who, Func<DecodedResponse> decode)
    {
        var stopwatch = Stopwatch.StartNew();
        Outcome outcome;

        try
        {
            outcome = new Outcome(decode(), null);
        }
        catch (JevProtocolException ex)
        {
            outcome = new Outcome(null, ex.Error);
        }
        catch (Exception ex) when (ex is not XunitException)
        {
            throw Failure(fuzzCase, $"{who} threw {ex.GetType().FullName} instead of refusing the body: {ex.Message}");
        }

        return stopwatch.Elapsed <= SlowDecode
            ? outcome
            : throw Failure(fuzzCase, $"{who} took {stopwatch.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} s to decide.");
    }

    /// <summary>
    /// Compares two decodes of a body. Numbers must match to the bit — both decoders parse the
    /// same digits, and a respelled number is the same decimal value. Legend values must match
    /// byte for byte when the body is the same, and as JSON values when it was only respelled.
    /// </summary>
    private static void Compare(FuzzCase fuzzCase, string what, DecodedResponse expected, DecodedResponse actual, bool exact)
    {
        string? difference = null;

        if (!string.Equals(expected.Model, actual.Model, StringComparison.Ordinal))
        {
            difference = "model";
        }
        else if (expected.Usage != actual.Usage)
        {
            difference = "usage";
        }
        else if (expected.Probabilities.Length != actual.Probabilities.Length || !SameBits(expected.Probabilities, actual.Probabilities))
        {
            difference = "probabilities";
        }
        else
        {
            for (var i = 0; i < expected.Slots.Length && difference is null; i++)
            {
                var (e, a) = (expected.Slots[i], actual.Slots[i]);

                if (e.Selected != a.Selected || !SameBits(e.Value, a.Value) || !SameBits(e.Confidence, a.Confidence))
                {
                    difference = "the answer in slot " + i.ToString(CultureInfo.InvariantCulture);
                }
                else if (!SameLegend(expected.Legends?[i] ?? default, actual.Legends?[i] ?? default, exact))
                {
                    difference = "the legend in slot " + i.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        if (difference is not null)
        {
            throw Failure(fuzzCase, $"{what}: {difference} differs.");
        }
    }

    private static bool SameBits(double expected, double actual) => BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual);

    private static bool SameBits(double[] expected, double[] actual)
    {
        for (var i = 0; i < expected.Length; i++)
        {
            if (!SameBits(expected[i], actual[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameLegend(System.Collections.Immutable.ImmutableArray<JsonElement> expected, System.Collections.Immutable.ImmutableArray<JsonElement> actual, bool exact)
    {
        if (expected.IsDefault || actual.IsDefault)
        {
            return expected.IsDefault == actual.IsDefault;
        }

        if (expected.Length != actual.Length)
        {
            return false;
        }

        for (var i = 0; i < expected.Length; i++)
        {
            var same = exact
                ? expected[i].GetRawText() == actual[i].GetRawText()
                : JsonElement.DeepEquals(expected[i], actual[i]);

            if (!same)
            {
                return false;
            }
        }

        return true;
    }

    private static int? Setting(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)
            : null;

    /// <summary>A failure that says everything needed to reproduce and read the case.</summary>
    private static FailException Failure(FuzzCase fuzzCase, string what)
    {
        var seed = fuzzCase.Seed.ToString(CultureInfo.InvariantCulture);
        var text = new StringBuilder()
            .AppendLine(what)
            .AppendLine($"Seed {seed}, iteration {fuzzCase.Iteration.ToString(CultureInfo.InvariantCulture)}: reproduce with JEV_DOTNET_FUZZ_SEED={seed} and JEV_DOTNET_FUZZ_ITERATIONS={(fuzzCase.Iteration + 1).ToString(CultureInfo.InvariantCulture)}.")
            .AppendLine($"Expected: {fuzzCase.Expectation}.")
            .AppendLine("Changes: " + (fuzzCase.Changes.Count == 0 ? "none" : string.Join("; ", fuzzCase.Changes.Select(Visible))))
            .AppendLine("Plan: " + string.Join(", ", fuzzCase.Plan.Questions.Select(Describe)))
            .AppendLine("Canonical: " + Visible(fuzzCase.Canonical))
            .Append("Body: " + Visible(fuzzCase.Body));

        return FailException.ForFailure(text.ToString());
    }

    private static string Describe(QuestionDefinition question) => question switch
    {
        ChoiceQuestionDefinition choice => $"choice \"{Visible(question.Id)}\" [{string.Join(", ", choice.Labels.Take(12).Select(l => "\"" + Visible(l) + "\""))}{(choice.Labels.Length > 12 ? $", ... {choice.Labels.Length.ToString(CultureInfo.InvariantCulture)} labels" : string.Empty)}]",
        ScoreQuestionDefinition score => $"score \"{Visible(question.Id)}\" ({score.Levels.Length.ToString(CultureInfo.InvariantCulture)} levels)",
        _ => $"noul \"{Visible(question.Id)}\"",
    };

    /// <summary>Text with control, invisible and unpaired characters made visible, and cut short when long.</summary>
    private static string Visible(string text)
    {
        var visible = new StringBuilder();

        foreach (var c in text.Length > 6000 ? text[..6000] : text)
        {
            if (char.IsControl(c) || char.IsSurrogate(c) || c is (char)0x2028 or (char)0x2029 or (char)0xFEFF or (char)0x200B or (char)0x00A0)
            {
                visible.Append("<U+").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture)).Append('>');
            }
            else
            {
                visible.Append(c);
            }
        }

        return text.Length > 6000 ? visible.Append(" ... (" + text.Length.ToString(CultureInfo.InvariantCulture) + " characters)").ToString() : visible.ToString();
    }

    private readonly record struct Outcome(DecodedResponse? Decoded, JevProtocolError? Error)
    {
        public bool Accepted => Decoded is not null;

        public override string ToString() => Accepted ? "accepted it" : $"refused it ({Error})";
    }
}
