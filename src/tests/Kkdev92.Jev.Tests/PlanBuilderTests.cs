using System.Text;
using System.Text.Json;
using Kkdev92.Jev.Requests;
using Kkdev92.Jev.Wire;
using static Kkdev92.Jev.Tests.TestPlans;

namespace Kkdev92.Jev.Tests;

public sealed class PlanBuilderTests
{
    private static readonly char LoneSurrogate = (char)0xD800;

    [Fact]
    public void TheQuestionsAreEncodedOnceInRegistrationOrderExactlyAsGiven()
    {
        var (plan, _, _, _) = Triage();

        Assert.Equal(
            """{"department":{"type":"choice","instructions":"Which team should handle this?","criteria":{"billing":"Payments, invoicing, refunds","technical":"Bugs, outages, integrations","sales":"Pricing, upgrades, new accounts"}},"frustration":{"type":"score","instructions":"How frustrated is the customer?","criteria":["Calm","Frustrated","Very angry"]},"is_urgent":{"type":"noul","instructions":"Does this convey urgency?"}}""",
            Encoding.UTF8.GetString(plan.EncodedQuestions));

        Assert.Equal(3, plan.QuestionCount);
        Assert.Equal(plan.EncodedQuestions.Length, plan.EncodedQuestionsLength);
    }

    [Fact]
    public void HandlesDescribeTheirQuestions()
    {
        var (_, department, frustration, urgent) = Triage();

        Assert.Equal("department", department.Id);
        Assert.Equal(3, department.OptionCount);
        Assert.Equal("frustration", frustration.Id);
        Assert.Equal(3, frustration.LevelCount);
        Assert.Equal("is_urgent", urgent.Id);
        Assert.False(urgent.IsDefault);
        Assert.True(default(JevNoulHandle).IsDefault);
        Assert.Equal(string.Empty, default(JevScoreHandle).Id);
    }

    /// <summary>
    /// The request the SDK writes is read back through the models generated from TypeSafe's own
    /// OpenAPI document, and checked against every constraint that document declares.
    /// </summary>
    [Fact]
    public void TheRequestConformsToTheOpenApiContract()
    {
        var builder = new JevDecisionPlanBuilder();
        builder.AddChoice<int>("c", default, [new(1, "one"), new(2, "two", JevContent.FromJson("""{"what":"two things"}"""))]);
        builder.AddScore("s", JevContent.Null, ["low", JevContent.FromJson("""["high","very high"]""")]);
        builder.AddNoul("n", JevContent.FromJson("""{"question":"Is it?","data":[1,true,null]}"""), new JevNoulCriteria("yes", JevContent.Null));
        var plan = builder.Build();

        var body = RequestBodyWriter.WriteEvaluate("jev-latest", StateSource.FromContent(JevContent.FromJson("""{"message":"hi"}""")), plan, 1 << 20);
        var request = JsonSerializer.Deserialize(body, JevWireJsonContext.Default.SystemOneRequest)!;

        var validation = new WireValidation();
        request.Validate(validation, "$");

        Assert.True(validation.IsValid, string.Join("; ", validation.Violations));
        Assert.Equal("jev-latest", request.Model);
        Assert.Equal(JsonValueKind.Object, request.State.ValueKind);
        Assert.Equal(["c", "s", "n"], request.Questions.Keys);

        var choice = Assert.IsType<ChoiceQuestion>(request.Questions["c"]);
        Assert.Equal(JsonValueKind.Undefined, choice.Instructions.ValueKind);
        Assert.Equal(JsonValueKind.Null, choice.Criteria["one"].ValueKind);
        Assert.Equal(JsonValueKind.Object, choice.Criteria["two"].ValueKind);

        var score = Assert.IsType<ScoreQuestion>(request.Questions["s"]);
        Assert.Equal(JsonValueKind.Null, score.Instructions.ValueKind);
        Assert.Equal(JsonValueKind.Array, score.Criteria[1].ValueKind);

        var noul = Assert.IsType<NoulQuestion>(request.Questions["n"]);
        Assert.Equal(JsonValueKind.Object, noul.Instructions.ValueKind);
        Assert.Equal("yes", noul.Criteria!.True.GetString());
        Assert.Equal(JsonValueKind.Null, noul.Criteria.False.ValueKind);
    }

