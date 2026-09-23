using System.Text;
using System.Text.Json;
using Kkdev92.Jev.Decoding;
using Kkdev92.Jev.TestSupport;
using static Kkdev92.Jev.Tests.TestPlans;

namespace Kkdev92.Jev.Tests;

/// <summary>
/// The response contract, checked against both decoders: the handwritten reader the client uses and
/// the reference decoder built on the generated wire models.
/// </summary>
/// <remarks>
/// Every case runs through both. Where they must agree — on acceptance, and on every value of an
/// accepted response — <see cref="BothDecodersAgreeOnEveryFixture"/> says so directly; the named
/// cases pin what the handwritten reader reports.
/// </remarks>
public sealed class ResponseDecodingTests
{
    private const double Tolerance = 1e-6;

    /// <summary>A backslash-u escape, written without a backslash-u in this source file.</summary>
    private static string U(string hex) => (char)92 + "u" + hex;

    private delegate DecodedResponse Decoder(ReadOnlySpan<byte> body, JevDecisionPlan plan, double tolerance);

    public static TheoryData<string> Decoders => ["reader", "reference"];

    private static Decoder Get(string name) => name == "reader"
        ? SystemOneResponseReader.Read
        : ReferenceResponseDecoder.Decode;

    private static DecodedResponse Decode(string decoder, string json, JevDecisionPlan plan)
        => Get(decoder)(Encoding.UTF8.GetBytes(json), plan, Tolerance);

    private static JevProtocolError Refuse(string decoder, string json, JevDecisionPlan plan)
        => Assert.Throws<JevProtocolException>(() => Decode(decoder, json, plan)).Error;

