using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kkdev92.Jev.IntegrationTests;

/// <summary>
/// Where the live tests write down what they observed: the test output always, and a Markdown file
/// when <c>JEV_DOTNET_LIVE_REPORT</c> names one — the file the workflows publish with each run.
/// </summary>
/// <remarks>
/// <para>
/// Several of the questions these tests exist to answer have no pass or fail: how precise the
/// probabilities are, how far they stray from the relationships the contract describes, what a
/// legend echoes back. A test that only asserted would throw that away the moment it passed. So
/// each test reports what it saw, and asserts only what the contract actually promises.
/// </para>
/// <para>
/// The report holds what the service did, never what could identify a caller: no key, no headers,
/// no request or response body, no request id. Numbers are copied exactly as the service spelled
/// them, because their spelling is one of the things being measured.
/// </para>
/// </remarks>
internal static partial class LiveReport
{
    public const string PathVariable = "JEV_DOTNET_LIVE_REPORT";

    private static readonly Lock Gate = new();

    /// <summary>Writes one section: a heading and a list of observations.</summary>
    public static void Write(string title, IEnumerable<string> observations)
    {
        var section = new StringBuilder().Append("### ").AppendLine(title).AppendLine();

        foreach (var observation in observations)
        {
            section.Append("- ").AppendLine(observation);
        }

        section.AppendLine();
        TestContext.Current.TestOutputHelper?.WriteLine(section.ToString());

        if (Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } path)
        {
            // Test classes run in parallel; a section is appended whole or not at all.
            lock (Gate)
            {
                File.AppendAllText(path, section.ToString());
            }
        }
    }

    /// <summary>True for a request id of the shape the service has sent so far: <c>req_</c> and 32 lowercase hex digits.</summary>
    public static bool IsRequestId(string? value) => value is not null && RequestId().IsMatch(value);

    /// <summary>What a successful evaluation says about the service, beyond the answers themselves.</summary>
    /// <param name="result">The result, with its raw body captured.</param>
    /// <param name="levelsSent">The levels each score question sent, by question id, to compare with the legend that comes back.</param>
    public static List<string> Describe(JevResult result, IReadOnlyDictionary<string, JevContent[]> levelsSent)
    {
        var metadata = result.Metadata;
        var lines = new List<string>
        {
            Invariant($"Model: requested `{result.RequestedModel}`, answered by `{result.ActualModel}`"),
            Invariant($"HTTP/{metadata.HttpVersion}, {metadata.Attempts} attempt(s), {metadata.Duration.TotalMilliseconds:F0} ms"),
            "Request id: " + (IsRequestId(metadata.RequestId) ? "`req_` and 32 hex digits" : "**not of the expected shape**"),
            Invariant($"Usage: {result.Usage.InputTokens} input tokens, {result.Usage.OutputTokens} output tokens"),
            "SDK consistency warnings at the default tolerance: " + (metadata.Warnings.Count == 0
                ? "none"
                : string.Join("; ", metadata.Warnings.Select(w => Invariant($"`{w.QuestionId}` {w.Check} {w.Deviation:R}")))),
        };

        // Read the numbers again from the body itself: a second reading of the same response,
        // independent of the SDK's, and the only one that sees how each number was spelled.
        using var document = JsonDocument.Parse(result.RawResponseBody ?? throw new InvalidOperationException("The raw response body was not captured."));
        var numbers = new List<string>();

        foreach (var answer in document.RootElement.GetProperty("answers").EnumerateObject())
        {
            lines.Add(DescribeAnswer(answer.Name, answer.Value, levelsSent, numbers));
        }

        var fractionDigits = numbers.Count == 0 ? 0 : numbers.Max(FractionDigits);
        lines.Add(Invariant($"Numbers in the answers: {numbers.Count}; most digits after the point: {fractionDigits}; exponent notation: {(numbers.Any(n => n.Contains('e', StringComparison.OrdinalIgnoreCase)) ? "yes" : "no")}"));

        return lines;
    }

    /// <summary>
    /// The shape of a body — each field's name and the JSON kind of its value, a few levels deep —
    /// with no value in it but an answer's <c>type</c>.
    /// </summary>
    /// <remarks>
    /// For a response the SDK refused: the kind of refusal says what rule broke, and this says
    /// where — a count that came back <c>null</c>, a field that is missing or renamed — without
    /// copying anything the service wrote.
    /// </remarks>
    public static string Shape(byte[]? body)
    {
        if (body is null)
        {
            return "(no body recorded)";
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var text = new StringBuilder();
            Shape(document.RootElement, text, depth: 0);
            return "`" + text + "`";
        }
        catch (JsonException)
        {
            return Invariant($"not JSON ({body.Length} bytes)");
        }
    }

    /// <summary>What a refusal said: status and kind, never the service's message.</summary>
    public static List<string> Describe(JevHttpException exception) =>
    [
        Invariant($"HTTP {exception.StatusCode}, error type `{exception.ErrorType ?? "(none)"}`, request {exception.Transmission}"),
        "Request id: " + (IsRequestId(exception.RequestId) ? "`req_` and 32 hex digits" : "**not of the expected shape**"),
        Invariant($"Retry-After: {(exception.RetryAfter is { } wait ? wait.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms" : "none")}"),
        "Validation errors: " + (exception.ValidationErrors.Count == 0
            ? "none"
            : string.Join("; ", exception.ValidationErrors.Select(e => $"`{e.Type}` at `{string.Join(".", e.Location)}`"))),
    ];

    private static string DescribeAnswer(string id, JsonElement answer, IReadOnlyDictionary<string, JevContent[]> levelsSent, List<string> numbers)
        => answer.GetProperty("type").GetString() switch
        {
            "noul" => $"`{id}` (noul): {Spelled(answer.GetProperty("noul"), numbers)}",
            "choice" => DescribeChoice(id, answer, numbers),
            "score" => DescribeScore(id, answer, levelsSent.GetValueOrDefault(id), numbers),
            _ => $"`{id}`: an answer of a type this report does not know",
        };

    private static string DescribeChoice(string id, JsonElement answer, List<string> numbers)
    {
        var chosen = answer.GetProperty("choice").GetString();
        var confidence = answer.GetProperty("confidence");
        var probabilities = answer.GetProperty("probabilities").EnumerateObject().ToList();
        var spelled = string.Join(", ", probabilities.Select(p => $"`{p.Name}` {Spelled(p.Value, numbers)}"));
        var sum = probabilities.Sum(p => p.Value.GetDouble());
        var top = probabilities.Max(p => p.Value.GetDouble());
        var chosenProbability = probabilities.First(p => p.Name == chosen).Value.GetDouble();
        var gap = top - chosenProbability;
        var rank = gap == 0 ? "the most probable" : Invariant($"{gap:R} below the most probable");

        return Invariant($"`{id}` (choice): `{chosen}`, confidence {Spelled(confidence, numbers)}; {spelled}; sum - 1 = {sum - 1:R}; ")
            + Invariant($"chosen is {rank}; confidence - p(chosen) = {confidence.GetDouble() - chosenProbability:R}");
    }

    private static string DescribeScore(string id, JsonElement answer, JevContent[]? levelsSent, List<string> numbers)
    {
        var score = answer.GetProperty("score");
        var confidence = answer.GetProperty("confidence");
        var probabilities = answer.GetProperty("probabilities").EnumerateObject().OrderBy(p => int.Parse(p.Name, CultureInfo.InvariantCulture)).ToList();
        var spelled = string.Join(", ", probabilities.Select(p => $"{p.Name}: {Spelled(p.Value, numbers)}"));
        var sum = probabilities.Sum(p => p.Value.GetDouble());
        var expectation = probabilities.Sum(p => int.Parse(p.Name, CultureInfo.InvariantCulture) * p.Value.GetDouble());

        return Invariant($"`{id}` (score): {Spelled(score, numbers)}, confidence {Spelled(confidence, numbers)}; {spelled}; sum - 1 = {sum - 1:R}; ")
            + Invariant($"score - expectation = {score.GetDouble() - expectation:R}; legend {DescribeLegend(answer.GetProperty("legend"), levelsSent)}");
    }

    /// <summary>A number exactly as the service spelled it, kept for the precision summary.</summary>
    private static string Spelled(JsonElement number, List<string> numbers)
    {
        var text = number.GetRawText();
        numbers.Add(text);
        return text;
    }

    /// <summary>Whether the legend came back as the levels were sent: same JSON kinds, same values.</summary>
    private static string DescribeLegend(JsonElement legend, JevContent[]? sent)
    {
        var entries = legend.EnumerateObject().OrderBy(e => int.Parse(e.Name, CultureInfo.InvariantCulture)).ToList();
        var kinds = string.Join(", ", entries.Select(e => e.Value.ValueKind.ToString().ToLowerInvariant()));

        if (sent is null)
        {
            return $"({kinds})";
        }

        var echoed = entries.Count == sent.Length
            && entries.Select((e, i) => JsonElement.DeepEquals(e.Value, sent[i].ToJsonElement())).All(same => same);

        return $"({kinds}) {(echoed ? "echoes the levels exactly" : "**differs from the levels sent**")}";
    }

    private static void Shape(JsonElement value, StringBuilder text, int depth)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object when depth < 4:
                text.Append('{');
                var first = true;

                foreach (var member in value.EnumerateObject())
                {
                    text.Append(first ? string.Empty : ", ").Append(member.Name).Append(": ");
                    first = false;

                    if (member.Name == "type" && member.Value.ValueKind == JsonValueKind.String)
                    {
                        text.Append('"').Append(member.Value.GetString()).Append('"');
                    }
                    else
                    {
                        Shape(member.Value, text, depth + 1);
                    }
                }

                text.Append('}');
                break;

            case JsonValueKind.Array:
                text.Append(Invariant($"array({value.GetArrayLength()})"));
                break;

            default:
                text.Append(value.ValueKind.ToString().ToLowerInvariant());
                break;
        }
    }

    /// <summary>Digits after the decimal point, as spelled: <c>0.880</c> has three, <c>1e-05</c> none.</summary>
    private static int FractionDigits(string number)
    {
        var point = number.IndexOf('.', StringComparison.Ordinal);

        if (point < 0)
        {
            return 0;
        }

        var end = number.IndexOfAny(['e', 'E'], point);
        return (end < 0 ? number.Length : end) - point - 1;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex("^req_[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex RequestId();
}
