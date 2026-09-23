using System.Globalization;
using System.Text;
using Kkdev92.Jev.Planning;

namespace Kkdev92.Jev.Tests.Fuzzing;

/// <summary>What the fuzzer knows about the outcome of a case before either decoder sees it.</summary>
internal enum FuzzExpectation
{
    /// <summary>Only changes JSON says change nothing: both accept, and read exactly what the canonical body says.</summary>
    Unchanged,

    /// <summary>A change the contract forbids: both refuse.</summary>
    Refused,

    /// <summary>A change whose outcome is not predicted here: the decoders must agree on it.</summary>
    Agreed,
}

/// <summary>One generated response, the plan it answers, and how it was made.</summary>
internal sealed record FuzzCase(
    int Seed,
    int Iteration,
    JevDecisionPlan Plan,
    string Canonical,
    string Body,
    IReadOnlyList<string> Changes,
    FuzzExpectation Expectation);

/// <summary>
/// Generates a random plan, a valid response to it, and a variant of that response.
/// </summary>
/// <remarks>
/// <para>
/// Every case is a pure function of its seed and iteration: the same pair always yields the same
/// plan, body and changes, on any machine, so a failure is reproduced by its two numbers alone.
/// </para>
/// <para>
/// A variant carries some changes that must not matter — members reordered, names and strings
/// spelled with escapes, numbers spelled differently, unknown fields added, whitespace — and at
/// most one change that does: either a fault the contract forbids, or an edit whose outcome the
/// fuzzer leaves to the decoders to agree on. Never two faults, so one cannot undo another.
/// </para>
/// </remarks>
internal static class ResponseFuzzer
{
    public const double Tolerance = 1e-6;

    private static readonly char Backslash = (char)92;

    /// <summary>Ids and labels that have broken parsers before, or plausibly could.</summary>
    private static readonly string[] Tricky =
    [
        "yes", "Yes", "YES", "no", "No", "type", "Type", "model", "answers", "usage", "noul", "choice",
        "score", "legend", "probabilities", "confidence", "input_tokens", "output_tokens",
        "$type", "$id", "$ref", "$values", "0", "00", "1", "-1", "01", "true", "null",
        "部署", "請求", "技術😀", "😀", "e" + Units(0x0301), Units(0x00E9), "a b", " a", "a ", "tab\there", "line\nbreak",
        "quote\"d", "back\\slash", "\\", "\"", "/", Units(0x0000), Units(0x001F), Units(0x007F), Units(0x2028), Units(0x2029),
        Units(0xFEFF), "zero" + Units(0x200B) + "width", "ｙｅｓ", "İ", "i", "ı", "ß", "SS", Units(0xFFFF), char.ConvertFromUtf32(0x10FFFF),
    ];

    /// <summary>Names a contract object does not define, or that look like names it does.</summary>
    private static readonly string[] UnknownNames =
    [
        "extra", "explanation", "reasoning", "Type", "TYPE", "types", "type ", " type", "$type", "$id",
        "$ref", "$values", "$schema", "noul", "choice", "score", "legend", "probabilities", "confidence",
        "model", "answers", "usage", "input_tokens", "cached_tokens", "Model", "😀", string.Empty,
    ];

    private static readonly Mutation[] Preserving =
    [
        new("reorder members", ReorderMembers),
        new("escape a name", EscapeName),
        new("escape a string", EscapeString),
        new("add an unknown field", AddUnknownField),
        new("respell a number", RespellNumber),
    ];

    private static readonly Mutation[] Faults =
    [
        new("remove a required field", RemoveRequiredField),
        new("remove a map entry", RemoveMapEntry),
        new("repeat a known field", RepeatKnownField),
        new("empty the answers", EmptyAnswers),
        new("give a value the wrong kind", WrongKind),
        new("put a number out of range", OutOfRange),
        new("overflow a number", Overflow),
        new("break a token count", BreakTokenCount),
        new("break a number's syntax", BreakNumberSyntax),
        new("answer an unasked question", AnswerUnaskedQuestion),
        new("use an unknown label", UseUnknownLabel),
        new("use a bad level key", UseBadLevelKey),
        new("change an answer's type", ChangeAnswerType),
        new("add a comment", AddComment),
        new("add a trailing comma", AddTrailingComma),
        new("break an escape", BreakEscape),
    ];

    private static readonly TextMutation[] TextFaults =
    [
        new("truncate the body", (text, random) => text[..random.Next(0, text.LastIndexOf('}'))]),
        new("append garbage", (text, random) => text + Pick(random, ["x", "{}", ",", "null", "]", "}", Units(0x00A0), Units(0x3000), "\u0000", "/", "//"])),
        new("send another JSON value", (_, random) => Pick(random, ["null", "[]", "1", "\"x\"", "true", "{}"])),
        new("wrap the body in an array", (text, _) => "[" + text + "]"),
    ];