    [Theory]
    [MemberData(nameof(Decoders))]
    public void Q01_ANoulAnswerKeepsItsProbabilityModelAndUsage(string decoder)
    {
        var plan = NoulPlan();
        var decoded = Decode(decoder, new SystemOneResponseBuilder().Noul("n", 0.98).Model("jev-1.13.0").Build(), plan);

        Assert.Equal(0.98, decoded.Slots[0].Value);
        Assert.True(double.IsNaN(decoded.Slots[0].Confidence));
        Assert.Equal("jev-1.13.0", decoded.Model);
        Assert.Equal(new JevUsage(120, 12), decoded.Usage);
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void Q02_AChoiceWithTheMaximumOptionsMapsEveryProbability(string decoder)
    {
        var builder = new JevDecisionPlanBuilder();
        var labels = Enumerable.Range(0, 255).Select(i => $"option_{i}").ToArray();
        builder.AddChoice<int>("c", "q", [.. labels.Select((l, i) => new JevChoiceOption<int>(i, l))]);
        var plan = builder.Build();

        var probabilities = labels.Select((l, i) => (l, i == 200 ? 0.9 : 0.1 / 254)).ToArray();
        var decoded = Decode(decoder, new SystemOneResponseBuilder().Choice("c", "option_200", 0.8, probabilities).Build(), plan);

        Assert.Equal(200, decoded.Slots[0].Selected);
        Assert.Equal(0.8, decoded.Slots[0].Confidence);
        Assert.Equal(0.9, decoded.Probabilities[200]);
        Assert.Equal(0.1 / 254, decoded.Probabilities[3]);
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void Q03_AScoreKeepsItsFractionalValueAndAStructuredLegend(string decoder)
    {
        var plan = ScorePlan(3);
        var answer = """{"type":"score","score":1.09,"confidence":0.87,"legend":{"0":{"what":"Cosmetic","examples":["typo"]},"1":"Broken","2":["Blocking",{"no":"workaround"}]},"probabilities":{"0":0.0,"1":0.91,"2":0.09}}""";
        var decoded = Decode(decoder, new SystemOneResponseBuilder().Raw("s", answer).Build(), plan);

        Assert.Equal(1.09, decoded.Slots[0].Value);
        Assert.Equal(0.87, decoded.Slots[0].Confidence);
        Assert.Equal([0.0, 0.91, 0.09], decoded.Probabilities);

        var legend = decoded.Legends![0];
        Assert.Equal(JsonValueKind.Object, legend[0].ValueKind);
        Assert.Equal("typo", legend[0].GetProperty("examples")[0].GetString());
        Assert.Equal("Broken", legend[1].GetString());
        Assert.Equal(JsonValueKind.Array, legend[2].ValueKind);
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void Q04_MixedAnswersMapByIdWhateverTheirOrder(string decoder)
    {
        var (plan, _, _, _) = Triage();
        var body = new SystemOneResponseBuilder()
            .Noul("is_urgent", 0.95)
            .Score("frustration", 1.05, 0.92, ["Calm", "Frustrated", "Very angry"], 0.0, 0.95, 0.05)
            .Choice("department", "technical", 0.7, ("sales", 0.0), ("technical", 0.85), ("billing", 0.15))
            .Build();

        var decoded = Decode(decoder, body, plan);

        Assert.Equal(1, decoded.Slots[0].Selected);
        Assert.Equal([0.15, 0.85, 0.0], decoded.Probabilities.AsSpan(0, 3).ToArray());
        Assert.Equal(1.05, decoded.Slots[1].Value);
        Assert.Equal(0.95, decoded.Slots[2].Value);
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void Q07_UnknownFieldsAreIgnoredAtEveryLevel(string decoder)
    {
        var plan = NoulPlan();
        var body = """{"new_top":{"deep":[1,{"x":null}]},"model":"m","answers":{"n":{"added":true,"type":"noul","noul":0.5,"explanation":"x"}},"usage":{"input_tokens":1,"output_tokens":0,"cached_tokens":7}}""";

        Assert.Equal(0.5, Decode(decoder, body, plan).Slots[0].Value);
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void Q08_AnUnknownTypeOrLabelIsNeverMappedToSomethingElse(string decoder)
    {
        var plan = ChoicePlan();

        Refuse(decoder, new SystemOneResponseBuilder().Raw("c", """{"type":"rank","rank":1}""").Build(), plan);
        Refuse(decoder, new SystemOneResponseBuilder().Choice("c", "maybe", 0.5, ("yes", 0.5), ("no", 0.5)).Build(), plan);
        Refuse(decoder, new SystemOneResponseBuilder().Choice("c", "yes", 0.5, ("yes", 0.5), ("no", 0.3), ("maybe", 0.2)).Build(), plan);
    }

    [Fact]
    public void Q08_TheReaderReportsAWrongTypeAsSuchWhereverTheTypeFieldIs()
    {
        var plan = ChoicePlan();
        var scoreAnswer = """{"legend":{"0":"a","1":"b"},"probabilities":{"0":0.5,"1":0.5},"score":0.5,"confidence":0.1,"type":"score"}""";

        Assert.Equal(JevProtocolError.AnswerTypeMismatch, Refuse("reader", new SystemOneResponseBuilder().Raw("c", scoreAnswer).Build(), plan));
        Assert.Equal(JevProtocolError.UnknownAnswerType, Refuse("reader", new SystemOneResponseBuilder().Raw("c", """{"type":"rank"}""").Build(), plan));
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void Q09_DuplicatesAreRefusedInAnySpelling(string decoder)
    {
        var plan = ChoicePlan();

        // A duplicated answer id, spelled differently the second time.
        Refuse(decoder, "{\"model\":\"m\",\"answers\":{\"c\":" + (ChoiceYes()) + ",\"" + (U("0063")) + "\":" + (ChoiceYes()) + "},\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}", plan);

        // A duplicated probability key.
        Refuse(decoder, new SystemOneResponseBuilder().Raw("c", "{\"type\":\"choice\",\"choice\":\"yes\",\"confidence\":0.5,\"probabilities\":{\"yes\":0.5,\"no\":0.5,\"" + (U("0079")) + "es\":0.5}}").Build(), plan);

        // A duplicated known field.
        Refuse(decoder, new SystemOneResponseBuilder().Raw("c", """{"type":"choice","choice":"yes","choice":"no","confidence":0.5,"probabilities":{"yes":0.5,"no":0.5}}""").Build(), plan);
        Refuse(decoder, """{"model":"a","model":"b","answers":{"c":""" + ChoiceYes() + """},"usage":{"input_tokens":1,"output_tokens":1}}""", plan);
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void Q10_EscapedAndNonAsciiNamesMatchByMeaning(string decoder)
    {
        var builder = new JevDecisionPlanBuilder();
        builder.AddChoice<string>("部署", "q", [new("請求", "請求"), new("技術😀", "技術😀")]);
        var plan = builder.Build();

        // The id and a label spelled with escapes, the other label spelled literally.
        var escapedId = U("90e8") + U("7f72");
        var escapedLabel = U("8acb") + U("6c42");
        var body = "{\"model\":\"m\",\"answers\":{\"" + (escapedId) + "\":{\"type\":\"choice\",\"choice\":\"技術😀\",\"confidence\":0.9,\"probabilities\":{\"" + (escapedLabel) + "\":0.1,\"技術😀\":0.9}}},\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}";

        var decoded = Decode(decoder, body, plan);

        Assert.Equal(1, decoded.Slots[0].Selected);
        Assert.Equal([0.1, 0.9], decoded.Probabilities);
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void Q11_TokenCountsAreExactIntegersInAnySpelling(string decoder)
    {
        var plan = NoulPlan();

        foreach (var (usage, input) in new[] { ("120", 120L), ("120.0", 120L), ("1.2e2", 120L), ("9223372036854775807", long.MaxValue) })
        {
            var body = new SystemOneResponseBuilder().Noul("n", 0.5).Usage("{\"input_tokens\":" + (usage) + ",\"output_tokens\":0}").Build();
            Assert.Equal(input, Decode(decoder, body, plan).Usage.InputTokens);
        }
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void Q11_InvalidNumbersAreRefusedNotRepaired(string decoder)
    {
        var plan = NoulPlan();

        foreach (var usage in new[] { "-1", "120.5", "9223372036854775808", "\"120\"", "null", "1e400" })
        {
            Refuse(decoder, new SystemOneResponseBuilder().Noul("n", 0.5).Usage("{\"input_tokens\":" + (usage) + ",\"output_tokens\":0}").Build(), plan);
        }

        foreach (var noul in new[] { "1e400", "-1e400", "1.5", "-0.1", "\"0.5\"", "null", "true" })
        {
            Refuse(decoder, new SystemOneResponseBuilder().Raw("n", "{\"type\":\"noul\",\"noul\":" + (noul) + "}").Build(), plan);
        }
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void Q11_BoundaryNumbersAreKeptAsSent(string decoder)
    {
        var plan = NoulPlan();

        foreach (var (noul, expected) in new[] { ("0", 0.0), ("1", 1.0), ("-0", -0.0), ("1e-300", 1e-300), ("1.0000001", 1.0000001), ("-0.0000009", -0.0000009) })
        {
            var decoded = Decode(decoder, new SystemOneResponseBuilder().Raw("n", "{\"type\":\"noul\",\"noul\":" + (noul) + "}").Build(), plan);
            Assert.Equal(expected, decoded.Slots[0].Value);
        }
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void Q11_LevelKeysMatchAsTextNotAsNumbers(string decoder)
    {
        var plan = ScorePlan(2);

        foreach (var key in new[] { "00", "0.0", "+0", " 0", "2", "-1", "01" })
        {
            var answer = "{\"type\":\"score\",\"score\":0.5,\"confidence\":0.5,\"legend\":{\"0\":\"a\",\"1\":\"b\"},\"probabilities\":{\"" + (key) + "\":0.5,\"1\":0.5}}";
            Refuse(decoder, new SystemOneResponseBuilder().Raw("s", answer).Build(), plan);
        }
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void Q12_FieldOrderDoesNotMatter(string decoder)
    {
        var plan = ChoicePlan();
        var body = """{"usage":{"output_tokens":2,"input_tokens":1},"answers":{"c":{"probabilities":{"no":0.25,"yes":0.75},"confidence":0.6,"choice":"yes","type":"choice"}},"model":"m"}""";

        var decoded = Decode(decoder, body, plan);

        Assert.Equal(0, decoded.Slots[0].Selected);
        Assert.Equal(new JevUsage(1, 2), decoded.Usage);
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void Q13_MalformedBodiesAreRefused(string decoder)
    {
        var plan = NoulPlan();
        var valid = new SystemOneResponseBuilder().Noul("n", 0.5).Build();

        foreach (var body in new[]
        {
            "",
            valid[..^1],
            valid + " {}",
            valid + valid,
            valid.Replace("\"noul\":0.5", "\"noul\":0.5,", StringComparison.Ordinal),
            "/*c*/" + valid,
            "null",
            "[]",
            "\"text\"",
        })
        {
            Refuse(decoder, body, plan);
        }
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void MissingPiecesAreRefusedNotDefaulted(string decoder)
    {
        var (plan, _, _, _) = Triage();

        // One question unanswered.
        Refuse(decoder, new SystemOneResponseBuilder().Noul("is_urgent", 0.5).Choice("department", "billing", 1, ("billing", 1), ("technical", 0), ("sales", 0)).Build(), plan);

        // An option with no probability: absent is not zero.
        Refuse(decoder, TriageWith(choice: """{"type":"choice","choice":"billing","confidence":1,"probabilities":{"billing":1,"technical":0}}"""), plan);

        // A level with no legend entry.
        Refuse(decoder, TriageWith(score: """{"type":"score","score":1,"confidence":1,"legend":{"0":"a","1":"b"},"probabilities":{"0":0,"1":1,"2":0}}"""), plan);

        // A null legend entry, which the contract does not allow.
        Refuse(decoder, TriageWith(score: """{"type":"score","score":1,"confidence":1,"legend":{"0":"a","1":"b","2":null},"probabilities":{"0":0,"1":1,"2":0}}"""), plan);

        // A required field missing.
        Refuse(decoder, TriageWith(choice: """{"type":"choice","choice":"billing","probabilities":{"billing":1,"technical":0,"sales":0}}"""), plan);
        Refuse(decoder, """{"model":"m","answers":{}}""", plan);
        Refuse(decoder, """{"answers":{},"usage":{"input_tokens":1,"output_tokens":1}}""", plan);

        // An answer to a question that was not asked.
        Refuse(decoder, TriageResponse().Replace("\"answers\":{", "\"answers\":{\"extra\":{\"type\":\"noul\",\"noul\":0.5},", StringComparison.Ordinal), plan);
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void ValuesOutsideTheirDocumentedRangeAreRefused(string decoder)
    {
        var plan = ScorePlan(3);

        Refuse(decoder, new SystemOneResponseBuilder().Score("s", 2.5, 0.5, ["a", "b", "c"], 0, 0.5, 0.5).Build(), plan);
        Refuse(decoder, new SystemOneResponseBuilder().Score("s", 1, 1.5, ["a", "b", "c"], 0, 1, 0).Build(), plan);
        Refuse(decoder, new SystemOneResponseBuilder().Score("s", 1, 0.5, ["a", "b", "c"], 0, 1.2, 0).Build(), plan);
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void AnUnknownFieldWhoseNameDoesNotDecodeIsRefusedWhateverItsLength(string decoder)
    {
        var (plan, _, _, _) = Triage();

        // Whatever its length and first letters: comparing a name with a known one only decodes it
        // when the two could match, so a malformed name has to be decoded outright to be refused.
        foreach (var padding in new[] { 0, 40, 120 })
        {
            var member = "\"a" + U("d800") + new string('z', padding) + "\":1,";

            Assert.Equal(JevProtocolError.MalformedJson, Refuse(decoder, TriageResponse().Insert(1, member), plan));
            Assert.Equal(JevProtocolError.MalformedJson, Refuse(decoder, TriageResponse().Replace("{\"type\":\"noul\"", "{" + member + "\"type\":\"noul\"", StringComparison.Ordinal), plan));
            Assert.Equal(JevProtocolError.MalformedJson, Refuse(decoder, TriageResponse().Replace("{\"input_tokens\"", "{" + member + "\"input_tokens\"", StringComparison.Ordinal), plan));
        }
    }

    /// <summary>
    /// The one input on which the decoders are known to differ — and the reference is the one that
    /// is wrong.
    /// </summary>
    /// <remarks>
    /// System.Text.Json reserves names beginning with <c>$</c> for its own metadata inside a
    /// polymorphic type: "Properties that start with '$' are not allowed in types that support
    /// metadata", in any position and any spelling — measured on .NET 10.0.12. An answer is such a
    /// type in the wire models. The contract says an unknown field is ignored, so the client keeps
    /// the answer. Where nothing is polymorphic both decoders ignore the field. Should this start
    /// failing because the reference accepts, the serializer has changed, and
    /// <see cref="DecoderFuzzTests"/> no longer needs to excuse the difference.
    /// </remarks>
    [Fact]
    public void AFieldNamedWithADollarInsideAnAnswerIsIgnoredThoughTheReferenceRefusesIt()
    {
        var (plan, _, _, _) = Triage();

        foreach (var name in new[] { "$extra", "$id", "$type", U("0024") + "ref" })
        {
            var inAnswer = TriageResponse().Replace("{\"type\":\"noul\",\"noul\":0.95}", "{\"type\":\"noul\",\"" + name + "\":1,\"noul\":0.95}", StringComparison.Ordinal);

            Assert.Equal(0.95, Decode("reader", inAnswer, plan).Slots[2].Value);
            Assert.Equal(JevProtocolError.MalformedJson, Refuse("reference", inAnswer, plan));

            foreach (var elsewhere in new[]
            {
                TriageResponse().Insert(1, "\"" + name + "\":1,"),
                TriageResponse().Replace("{\"input_tokens\"", "{\"" + name + "\":1,\"input_tokens\"", StringComparison.Ordinal),
                TriageResponse().Replace("\"0\":\"Calm\"", "\"0\":{\"" + name + "\":1}", StringComparison.Ordinal),
            })
            {
                Assert.Equal(0.95, Decode("reader", elsewhere, plan).Slots[2].Value);
                Assert.Equal(0.95, Decode("reference", elsewhere, plan).Slots[2].Value);
            }
        }
    }

    /// <summary>
    /// The two decoders are held to each other: every fixture here is accepted by both or refused by
    /// both, and an accepted one decodes to the same values.
    /// </summary>
    [Fact]
    public void BothDecodersAgreeOnEveryFixture()
    {
        var (plan, _, _, _) = Triage();

        foreach (var body in Corpus())
        {
            var reader = Outcome(() => SystemOneResponseReader.Read(Encoding.UTF8.GetBytes(body), plan, Tolerance));
            var reference = Outcome(() => ReferenceResponseDecoder.Decode(Encoding.UTF8.GetBytes(body), plan, Tolerance));

            Assert.True(reader.Accepted == reference.Accepted, $"The decoders disagree on {body}: reader {reader.Accepted}, reference {reference.Accepted}.");

            if (reader.Accepted)
            {
                Assert.Equal(reference.Decoded!.Model, reader.Decoded!.Model);
                Assert.Equal(reference.Decoded.Usage, reader.Decoded.Usage);
                Assert.Equal(reference.Decoded.Probabilities, reader.Decoded.Probabilities);

                for (var i = 0; i < plan.QuestionCount; i++)
                {
                    Assert.Equal(reference.Decoded.Slots[i].Selected, reader.Decoded.Slots[i].Selected);
                    Assert.Equal(reference.Decoded.Slots[i].Value, reader.Decoded.Slots[i].Value);
                    Assert.Equal(reference.Decoded.Slots[i].Confidence, reader.Decoded.Slots[i].Confidence);
                }

                var legendReader = reader.Decoded.Legends![1];
                var legendReference = reference.Decoded.Legends![1];
                Assert.True(legendReader.Zip(legendReference).All(p => JsonElement.DeepEquals(p.First, p.Second)));
            }
        }
    }

    private static IEnumerable<string> Corpus()
    {
        yield return TriageResponse();
        yield return TriageResponse().Replace("0.95}", "0.95,\"extra\":[1,2,3]}", StringComparison.Ordinal);
        yield return TriageResponse().Replace("\"model\":\"jev-1.13.0\"", "\"model\":\"jev-1.13.0\",\"model\":\"x\"", StringComparison.Ordinal);
        yield return TriageResponse().Replace("\"billing\":0.88", "\"billing\":\"0.88\"", StringComparison.Ordinal);
        yield return TriageResponse().Replace("\"input_tokens\":120", "\"input_tokens\":120.0", StringComparison.Ordinal);
        yield return TriageResponse().Replace("\"input_tokens\":120", "\"input_tokens\":120.5", StringComparison.Ordinal);
        yield return TriageResponse().Replace("\"noul\":0.95", "\"noul\":1e400", StringComparison.Ordinal);
        yield return TriageResponse().Replace("\"type\":\"noul\",", string.Empty, StringComparison.Ordinal);
        yield return TriageResponse().Replace("\"type\":\"noul\"", "\"type\":\"choice\"", StringComparison.Ordinal);
        yield return TriageResponse().Replace("\"0\":\"Calm\"", "\"0\":{\"what\":\"Calm\",\"n\":[1,null]}", StringComparison.Ordinal);
        yield return TriageResponse().Replace("\"1\":0.95", "\"01\":0.95", StringComparison.Ordinal);
        yield return TriageResponse().Replace("\"choice\":\"billing\"", "\"choice\":\"Billing\"", StringComparison.Ordinal);
        yield return TriageResponse().Replace("\"answers\":{", "\"answers\":{\"ghost\":{\"type\":\"noul\",\"noul\":0.1},", StringComparison.Ordinal);
        yield return TriageResponse() + " ";
        yield return TriageResponse() + "x";
    }

    private static (bool Accepted, DecodedResponse? Decoded) Outcome(Func<DecodedResponse> decode)
    {
        try
        {
            return (true, decode());
        }
        catch (JevProtocolException)
        {
            return (false, null);
        }
    }

    private static string ChoiceYes() => """{"type":"choice","choice":"yes","confidence":0.5,"probabilities":{"yes":0.75,"no":0.25}}""";

    private static string TriageWith(string? choice = null, string? score = null)
    {
        var builder = new SystemOneResponseBuilder();

        if (choice is null)
        {
            builder.Choice("department", "billing", 1, ("billing", 1), ("technical", 0), ("sales", 0));
        }
        else
        {
            builder.Raw("department", choice);
        }

        if (score is null)
        {
            builder.Score("frustration", 1, 1, ["a", "b", "c"], 0, 1, 0);
        }
        else
        {
            builder.Raw("frustration", score);
        }

        return builder.Noul("is_urgent", 0.5).Build();
    }

    private static JevDecisionPlan NoulPlan()
    {
        var builder = new JevDecisionPlanBuilder();
        builder.AddNoul("n", "q");
        return builder.Build();
    }

    private static JevDecisionPlan ChoicePlan()
    {
        var builder = new JevDecisionPlanBuilder();
        builder.AddChoice<bool>("c", "q", [new(true, "yes"), new(false, "no")]);
        return builder.Build();
    }

    private static JevDecisionPlan ScorePlan(int levels)
    {
        var builder = new JevDecisionPlanBuilder();
        builder.AddScore("s", "q", [.. Enumerable.Range(0, levels).Select(i => (JevContent)$"level {i}")]);
        return builder.Build();
    }
}