    [Fact]
    public void FieldsAreWrittenModelStateQuestionsWithTheStateAsText()
    {
        var (plan, _, _, _) = Triage();

        var body = RequestBodyWriter.WriteEvaluate("jev-1.13.0", StateSource.FromContent("{\"x\":1}"), plan, 1 << 20);
        var text = Encoding.UTF8.GetString(body);

        // A string that looks like JSON is still a string.
        Assert.StartsWith("""{"model":"jev-1.13.0","state":"{\u0022x\u0022:1}","questions":{"department":""", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Text outside Basic Latin is written as its own UTF-8; what the encoder exists to guard is still
    /// escaped.
    /// </summary>
    /// <remarks>
    /// The default encoder writes each Japanese character as a six-byte escape, twice its UTF-8 size
    /// and several times slower to produce. The one the SDK uses allows the Basic Multilingual Plane
    /// and nothing more: HTML-sensitive characters, quotes and anything outside the plane, emoji
    /// included, stay escaped. The service reads the same string either way.
    /// </remarks>
    [Fact]
    public void TextIsWrittenAsUtf8AndHtmlSensitiveCharactersStayEscaped()
    {
        var builder = new JevDecisionPlanBuilder();
        builder.AddChoice<string>("部署", "どのチームが対応しますか？", [new("請求", "請求"), new("技術", "技術")]);
        var plan = builder.Build();

        const string state = "請求が二重です <b>&'\"</b> 😀";
        var body = RequestBodyWriter.WriteEvaluate("jev-latest", StateSource.FromContent(state), plan, 1 << 20);
        var text = Encoding.UTF8.GetString(body);

        Assert.Contains("請求が二重です", text, StringComparison.Ordinal);
        Assert.Contains("\"部署\"", text, StringComparison.Ordinal);
        Assert.Contains("どのチームが対応しますか？", text, StringComparison.Ordinal);
        Assert.Contains(U("003C") + "b" + U("003E") + U("0026") + U("0027") + U("0022"), text, StringComparison.Ordinal);
        Assert.Contains(U("D83D") + U("DE00"), text, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(body);
        Assert.Equal(state, document.RootElement.GetProperty("state").GetString());
    }

    /// <summary>Structured content keeps its non-ASCII text as UTF-8 too, however it was escaped when it arrived.</summary>
    [Fact]
    public void ContentIsStoredWithTheSameEncoder()
    {
        var content = JevContent.FromJson("{\"q\":\"" + U("8acb") + U("6c42") + "\"}");

        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            content.WriteTo(writer);
        }

        Assert.Equal("{\"q\":\"請求\"}", Encoding.UTF8.GetString(buffer.ToArray()));
    }

    [Fact]
    public void TheBodyIsExactlyAsLongAsWhatWasWrittenAndOwnedByTheCaller()
    {
        var (plan, _, _, _) = Triage();

        var first = RequestBodyWriter.WriteEvaluate("jev-latest", StateSource.FromContent("one"), plan, 1 << 20);
        var second = RequestBodyWriter.WriteEvaluate("jev-latest", StateSource.FromContent("two"), plan, 1 << 20);

        Assert.NotSame(first, second);
        Assert.Equal((byte)'}', first[^1]);
        Assert.Contains("\"state\":\"one\"", Encoding.UTF8.GetString(first), StringComparison.Ordinal);
        Assert.Contains("\"state\":\"two\"", Encoding.UTF8.GetString(second), StringComparison.Ordinal);
    }

    private static string U(string hex) => (char)92 + "u" + hex;

    [Fact]
    public void UnspecifiedInstructionsAreOmittedAndNullIsWritten()
    {
        var builder = new JevDecisionPlanBuilder();
        builder.AddNoul("omitted", default);
        builder.AddNoul("explicit_null", JevContent.Null);

        Assert.Equal(
            """{"omitted":{"type":"noul"},"explicit_null":{"type":"noul","instructions":null}}""",
            Encoding.UTF8.GetString(builder.Build().EncodedQuestions));
    }

    [Fact]
    public void NoulCriteriaSidesAreIndependent()
    {
        var builder = new JevDecisionPlanBuilder();
        builder.AddNoul("none", "q", default);
        builder.AddNoul("only_true", "q", new JevNoulCriteria("yes", default));
        builder.AddNoul("null_false", "q", new JevNoulCriteria(default, JevContent.Null));

        Assert.Equal(
            """{"none":{"type":"noul","instructions":"q"},"only_true":{"type":"noul","instructions":"q","criteria":{"true":"yes"}},"null_false":{"type":"noul","instructions":"q","criteria":{"false":null}}}""",
            Encoding.UTF8.GetString(builder.Build().EncodedQuestions));
    }

    [Fact]
    public void TextIsSentExactlyAsGivenWhateverItContains()
    {
        var builder = new JevDecisionPlanBuilder();
        builder.AddChoice<string>(
            "言語",
            "  決済が失敗しました 😀 é (e + combining acute: e\u0301)  ",
            [new("日本", "日本"), new("Japan", "Japan"), new("japan", "japan")]);

        var plan = builder.Build();
        using var document = JsonDocument.Parse(plan.EncodedQuestions);
        var question = document.RootElement.GetProperty("言語");

        Assert.Equal("  決済が失敗しました 😀 é (e + combining acute: e\u0301)  ", question.GetProperty("instructions").GetString());
        Assert.Equal(["日本", "Japan", "japan"], question.GetProperty("criteria").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void ChoiceOptionCountsAtTheDocumentedLimitsAreAccepted()
    {
        var builder = new JevDecisionPlanBuilder();

        builder.AddChoice<int>("one", "q", [new(1, "only")]);
        builder.AddChoice<int>("max", "q", Options(JevDecisionPlanBuilder.MaxChoiceOptions));

        Assert.Equal(2, builder.Build().QuestionCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(256)]
    public void ChoiceOptionCountsOutsideTheLimitsAreRefused(int count)
    {
        var builder = new JevDecisionPlanBuilder();
        Assert.Throws<ArgumentException>(() => builder.AddChoice<int>("c", "q", Options(count)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    public void ScoreLevelCountsOutsideTheLimitsAreRefused(int count)
    {
        var builder = new JevDecisionPlanBuilder();
        var levels = Enumerable.Range(0, count).Select(i => (JevContent)$"level {i}").ToArray();

        Assert.Throws<ArgumentException>(() => builder.AddScore("s", "q", levels));
    }

    [Fact]
    public void ScoreLevelsCannotBeNullOrUnspecified()
    {
        var builder = new JevDecisionPlanBuilder();

        Assert.Throws<ArgumentException>(() => builder.AddScore("s", "q", ["low", JevContent.Null]));
        Assert.Throws<ArgumentException>(() => builder.AddScore("s", "q", ["low", default]));
    }

    [Fact]
    public void AnOptionDescriptionMustBeGivenOrExplicitlyNull()
    {
        var builder = new JevDecisionPlanBuilder();

        Assert.Throws<ArgumentException>(() => builder.AddChoice<int>("c", "q", [new(1, "a", default)]));
        builder.AddChoice<int>("c", "q", [new(1, "a", JevContent.Null), new(2, "b")]);

        Assert.Equal(
            """{"c":{"type":"choice","instructions":"q","criteria":{"a":null,"b":null}}}""",
            Encoding.UTF8.GetString(builder.Build().EncodedQuestions));
    }

    [Fact]
    public void LabelsAreUniqueOrdinally()
    {
        var builder = new JevDecisionPlanBuilder();

        Assert.Throws<ArgumentException>(() => builder.AddChoice<int>("c", "q", [new(1, "a"), new(2, "a")]));

        // Case differs, so these are two labels.
        builder.AddChoice<int>("c", "q", [new(1, "a"), new(2, "A")]);
    }

    [Fact]
    public void NullValuesAndLabelsAreRefused()
    {
        var builder = new JevDecisionPlanBuilder();

        Assert.Throws<ArgumentNullException>(() => builder.AddChoice<string>("c", "q", [new(null!, "a")]));
        Assert.Throws<ArgumentNullException>(() => builder.AddChoice<int>("c", "q", [new(1, null!)]));
        Assert.Throws<ArgumentException>(() => builder.AddChoice<int>("c", "q", [new(1, "")]));
    }

    [Fact]
    public void IdsAreUniqueNonEmptyAndWellFormed()
    {
        var builder = new JevDecisionPlanBuilder();
        builder.AddNoul("id", "q");

        Assert.Throws<ArgumentException>(() => builder.AddNoul("id", "q"));
        Assert.Throws<ArgumentException>(() => builder.AddNoul("", "q"));
        Assert.Throws<ArgumentNullException>(() => builder.AddNoul(null!, "q"));
        Assert.Throws<ArgumentException>(() => builder.AddNoul("a" + LoneSurrogate, "q"));
    }

    /// <summary>
    /// Utf8JsonWriter writes U+FFFD in place of a lone surrogate without complaint, so an answer would
    /// come back under a different name than the question it answers. The builder refuses instead.
    /// </summary>
    [Fact]
    public void TextWithALoneSurrogateIsRefusedWhereverItAppears()
    {
        var bad = "x" + LoneSurrogate;
        var builder = new JevDecisionPlanBuilder();

        Assert.Throws<ArgumentException>(() => builder.AddNoul("n", bad));
        Assert.Throws<ArgumentException>(() => builder.AddNoul("n", "q", new JevNoulCriteria(bad, default)));
        Assert.Throws<ArgumentException>(() => builder.AddChoice<int>("c", "q", [new(1, bad)]));
        Assert.Throws<ArgumentException>(() => builder.AddChoice<int>("c", "q", [new(1, "a", bad)]));
        Assert.Throws<ArgumentException>(() => builder.AddScore("s", "q", ["low", bad]));
    }

    [Fact]
    public void ARefusedQuestionLeavesTheBuilderUsable()
    {
        var builder = new JevDecisionPlanBuilder();

        Assert.Throws<ArgumentException>(() => builder.AddChoice<int>("c", "q", []));
        Assert.Equal(0, builder.Count);

        builder.AddChoice<int>("c", "q", [new(1, "a")]);
        Assert.Equal(1, builder.Build().QuestionCount);
    }

    [Fact]
    public void ABuilderBuildsOnce()
    {
        var builder = new JevDecisionPlanBuilder();
        builder.AddNoul("n", "q");
        builder.Build();

        Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Throws<InvalidOperationException>(() => builder.AddNoul("m", "q"));
    }

    [Fact]
    public void AnEmptyPlanIsRefusedAndFaultsTheBuilder()
    {
        var builder = new JevDecisionPlanBuilder();

        Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Throws<InvalidOperationException>(() => builder.AddNoul("n", "q"));
    }

    [Fact]
    public void APlanHoldsAtMostMaxQuestions()
    {
        var builder = new JevDecisionPlanBuilder();

        for (var i = 0; i < JevDecisionPlanBuilder.MaxQuestions; i++)
        {
            builder.AddNoul($"q{i}", default);
        }

        Assert.Throws<InvalidOperationException>(() => builder.AddNoul("one_too_many", default));
        Assert.Equal(JevDecisionPlanBuilder.MaxQuestions, builder.Build().QuestionCount);
    }

    [Fact]
    public void OptionsAreCopiedWhenAdded()
    {
        var options = new JevChoiceOption<int>[] { new(1, "a"), new(2, "b") };
        var builder = new JevDecisionPlanBuilder();
        builder.AddChoice<int>("c", "q", options);

        options[0] = new JevChoiceOption<int>(9, "changed");

        Assert.Contains("\"a\":null", Encoding.UTF8.GetString(builder.Build().EncodedQuestions), StringComparison.Ordinal);
    }

    [Fact]
    public void APlanNeverPrintsItsQuestions()
    {
        var (plan, department, _, _) = Triage();

        Assert.Equal("JevDecisionPlan (3 questions)", plan.ToString());
        Assert.Equal("JevChoiceHandle (department)", department.ToString());
    }

    private static JevChoiceOption<int>[] Options(int count)
        => [.. Enumerable.Range(0, count).Select(i => new JevChoiceOption<int>(i, $"option_{i}"))];
}