    private static readonly Mutation[] Unpredicted =
    [
        new("nest deeply", NestDeeply),
        new("put in a lone surrogate", LoneSurrogate),
        new("repeat an unknown field", RepeatUnknownField),
        new("change a number", ChangeNumber),
        new("change the model", ChangeModel),
        new("change a legend value", ChangeLegendValue),
    ];

    private static readonly TextMutation[] UnpredictedText =
    [
        new("insert a character", (text, random) => text.Insert(random.Next(text.Length + 1), ((char)random.Next(0x20, 0x7F)).ToString())),
        new("delete a character", (text, random) => text.Remove(random.Next(text.Length), 1)),
        new("prefix a byte order mark", (text, _) => Units(0xFEFF) + text),
    ];

    /// <summary>A field the contract does not define, named with a leading dollar, in an answer.</summary>
    private static readonly Mutation DollarField = new("add a dollar-named field to an answer", AddDollarField);

    /// <summary>Whitespace before and after the root value, which JSON ignores.</summary>
    private static readonly TextMutation Surround =
        new("surround with whitespace", (text, random) => Pick(random, [" ", "\t", "\r\n", "\n\n"]) + text + Pick(random, [" ", "\t", "\r\n", string.Empty]));

    private delegate string? Apply(FuzzObject root, Random random, JevDecisionPlan plan);

    /// <summary>The names of every change this fuzzer can make, for coverage checks.</summary>
    public static IEnumerable<string> ChangeNames
        => Preserving.Select(m => m.Name)
            .Concat(Faults.Select(m => m.Name))
            .Concat(TextFaults.Select(m => m.Name))
            .Concat(Unpredicted.Select(m => m.Name))
            .Concat(UnpredictedText.Select(m => m.Name))
            .Append(DollarField.Name)
            .Append(Surround.Name)
            .Append("vary the layout");

    public static FuzzCase Create(int seed, int iteration)
    {
        var random = new Random(Mix(seed, iteration));
        var plan = NewPlan(random);
        var root = NewResponse(plan, random);
        var canonical = FuzzJsonWriter.Write(root, random, varyLayout: false);

        var changes = new List<string>();
        var expectation = FuzzExpectation.Unchanged;
        var variant = (FuzzObject)root.Clone();

        for (var i = random.Next(0, 4); i > 0; i--)
        {
            Mutate(Pick(random, Preserving), variant, random, plan, changes);
        }

        // One case in thirty puts a field named with a dollar in an answer, and nothing else that
        // matters: the one input the reference decoder is known to refuse wrongly, so on it the
        // client is judged alone, against the canonical body — and nowhere else does the
        // reference's verdict go unchecked.
        var dollar = random.Next(30) == 0;
        var roll = dollar ? -1 : random.Next(10);

        if (dollar)
        {
            Mutate(DollarField, variant, random, plan, changes);
        }
        else if (roll is >= 0 and < 5)
        {
            if (Mutate(Pick(random, Faults), variant, random, plan, changes))
            {
                expectation = FuzzExpectation.Refused;
            }
        }
        else if (roll < 7)
        {
            if (Mutate(Pick(random, Unpredicted), variant, random, plan, changes))
            {
                expectation = FuzzExpectation.Agreed;
            }
        }

        var varyLayout = random.Next(2) == 0;

        if (varyLayout)
        {
            changes.Add("vary the layout");
        }

        var body = FuzzJsonWriter.Write(variant, random, varyLayout);

        if (expectation == FuzzExpectation.Unchanged && !dollar)
        {
            roll = random.Next(10);

            if (roll == 0)
            {
                var fault = Pick(random, TextFaults);
                body = fault.Change(body, random);
                changes.Add(fault.Name);
                expectation = FuzzExpectation.Refused;
            }
            else if (roll == 1)
            {
                var edit = Pick(random, UnpredictedText);
                body = edit.Change(body, random);
                changes.Add(edit.Name);
                expectation = FuzzExpectation.Agreed;
            }
            else if (roll == 2)
            {
                body = Surround.Change(body, random);
                changes.Add(Surround.Name);
            }
        }

        return new FuzzCase(seed, iteration, plan, canonical, body, changes, expectation);
    }

    private static bool Mutate(Mutation mutation, FuzzObject root, Random random, JevDecisionPlan plan, List<string> changes)
    {
        var detail = mutation.Change(root, random, plan);

        if (detail is null)
        {
            return false;
        }

        changes.Add(detail.Length == 0 ? mutation.Name : mutation.Name + ": " + detail);
        return true;
    }

    /// <summary>A seed for one case, mixed so that neighbouring cases share nothing.</summary>
    private static int Mix(int seed, int iteration)
    {
        // SplitMix64's finaliser over the pair.
        var z = ((ulong)(uint)seed << 32) | (uint)iteration;
        z += 0x9E3779B97F4A7C15;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        z ^= z >> 31;
        return (int)(z & int.MaxValue);
    }

    #region Plans and responses

