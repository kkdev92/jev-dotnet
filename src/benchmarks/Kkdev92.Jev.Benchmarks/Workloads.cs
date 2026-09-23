using System.Globalization;
using System.Text;
using Kkdev92.Jev.TestSupport;

namespace Kkdev92.Jev.Benchmarks;

/// <summary>
/// The representative set: the questions of a plan, and a response that answers them consistently.
/// </summary>
/// <remarks>
/// <para>
/// Fixed in advance, as the adoption gate in <c>BASELINE.md</c> requires: a workload is never
/// chosen after the measurement because it is the one that wins. They span the axes that change
/// the work: the number of answers, the number of probabilities per answer, and the mix of kinds.
/// </para>
/// <para>
/// Every response is consistent — probabilities sum to one, the choice is the most probable label,
/// the score is the expectation over its levels — so every path measured is the success path and no
/// consistency warning is allocated. A benchmark of the failure path would be measuring exception
/// construction.
/// </para>
/// <para>
/// The questions are prepared once: labels, options and levels are ordinary caller data, and
/// formatting them is not the SDK's cost. What <see cref="Workload.BuildPlan"/> measures is the
/// builder's validation and encoding and nothing else.
/// </para>
/// </remarks>
internal static class Workloads
{
    public const string Triage = "triage";
    public const string Wide = "wide";
    public const string MaxChoice = "max-choice";
    public const string ManyNoul = "many-noul";

    public static readonly string[] All = [Triage, Wide, MaxChoice, ManyNoul];

    public static Workload Get(string name) => name switch
    {
        // The shape of the API reference's examples: three questions, one of each kind.
        Triage => new([Choice("department", 3), Score("frustration", 3), Noul("urgent")]),

        // A form-filling plan: thirty-two questions of every kind.
        Wide => new(
        [
            .. Enumerable.Range(0, 16).Select(i => Choice($"choice_{i:00}", 8)),
            .. Enumerable.Range(0, 8).Select(i => Score($"score_{i:00}", 5)),
            .. Enumerable.Range(0, 8).Select(i => Noul($"noul_{i:00}")),
        ]),

        // The documented maximum of choice options, which is the most probabilities one answer carries.
        MaxChoice => new([Choice("category", 255)]),

        // Many small answers: per-answer overhead with almost no per-answer work.
        ManyNoul => new([.. Enumerable.Range(0, 128).Select(i => Noul($"flag_{i:000}"))]),

        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a workload."),
    };

    /// <summary>State text of at most the given UTF-8 size, in one of two scripts.</summary>
    public static string State(int utf8Bytes, bool japanese)
    {
        const string ascii = "The customer says the invoice was charged twice and asks for a refund before Friday. ";
        const string kana = "請求が二重に発生しているため、金曜日までに返金してほしいとのことです。";

        var unit = japanese ? kana : ascii;
        var unitBytes = Encoding.UTF8.GetByteCount(unit);
        var builder = new StringBuilder(utf8Bytes);

        for (var written = unitBytes; written <= utf8Bytes; written += unitBytes)
        {
            builder.Append(unit);
        }

        return builder.ToString();
    }

    private static Question Choice(string id, int count)
    {
        var labels = Enumerable.Range(0, count).Select(i => $"option_{i.ToString("000", CultureInfo.InvariantCulture)}").ToArray();
        var options = labels.Select((label, i) => new JevChoiceOption<int>(i, label, $"Description of {label}")).ToArray();

        // The first option is the most probable, and the rest share what is left.
        var first = count == 1 ? 1.0 : 0.5;
        var rest = count == 1 ? 0.0 : 0.5 / (count - 1);
        var probabilities = labels.Select((label, i) => (label, i == 0 ? first : rest)).ToArray();

        return new(
            builder => builder.AddChoice<int>(id, "Which option fits best?", options),
            response => response.Choice(id, labels[0], first, probabilities));
    }

    private static Question Score(string id, int count)
    {
        var legend = Enumerable.Range(0, count).Select(i => $"Level {i.ToString(CultureInfo.InvariantCulture)}").ToArray();
        var levels = legend.Select(l => (JevContent)l).ToArray();

        // Uniform, so the expectation is the midpoint and the sum is one.
        var probabilities = Enumerable.Repeat(1.0 / count, count).ToArray();
        var expectation = probabilities.Select((p, i) => p * i).Sum();

        return new(
            builder => builder.AddScore(id, "How strongly does this apply?", levels),
            response => response.Score(id, expectation, 0.5, legend, probabilities));
    }

    private static Question Noul(string id) => new(
        builder => builder.AddNoul(id, "Does this apply?"),
        response => response.Noul(id, 0.75));
}

/// <summary>One question: how it is added to a plan, and how a response answers it.</summary>
internal sealed record Question(Action<JevDecisionPlanBuilder> Add, Action<SystemOneResponseBuilder> Answer);

/// <summary>A plan's questions, prepared.</summary>
internal sealed class Workload(Question[] questions)
{
    public JevDecisionPlan BuildPlan()
    {
        var builder = new JevDecisionPlanBuilder();

        foreach (var question in questions)
        {
            question.Add(builder);
        }

        return builder.Build();
    }

    public byte[] BuildResponse()
    {
        var response = new SystemOneResponseBuilder();

        foreach (var question in questions)
        {
            question.Answer(response);
        }

        return Encoding.UTF8.GetBytes(response.Build());
    }
}