    private static JevDecisionPlan NewPlan(Random random)
    {
        var builder = new JevDecisionPlanBuilder();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var count = random.Next(10) == 0 ? random.Next(5, 25) : random.Next(1, 5);

        for (var i = 0; i < count; i++)
        {
            var id = UniqueText(random, ids, allowLong: true);

            switch (random.Next(3))
            {
                case 0:
                    builder.AddNoul(id, "q");
                    break;

                case 1:
                    var many = random.Next(20) == 0;
                    var labels = new HashSet<string>(StringComparer.Ordinal);
                    var options = new JevChoiceOption<int>[many ? random.Next(20, 256) : random.Next(1, 7)];

                    for (var o = 0; o < options.Length; o++)
                    {
                        // Short labels when there are many, so the plan stays well inside its size limit.
                        options[o] = new JevChoiceOption<int>(o, many ? UniqueWord(random, labels) : UniqueText(random, labels, allowLong: true));
                    }

                    builder.AddChoice<int>(id, "q", options);
                    break;

                default:
                    var levels = random.Next(JevDecisionPlanBuilder.MinScoreLevels, JevDecisionPlanBuilder.MaxScoreLevels + 1);
                    builder.AddScore(id, "q", [.. Enumerable.Range(0, levels).Select(l => (JevContent)("level " + l.ToString(CultureInfo.InvariantCulture)))]);
                    break;
            }
        }

        return builder.Build();
    }

    private static FuzzObject NewResponse(JevDecisionPlan plan, Random random)
    {
        var answers = new FuzzObject(FuzzRole.Answers);

        foreach (var question in plan.Questions.OrderBy(_ => random.Next()))
        {
            answers.Add(question.Id, NewAnswer(question, random));
        }

        var usage = new FuzzObject(FuzzRole.Usage)
            .Add("input_tokens", FuzzRaw.Of(Count(random)))
            .Add("output_tokens", FuzzRaw.Of(Count(random)));

        return new FuzzObject(FuzzRole.Root)
            .Add("model", new FuzzString(Pick(random, ["jev-1.13.0", "jev-latest", string.Empty, "jev\u0000", Text(random)])))
            .Add("answers", answers)
            .Add("usage", usage);
    }

    private static FuzzObject NewAnswer(QuestionDefinition question, Random random)
    {
        var answer = new FuzzObject(FuzzRole.Answer) { Question = question };
        answer.Add("type", new FuzzString(question.TypeTag));

        switch (question)
        {
            case NoulQuestionDefinition:
                answer.Add("noul", FuzzRaw.Of(Number(random, 1)));
                break;

            case ChoiceQuestionDefinition choice:
                var probabilities = new FuzzObject(FuzzRole.ChoiceProbabilities) { Question = question };

                foreach (var label in choice.Labels)
                {
                    probabilities.Add(label, FuzzRaw.Of(Number(random, 1)));
                }

                answer
                    .Add("choice", new FuzzString(Pick(random, choice.Labels)))
                    .Add("confidence", FuzzRaw.Of(Number(random, 1)))
                    .Add("probabilities", probabilities);
                break;

            case ScoreQuestionDefinition score:
                var levels = score.Levels.Length;
                var legend = new FuzzObject(FuzzRole.Legend) { Question = question };
                var distribution = new FuzzObject(FuzzRole.LevelProbabilities) { Question = question };

                for (var level = 0; level < levels; level++)
                {
                    legend.Add(Key(level), LegendValue(random));
                    distribution.Add(Key(level), FuzzRaw.Of(Number(random, 1)));
                }

                answer
                    .Add("score", FuzzRaw.Of(Number(random, levels - 1)))
                    .Add("confidence", FuzzRaw.Of(Number(random, 1)))
                    .Add("legend", legend)
                    .Add("probabilities", distribution);
                break;
        }

        return answer;
    }

    /// <summary>A number the contract accepts where the maximum is <paramref name="maximum"/>, edges included.</summary>
    private static double Number(Random random, double maximum) => random.Next(12) switch
    {
        0 => 0,
        1 => maximum,
        2 => -0.0,
        3 => maximum + (Tolerance / 2),
        4 => -Tolerance / 2,
        5 => double.Epsilon,
        6 => 1e-300,
        7 => Math.Round(random.NextDouble() * maximum, 2),
        _ => random.NextDouble() * maximum,
    };

    private static long Count(Random random) => random.Next(6) switch
    {
        0 => 0,
        1 => long.MaxValue,
        2 => (1L << 53) + 1,
        3 => random.NextInt64(),
        _ => random.Next(0, 5000),
    };

    private static string Key(int level) => level.ToString(CultureInfo.InvariantCulture);

    private static FuzzNode LegendValue(Random random) => random.Next(3) switch
    {
        0 => new FuzzString(Text(random)),
        1 => FreeObject(random, 2),
        _ => FreeArray(random, 2),
    };

    /// <summary>Any JSON value, with no duplicate names, nested at most <paramref name="depth"/> deep.</summary>
    private static FuzzNode Free(Random random, int depth) => random.Next(depth > 0 ? 7 : 5) switch
    {
        0 => new FuzzString(Text(random)),
        1 => FuzzRaw.Of(Math.Round(random.NextDouble(), random.Next(0, 16)) * Math.Pow(10, random.Next(-8, 9))),
        2 => new FuzzRaw("true"),
        3 => new FuzzRaw("false"),
        4 => new FuzzRaw("null"),
        5 => FreeObject(random, depth - 1),
        _ => FreeArray(random, depth - 1),
    };

    private static FuzzObject FreeObject(Random random, int depth)
    {
        var obj = new FuzzObject();
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (var i = random.Next(0, 4); i > 0; i--)
        {
            obj.Add(UniqueText(random, names, allowLong: false), Free(random, depth));
        }

        return obj;
    }

    private static FuzzArray FreeArray(Random random, int depth)
    {
        var array = new FuzzArray();

        for (var i = random.Next(0, 4); i > 0; i--)
        {
            array.Items.Add(Free(random, depth));
        }

        return array;
    }

    private static string UniqueText(Random random, HashSet<string> used, bool allowLong)
    {
        while (true)
        {
            var text = Text(random, allowLong);

            if (text.Length > 0 && used.Add(text))
            {
                return text;
            }
        }
    }

    private static string UniqueWord(Random random, HashSet<string> used)
    {
        while (true)
        {
            var text = Word(random, random.Next(1, 8));

            if (used.Add(text))
            {
                return text;
            }
        }
    }

    /// <summary>Text for an id, a label, a string: tricky, short, or — sometimes — past the reader's stack buffer.</summary>
    private static string Text(Random random, bool allowLong = false) => random.Next(3) switch
    {
        0 => Pick(random, Tricky),
        1 => Word(random, random.Next(1, 12)),
        _ => allowLong && random.Next(6) == 0 ? Word(random, random.Next(200, 320)) : Word(random, random.Next(1, 40)),
    };

    /// <summary>Well-formed UTF-16 from every width UTF-8 has, control characters, quotes and backslashes included.</summary>
    private static string Word(Random random, int length)
    {
        var text = new StringBuilder(length + 1);

        while (text.Length < length)
        {
            switch (random.Next(10))
            {
                case < 5:
                    text.Append((char)random.Next('a', 'z' + 1));
                    break;

                case 5:
                    text.Append((char)random.Next(0x20, 0x7F));
                    break;

                case 6:
                    text.Append((char)random.Next(0x4E00, 0xA000));
                    break;

                case 7:
                    text.Append(char.ConvertFromUtf32(random.Next(0x1F300, 0x1FB00)));
                    break;

                case 8:
                    text.Append((char)random.Next(0, 0x20));
                    break;

                default:
                    text.Append((char)random.Next(0xA0, 0x800));
                    break;
            }
        }

        return text.ToString();
    }

    #endregion

    #region Changes that must not matter

    private static string? ReorderMembers(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var objects = Nodes(root).OfType<FuzzObject>().Where(o => o.Members.Count > 1).ToList();

        if (objects.Count == 0)
        {
            return null;
        }

        var target = Pick(random, objects);
        Shuffle(target.Members, random);
        return target.Role.ToString();
    }

    private static string? EscapeName(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var members = Members(root).ToList();

        if (members.Count == 0)
        {
            return null;
        }

        var (owner, index) = Pick(random, members);
        owner.Members[index] = owner.Members[index] with { EscapeName = true };
        return owner.Role.ToString();
    }

    private static string? EscapeString(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var strings = Nodes(root).OfType<FuzzString>().ToList();

        if (strings.Count == 0)
        {
            return null;
        }

        Pick(random, strings).Escape = true;
        return string.Empty;
    }

    private static string? AddUnknownField(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var target = Pick(random, ContractObjects(root));
        var name = UnknownName(target, random);

        target.Members.Insert(random.Next(target.Members.Count + 1), new FuzzMember(name, Free(random, 3)));
        return target.Role + " gets \"" + name + "\"";
    }

    private static string? AddDollarField(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var answer = Pick(random, Nodes(root).OfType<FuzzObject>().Where(o => o.Role == FuzzRole.Answer).ToList());
        var name = Pick(random, ["$id", "$ref", "$values", "$type", "$schema", "$", "$extra"]);

        answer.Members.Insert(random.Next(answer.Members.Count + 1), new FuzzMember(name, Free(random, 2), EscapeName: random.Next(3) == 0));
        return "\"" + name + "\"";
    }

    private static string? RespellNumber(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var numbers = Slots(root).Where(s => s.Node is FuzzRaw raw && FuzzNumbers.TryParse(raw.Text, out _, out _, out _)).ToList();

        if (numbers.Count == 0)
        {
            return null;
        }

        var slot = Pick(random, numbers);
        var before = ((FuzzRaw)slot.Node).Text;
        var after = FuzzNumbers.Respell(before, random)!;

        slot.Replace(new FuzzRaw(after));
        return before + " as " + after;
    }

    #endregion

    #region Faults

    private static string? RemoveRequiredField(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var target = Pick(random, ContractObjects(root));
        var name = Pick(random, Known(target));
        var index = target.Members.FindIndex(m => m.Name == name);

        if (index < 0)
        {
            return null;
        }

        target.Members.RemoveAt(index);
        return target.Role + " loses \"" + name + "\"";
    }

    private static string? RemoveMapEntry(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var maps = Nodes(root).OfType<FuzzObject>().Where(o => o.IsMap && o.Members.Count > 0).ToList();
        var target = Pick(random, maps);

        target.Members.RemoveAt(random.Next(target.Members.Count));
        return target.Role.ToString();
    }

    private static string? RepeatKnownField(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var members = Members(root)
            .Where(m => m.Owner.IsMap || Known(m.Owner).Contains(m.Owner.Members[m.Index].Name))
            .ToList();

        var (owner, index) = Pick(random, members);
        var member = owner.Members[index];

        // The copy is spelled with escapes half the time: a duplicate is a duplicate in any spelling.
        owner.Members.Insert(random.Next(owner.Members.Count + 1), member with { Value = member.Value.Clone(), EscapeName = random.Next(2) == 0 });
        return owner.Role + " repeats \"" + member.Name + "\"";
    }

    private static string? EmptyAnswers(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        ((FuzzObject)root.Find("answers")!.Value).Members.Clear();
        return string.Empty;
    }

    private static string? WrongKind(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var slots = Slots(root).Where(s => s.Expects != Expect.Free).ToList();
        var slot = Pick(random, slots);

        string[] wrong = slot.Expects switch
        {
            Expect.Text or Expect.TypeTag or Expect.Label => ["1", "true", "false", "null", "{}", "[]", "[\"x\"]"],
            Expect.Unit or Expect.ScoreValue or Expect.Count => ["\"0.5\"", "\"1\"", "\"0\"", "null", "true", "false", "{}", "[]", "[0.5]"],
            Expect.LegendValue => ["null", "1", "0", "true", "false", "-1.5"],
            _ => ["null", "[]", "\"x\"", "1", "true", "[{}]"],
        };

        var replacement = Pick(random, wrong);
        slot.Replace(new FuzzRaw(replacement));
        return slot.Expects + " becomes " + replacement;
    }

    private static string? OutOfRange(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var slots = Slots(root).Where(s => s.Expects is Expect.Unit or Expect.ScoreValue).ToList();
        var slot = Pick(random, slots);
        var maximum = Maximum(slot);

        var value = Pick(random, [maximum + (Tolerance * 10), -Tolerance * 10, maximum + 1, -1, 1e308, -1e308, (maximum * 2) + 1]);
        slot.Replace(FuzzRaw.Of(value));
        return slot.Expects + " becomes " + value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string? Overflow(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var slots = Slots(root).Where(s => s.Expects is Expect.Unit or Expect.ScoreValue or Expect.Count).ToList();
        var slot = Pick(random, slots);
        var value = Pick(random, ["1e400", "-1e400", "1E+999", "123456789e999999"]);

        slot.Replace(new FuzzRaw(value));
        return slot.Expects + " becomes " + value;
    }

    private static string? BreakTokenCount(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var slot = Pick(random, Slots(root).Where(s => s.Expects == Expect.Count).ToList());
        var value = Pick(random, ["-1", "1.5", "0.1", "1e-1", "9223372036854775808", "1e19", "-9223372036854775809", "12.0001", "-1e0", "1E+20"]);

        slot.Replace(new FuzzRaw(value));
        return value;
    }

    private static string? BreakNumberSyntax(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var numbers = Slots(root).Where(s => s.Node is FuzzRaw raw && FuzzNumbers.TryParse(raw.Text, out _, out _, out _)).ToList();

        if (numbers.Count == 0)
        {
            return null;
        }

        var value = Pick(random, ["01", "-01", ".5", "+1", "1.", "1.e1", "1e", "1e+", "-", "--1", "NaN", "Infinity", "-Infinity", "0x1", "1_0", "0.5.5", "1e1.5", Units(0x0661)]);
        var slot = Pick(random, numbers);

        slot.Replace(new FuzzRaw(value));
        return slot.Expects + " becomes " + value;
    }

    private static string? AnswerUnaskedQuestion(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var answers = (FuzzObject)root.Find("answers")!.Value;
        string id;

        do
        {
            id = Text(random);
        }
        while (plan.SlotOf(id) >= 0);

        var answer = new FuzzObject().Add("type", new FuzzString("noul")).Add("noul", FuzzRaw.Of(0.5));
        answers.Members.Insert(random.Next(answers.Members.Count + 1), new FuzzMember(id, answer));
        return "\"" + id + "\"";
    }

    private static string? UseUnknownLabel(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var choices = Nodes(root).OfType<FuzzObject>().Where(o => o.Role == FuzzRole.Answer && o.Question is ChoiceQuestionDefinition).ToList();

        if (choices.Count == 0)
        {
            return null;
        }

        var answer = Pick(random, choices);
        var question = (ChoiceQuestionDefinition)answer.Question!;
        var label = Pick(random, question.Labels);

        var candidates = new[]
        {
            label.ToUpperInvariant(), label.ToLowerInvariant(), label + " ", " " + label, label + Units(0),
            Normalize(label, NormalizationForm.FormD), Normalize(label, NormalizationForm.FormC), label[..^1], Text(random),
        };

        var unknown = candidates.Where(c => question.IndexOf(c) < 0).ToList();

        if (unknown.Count == 0)
        {
            return null;
        }

        var replacement = Pick(random, unknown);

        if (random.Next(2) == 0)
        {
            var index = answer.Members.FindIndex(m => m.Name == "choice");
            answer.Members[index] = answer.Members[index] with { Value = new FuzzString(replacement) };
            return "choice \"" + replacement + "\"";
        }

        var probabilities = (FuzzObject)answer.Find("probabilities")!.Value;
        var entry = probabilities.Members.FindIndex(m => m.Name == label);
        probabilities.Members[entry] = probabilities.Members[entry] with { Name = replacement };
        return "probability for \"" + replacement + "\"";
    }

    private static string? UseBadLevelKey(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var maps = Nodes(root).OfType<FuzzObject>().Where(o => o.Role is FuzzRole.LevelProbabilities or FuzzRole.Legend).ToList();

        if (maps.Count == 0)
        {
            return null;
        }

        var map = Pick(random, maps);
        var levels = ((ScoreQuestionDefinition)map.Question!).Levels.Length;
        var index = random.Next(map.Members.Count);
        var key = map.Members[index].Name;

        var candidates = new[]
        {
            "00", "01", "-0", "+0", "0.0", "1e0", " 0", "0 ", string.Empty, Units(0x0660), Units(0xFF10), "10", "99", "-1", "1.0",
            Key(levels), key + "0", "0" + key, key + " ",
        };

        var bad = candidates.Where(c => !Enumerable.Range(0, levels).Select(Key).Contains(c)).ToList();
        var replacement = Pick(random, bad);

        map.Members[index] = map.Members[index] with { Name = replacement };
        return map.Role + " key \"" + key + "\" as \"" + replacement + "\"";
    }

    private static string? ChangeAnswerType(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var answer = Pick(random, Nodes(root).OfType<FuzzObject>().Where(o => o.Role == FuzzRole.Answer).ToList());
        var own = answer.Question!.TypeTag;
        var replacement = Pick(random, new[] { "noul", "choice", "score", "rank", "Noul", "NOUL", string.Empty, "noul ", " noul", "noul\u0000" }.Where(t => t != own).ToList());

        var index = answer.Members.FindIndex(m => m.Name == "type");
        answer.Members[index] = answer.Members[index] with { Value = new FuzzString(replacement) };
        return own + " as \"" + replacement + "\"";
    }

    private static string? AddComment(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var slots = Slots(root).Where(s => s.Owner is not null).ToList();
        var slot = Pick(random, slots);
        var comment = random.Next(2) == 0 ? "/* note */" : "// note\n";

        slot.Replace(new FuzzRaw(comment + FuzzJsonWriter.Write(slot.Node, random, varyLayout: false)));
        return slot.Expects.ToString();
    }

    private static string? AddTrailingComma(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var containers = Nodes(root).Where(n => n is FuzzObject { Members.Count: > 0 } or FuzzArray { Items.Count: > 0 }).ToList();

        switch (Pick(random, containers))
        {
            case FuzzObject obj:
                obj.TrailingComma = true;
                return obj.Role.ToString();

            case FuzzArray array:
                array.TrailingComma = true;
                return "array";

            default:
                return null;
        }
    }

    private static string? BreakEscape(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var broken = Pick(random, [Backslash + "x41", Backslash + "u12", Backslash + "U0041", Backslash + "'", Backslash + "u12G4", "\u0001", "\n", "\t", Backslash + "a"]);
        var text = "\"a" + broken + "b\"";

        if (random.Next(2) == 0)
        {
            var (owner, index) = Pick(random, Members(root).ToList());
            owner.Members[index] = owner.Members[index] with { RawName = text };
            return owner.Role + " name";
        }

        var strings = Slots(root).Where(s => s.Node is FuzzString).ToList();
        var slot = Pick(random, strings);
        slot.Replace(new FuzzRaw(text));
        return slot.Expects.ToString();
    }

    #endregion

    #region Changes left to the decoders to agree on

    private static string? NestDeeply(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        // The readers stop at 64 levels. Unknown fields sit two to three levels down already, so
        // this straddles the limit from both sides.
        var depth = random.Next(56, 68);
        FuzzNode value = Free(random, 0);

        for (var i = 0; i < depth; i++)
        {
            value = random.Next(2) == 0
                ? new FuzzObject().Add("n", value)
                : new FuzzArray { Items = { value } };
        }

        var target = Pick(random, ContractObjects(root));
        target.Members.Insert(random.Next(target.Members.Count + 1), new FuzzMember(UnknownName(target, random), value));
        return depth.ToString(CultureInfo.InvariantCulture) + " levels under " + target.Role;
    }

    private static string? LoneSurrogate(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var lone = Pick(random, [Units(0xD800), Units(0xDFFF), Units(0xDBFF), Units(0xDC00), Units(0xD800, 0xD800), Units(0xDC00, 0xD800)]);
        var text = Word(random, random.Next(0, 3)) + lone + Word(random, random.Next(0, 3));

        switch (random.Next(3))
        {
            case 0:
                // An unknown field, whose name no decoder needs, of a length either side of the
                // point where a name stops being worth comparing to known ones.
                var target = Pick(random, ContractObjects(root));
                var name = random.Next(2) == 0 ? text : text + new string('z', random.Next(10, 120));
                target.Members.Insert(random.Next(target.Members.Count + 1), new FuzzMember(name, Free(random, 1)));
                return "unknown field name in " + target.Role;

            case 1:
                var (owner, index) = Pick(random, Members(root).ToList());
                owner.Members[index] = owner.Members[index] with { Name = text };
                return owner.Role + " name";

            default:
                var slot = Pick(random, Slots(root).Where(s => s.Node is FuzzString).ToList());
                slot.Replace(new FuzzString(text));
                return slot.Expects.ToString();
        }
    }

    private static string? RepeatUnknownField(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var target = Pick(random, ContractObjects(root));
        var name = UnknownName(target, random);

        target.Members.Insert(random.Next(target.Members.Count + 1), new FuzzMember(name, Free(random, 1)));
        target.Members.Insert(random.Next(target.Members.Count + 1), new FuzzMember(name, Free(random, 1), EscapeName: random.Next(2) == 0));
        return target.Role + " repeats \"" + name + "\"";
    }

    private static string? ChangeNumber(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var slot = Pick(random, Slots(root).Where(s => s.Expects is Expect.Unit or Expect.ScoreValue).ToList());
        var maximum = Maximum(slot);
        var value = (random.NextDouble() * (maximum + 0.5)) - 0.25;

        slot.Replace(FuzzRaw.Of(value));
        return slot.Expects + " becomes " + value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string? ChangeModel(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var index = root.Members.FindIndex(m => m.Name == "model");
        root.Members[index] = root.Members[index] with { Value = new FuzzString(Text(random, allowLong: true)) };
        return string.Empty;
    }

    private static string? ChangeLegendValue(FuzzObject root, Random random, JevDecisionPlan plan)
    {
        var slots = Slots(root).Where(s => s.Expects == Expect.LegendValue).ToList();

        if (slots.Count == 0)
        {
            return null;
        }

        Pick(random, slots).Replace(Free(random, 3));
        return string.Empty;
    }

    #endregion

    #region Tree access

    /// <summary>What the contract expects of a value, judged by where it sits.</summary>
    private enum Expect
    {
        /// <summary>Nothing: an unknown field's value, or anything inside a legend value.</summary>
        Free,
        Text,
        TypeTag,
        Label,
        Unit,
        ScoreValue,
        Count,
        LegendValue,
        Object,
    }

    private readonly record struct Slot(FuzzNode Node, Action<FuzzNode> Replace, FuzzObject? Owner, string? Name, Expect Expects);

    private static FuzzObject[] ContractObjects(FuzzObject root)
        => [.. Nodes(root).OfType<FuzzObject>().Where(o => o.Role is FuzzRole.Root or FuzzRole.Answer or FuzzRole.Usage)];

    private static string[] Known(FuzzObject obj) => obj.Role switch
    {
        FuzzRole.Root => ["model", "answers", "usage"],
        FuzzRole.Usage => ["input_tokens", "output_tokens"],
        FuzzRole.Answer => obj.Question!.Kind switch
        {
            QuestionKind.Noul => ["type", "noul"],
            QuestionKind.Choice => ["type", "choice", "confidence", "probabilities"],
            _ => ["type", "score", "confidence", "legend", "probabilities"],
        },
        _ => [],
    };

    /// <summary>
    /// A name the object does not define. Never one starting with a dollar in an answer: that is
    /// <see cref="AddDollarField"/>'s alone, so the reference decoder's one known error cannot mask
    /// another change.
    /// </summary>
    private static string UnknownName(FuzzObject target, Random random)
    {
        var known = Known(target);

        while (true)
        {
            var name = random.Next(3) == 0 ? Text(random) : Pick(random, UnknownNames);

            if (!known.Contains(name) && !(target.Role == FuzzRole.Answer && name.StartsWith('$')))
            {
                return name;
            }
        }
    }

    private static double Maximum(Slot slot)
        => slot.Expects == Expect.ScoreValue ? ((ScoreQuestionDefinition)slot.Owner!.Question!).Levels.Length - 1 : 1;

    private static Expect Classify(FuzzObject? owner, string? name) => owner?.Role switch
    {
        FuzzRole.Root => name switch
        {
            "model" => Expect.Text,
            "answers" or "usage" => Expect.Object,
            _ => Expect.Free,
        },
        FuzzRole.Answers => Expect.Object,
        FuzzRole.Usage => name is "input_tokens" or "output_tokens" ? Expect.Count : Expect.Free,
        FuzzRole.ChoiceProbabilities or FuzzRole.LevelProbabilities => Expect.Unit,
        FuzzRole.Legend => Expect.LegendValue,
        FuzzRole.Answer => (owner!.Question!.Kind, name) switch
        {
            (_, "type") => Expect.TypeTag,
            (QuestionKind.Noul, "noul") => Expect.Unit,
            (QuestionKind.Choice, "choice") => Expect.Label,
            (QuestionKind.Choice or QuestionKind.Score, "confidence") => Expect.Unit,
            (QuestionKind.Choice, "probabilities") => Expect.Object,
            (QuestionKind.Score, "score") => Expect.ScoreValue,
            (QuestionKind.Score, "legend" or "probabilities") => Expect.Object,
            _ => Expect.Free,
        },
        _ => Expect.Free,
    };

    /// <summary>Every value in the tree below the root, with a way to replace it and what the contract expects of it.</summary>
    private static List<Slot> Slots(FuzzObject root)
    {
        var slots = new List<Slot>();
        Visit(root);
        return slots;

        void Visit(FuzzNode node)
        {
            switch (node)
            {
                case FuzzObject obj:
                    for (var i = 0; i < obj.Members.Count; i++)
                    {
                        var index = i;
                        var member = obj.Members[i];

                        // A value inside a legend value or an unknown field is free, whatever its parent's role.
                        var expects = Classify(obj, member.Name);
                        slots.Add(new Slot(member.Value, v => obj.Members[index] = obj.Members[index] with { Value = v }, obj, member.Name, expects));

                        if (obj.Role == FuzzRole.Free || expects is Expect.Object)
                        {
                            Visit(member.Value);
                        }
                        else if (expects is Expect.Free or Expect.LegendValue)
                        {
                            VisitFree(member.Value);
                        }
                    }

                    break;

                case FuzzArray array:
                    VisitFree(array);
                    break;
            }
        }

        void VisitFree(FuzzNode node)
        {
            switch (node)
            {
                case FuzzObject obj:
                    for (var i = 0; i < obj.Members.Count; i++)
                    {
                        var index = i;
                        slots.Add(new Slot(obj.Members[i].Value, v => obj.Members[index] = obj.Members[index] with { Value = v }, obj, obj.Members[i].Name, Expect.Free));
                        VisitFree(obj.Members[i].Value);
                    }

                    break;

                case FuzzArray array:
                    for (var i = 0; i < array.Items.Count; i++)
                    {
                        var index = i;
                        slots.Add(new Slot(array.Items[i], v => array.Items[index] = v, null, null, Expect.Free));
                        VisitFree(array.Items[i]);
                    }

                    break;
            }
        }
    }

    private static IEnumerable<FuzzNode> Nodes(FuzzNode node)
    {
        yield return node;

        IEnumerable<FuzzNode> children = node switch
        {
            FuzzObject obj => obj.Members.Select(m => m.Value),
            FuzzArray array => array.Items,
            _ => [],
        };

        foreach (var child in children.ToList())
        {
            foreach (var descendant in Nodes(child))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<(FuzzObject Owner, int Index)> Members(FuzzObject root)
    {
        foreach (var obj in Nodes(root).OfType<FuzzObject>())
        {
            for (var i = 0; i < obj.Members.Count; i++)
            {
                yield return (obj, i);
            }
        }
    }

    private static T Pick<T>(Random random, IReadOnlyList<T> items) => items[random.Next(items.Count)];

    /// <summary>The text in a normalisation form, or unchanged where the platform refuses to normalise it.</summary>
    private static string Normalize(string text, NormalizationForm form)
    {
        try
        {
            return text.Normalize(form);
        }
        catch (ArgumentException)
        {
            return text;
        }
    }

    /// <summary>
    /// Text from UTF-16 code units, so that invisible, look-alike and unpaired characters are
    /// legible in this source rather than hidden in a literal.
    /// </summary>
    private static string Units(params ReadOnlySpan<int> units)
    {
        var chars = new char[units.Length];

        for (var i = 0; i < units.Length; i++)
        {
            chars[i] = (char)units[i];
        }

        return new string(chars);
    }

    private static void Shuffle<T>(List<T> items, Random random)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    #endregion

    private sealed record Mutation(string Name, Apply Change);

    private sealed record TextMutation(string Name, Func<string, Random, string> Change);
}
