using System.Text;
using Kkdev92.Jev.Decoding;
using Kkdev92.Jev.TestSupport;

namespace Kkdev92.Jev.Tests;

/// <summary>
/// Box answers: read by both decoders, checked for edges that cross, and returned through a handle.
/// </summary>
/// <remarks>
/// The generated-response fuzzer does not produce box answers, so the cases are written out here,
/// and every decoding case runs through both decoders.
/// </remarks>
public sealed class BoxAnswerTests
{
    private const double Tolerance = 1e-6;

    private const string Edges = """{"top":0.1,"bottom":0.6,"left":0.2,"right":0.7}""";

    private delegate DecodedResponse Decoder(ReadOnlySpan<byte> body, JevDecisionPlan plan, double tolerance);

    public static TheoryData<string> Decoders => ["reader", "reference"];

    [Theory]
    [MemberData(nameof(Decoders))]
    public void ABoxAnswerKeepsItsFourEdgesAndItsConfidence(string decoder)
    {
        var (plan, _) = BoxPlan();
        var decoded = Decode(decoder, new SystemOneResponseBuilder().Box("b", 0.1, 0.6, 0.2, 0.7, 0.85).Build(), plan);

        // Top, bottom, left, right: the order the plan keeps them in.
        Assert.Equal([0.1, 0.6, 0.2, 0.7], decoded.Probabilities);
        Assert.Equal(0.85, decoded.Slots[0].Confidence);
        Assert.True(double.IsNaN(decoded.Slots[0].Value));
        Assert.Equal(-1, decoded.Slots[0].Selected);
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void ANullConfidenceIsKeptAsNoNumber(string decoder)
    {
        var (plan, _) = BoxPlan();
        var decoded = Decode(decoder, new SystemOneResponseBuilder().Box("b", 0.1, 0.6, 0.2, 0.7, null).Build(), plan);

        Assert.True(double.IsNaN(decoded.Slots[0].Confidence));
        Assert.Equal([0.1, 0.6, 0.2, 0.7], decoded.Probabilities);
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void ABoxAmongOtherAnswersKeepsItsEdgesBetweenTheirDistributions(string decoder)
    {
        var (plan, _) = MixedPlan();
        var decoded = Decode(decoder, MixedResponse().Box("b", 0.1, 0.6, 0.2, 0.7, 0.85).Build(), plan);

        Assert.Equal([0.95, 0.05, 0.1, 0.6, 0.2, 0.7, 0.1, 0.8, 0.1], decoded.Probabilities);
        Assert.Equal(0, decoded.Slots[0].Selected);
        Assert.Equal(0.85, decoded.Slots[1].Confidence);
        Assert.Equal(1.0, decoded.Slots[2].Value);
        Assert.Equal(0.97, decoded.Slots[3].Value);
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void EdgesAndConfidencesAtTheirBoundsOrWithinTheToleranceAreKeptAsSent(string decoder)
    {
        var (plan, _) = BoxPlan();

        foreach (var (number, expected) in new[] { ("0", 0.0), ("1", 1.0), ("-0", -0.0), ("1e-300", 1e-300), ("1.0000001", 1.0000001), ("-0.0000009", -0.0000009), ("1e-1", 0.1) })
        {
            var decoded = Decode(decoder, BoxBody("{\"top\":" + number + ",\"bottom\":1,\"left\":0,\"right\":1}", number), plan);

            Assert.Equal(expected, decoded.Probabilities[0]);
            Assert.Equal(expected, decoded.Slots[0].Confidence);
        }
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void EdgesAndConfidencesOutsideTheirRangeAreRefused(string decoder)
    {
        var (plan, _) = BoxPlan();

        foreach (var number in new[] { "1.5", "-0.1", "1.000002", "-0.000002", "1e400", "-1e400" })
        {
            Refuse(decoder, BoxBody("{\"top\":" + number + ",\"bottom\":0.6,\"left\":0.2,\"right\":0.7}"), plan);
            Refuse(decoder, BoxBody("{\"top\":0.1,\"bottom\":0.6,\"left\":0.2,\"right\":" + number + "}"), plan);
            Refuse(decoder, BoxBody(Edges, number), plan);
        }
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void ABoxAnswerThatBreaksTheContractIsRefusedNotRepaired(string decoder)
    {
        var (plan, _) = BoxPlan();

        foreach (var answer in new[]
        {
            // A piece missing: an absent edge is not zero, and an absent confidence is not a null one.
            """{"type":"box","confidence":0.9}""",
            """{"type":"box","box":{"top":0.1,"bottom":0.6,"left":0.2,"right":0.7}}""",
            """{"type":"box","box":{"top":0.1,"bottom":0.6,"left":0.2},"confidence":0.9}""",
            """{"type":"box","box":{},"confidence":0.9}""",

            // A value of the wrong kind.
            """{"type":"box","box":{"top":"0.1","bottom":0.6,"left":0.2,"right":0.7},"confidence":0.9}""",
            """{"type":"box","box":{"top":null,"bottom":0.6,"left":0.2,"right":0.7},"confidence":0.9}""",
            """{"type":"box","box":[0.1,0.6,0.2,0.7],"confidence":0.9}""",
            """{"type":"box","box":null,"confidence":0.9}""",
            """{"type":"box","box":0.5,"confidence":0.9}""",
            """{"type":"box","box":{"top":0.1,"bottom":0.6,"left":0.2,"right":0.7},"confidence":"0.9"}""",
            """{"type":"box","box":{"top":0.1,"bottom":0.6,"left":0.2,"right":0.7},"confidence":true}""",
            """{"type":"box","box":{"top":0.1,"bottom":0.6,"left":0.2,"right":0.7},"confidence":{"value":0.9}}""",

            // A field given twice, in any spelling.
            """{"type":"box","box":{"top":0.1,"bottom":0.6,"left":0.2,"right":0.7,"top":0.2},"confidence":0.9}""",
            "{\"type\":\"box\",\"box\":{\"top\":0.1,\"bottom\":0.6,\"left\":0.2,\"right\":0.7,\"" + U("0074") + "op\":0.1},\"confidence\":0.9}",
            """{"type":"box","box":{"top":0.1,"bottom":0.6,"left":0.2,"right":0.7},"box":{"top":0.1,"bottom":0.6,"left":0.2,"right":0.7},"confidence":0.9}""",
            """{"type":"box","box":{"top":0.1,"bottom":0.6,"left":0.2,"right":0.7},"confidence":0.9,"confidence":null}""",
        })
        {
            Refuse(decoder, Body(answer), plan);
        }
    }

    [Fact]
    public void TheReaderSaysWhatIsWrongWithABox()
    {
        var (plan, _) = BoxPlan();

        Assert.Equal(JevProtocolError.MissingField, Refuse("reader", Body("""{"type":"box","confidence":0.9}"""), plan));
        Assert.Equal(JevProtocolError.MissingField, Refuse("reader", Body("""{"type":"box","box":{"top":0.1,"bottom":0.6,"left":0.2,"right":0.7}}"""), plan));
        Assert.Equal(JevProtocolError.MissingField, Refuse("reader", BoxBody("""{"top":0.1,"bottom":0.6,"right":0.7}"""), plan));
        Assert.Equal(JevProtocolError.NumberOutOfRange, Refuse("reader", BoxBody("""{"top":0.1,"bottom":1.5,"left":0.2,"right":0.7}"""), plan));
        Assert.Equal(JevProtocolError.NumberOutOfRange, Refuse("reader", BoxBody(Edges, "-0.5"), plan));
        Assert.Equal(JevProtocolError.NonFiniteNumber, Refuse("reader", BoxBody(Edges, "1e400"), plan));
        Assert.Equal(JevProtocolError.UnexpectedShape, Refuse("reader", BoxBody("""{"top":0.1,"bottom":"0.6","left":0.2,"right":0.7}"""), plan));
        Assert.Equal(JevProtocolError.UnexpectedShape, Refuse("reader", BoxBody("[0.1,0.6,0.2,0.7]"), plan));
        Assert.Equal(JevProtocolError.UnexpectedShape, Refuse("reader", BoxBody("null"), plan));
        Assert.Equal(JevProtocolError.DuplicateField, Refuse("reader", BoxBody("{\"top\":0.1,\"bottom\":0.6,\"left\":0.2,\"right\":0.7,\"" + U("0074") + "op\":0.1}"), plan));
        Assert.Equal(JevProtocolError.AnswerTypeMismatch, Refuse("reader", new SystemOneResponseBuilder().Noul("b", 0.5).Build(), plan));

        var noul = new JevDecisionPlanBuilder();
        noul.AddNoul("b", "q");
        Assert.Equal(JevProtocolError.AnswerTypeMismatch, Refuse("reader", BoxBody(Edges), noul.Build()));
    }

    [Theory]
    [MemberData(nameof(Decoders))]
    public void UnknownFieldsAreIgnoredAndOrderDoesNotMatter(string decoder)
    {
        var (plan, _) = BoxPlan();
        var answer = """{"confidence":0.5,"label":{"x":[1,null]},"box":{"right":0.7,"unit":"fraction","left":0.2,"bottom":0.6,"top":0.1},"type":"box"}""";

        var decoded = Decode(decoder, Body(answer), plan);

        Assert.Equal([0.1, 0.6, 0.2, 0.7], decoded.Probabilities);
        Assert.Equal(0.5, decoded.Slots[0].Confidence);
    }

    /// <summary>
    /// The two decoders are held to each other on box answers as on the others: every fixture is
    /// accepted by both or refused by both, and an accepted one decodes to the same values.
    /// </summary>
    [Fact]
    public void BothDecodersAgreeOnEveryBoxFixture()
    {
        var (plan, _) = MixedPlan();
        var accepted = 0;

        foreach (var answer in Corpus())
        {
            var body = Encoding.UTF8.GetBytes(MixedResponse().Raw("b", answer).Build());
            var reader = Outcome(() => SystemOneResponseReader.Read(body, plan, Tolerance));
            var reference = Outcome(() => ReferenceResponseDecoder.Decode(body, plan, Tolerance));

            Assert.True(reader.Accepted == reference.Accepted, $"The decoders disagree on {answer}: reader {reader.Accepted}, reference {reference.Accepted}.");

            if (reader.Accepted)
            {
                accepted++;
                Assert.Equal(reference.Decoded!.Probabilities, reader.Decoded!.Probabilities);

                for (var i = 0; i < plan.QuestionCount; i++)
                {
                    Assert.Equal(reference.Decoded.Slots[i].Selected, reader.Decoded.Slots[i].Selected);
                    Assert.Equal(reference.Decoded.Slots[i].Value, reader.Decoded.Slots[i].Value);
                    Assert.Equal(reference.Decoded.Slots[i].Confidence, reader.Decoded.Slots[i].Confidence);
                }
            }
        }

        // Both outcomes occur, so neither half of the comparison is vacuous.
        Assert.Equal(6, accepted);
    }

    [Fact]
    public void EdgesThatCrossAreReportedAndLeftAlone()
    {
        var (plan, _) = BoxPlan();
        var decoded = ReadBox(plan, 0.1, 0.6, 0.7, 0.5);

        var warning = Assert.Single(ConsistencyCheck.Run(plan, decoded, Tolerance, Tolerance, JevNumericalConsistency.Report, null));

        Assert.Equal(JevConsistencyCheck.BoxEdgeOrder, warning.Check);
        Assert.Equal("b", warning.QuestionId);
        Assert.Equal(0.2, warning.Deviation, 9);

        // Nothing swapped or clamped.
        Assert.Equal([0.1, 0.6, 0.7, 0.5], decoded.Probabilities);
    }

    [Fact]
    public void TheDeviationIsTheFurtherOfTheTwoCrossings()
    {
        var (plan, _) = BoxPlan();

        // Top past bottom by 0.3, left past right by 0.1.
        var warning = Assert.Single(ConsistencyCheck.Run(plan, ReadBox(plan, 0.6, 0.3, 0.3, 0.2), Tolerance, Tolerance, JevNumericalConsistency.Report, null));

        Assert.Equal(0.3, warning.Deviation, 9);
    }

    [Fact]
    public void EdgesThatMeetOrCrossWithinTheToleranceAreNotReported()
    {
        var (plan, _) = BoxPlan();

        // A box of no width or height: its edges meet but do not cross, even with no tolerance.
        Assert.Empty(ConsistencyCheck.Run(plan, ReadBox(plan, 0.5, 0.5, 0.5, 0.5), 0, 0, JevNumericalConsistency.Report, null));

        // Crossing by less than the tolerance; with none, the same box is reported.
        var hairsbreadth = ReadBox(plan, 0.1, 0.6, 0.5000005, 0.5);
        Assert.Empty(ConsistencyCheck.Run(plan, hairsbreadth, Tolerance, Tolerance, JevNumericalConsistency.Report, null));
        Assert.Equal(JevConsistencyCheck.BoxEdgeOrder, Assert.Single(ConsistencyCheck.Run(plan, hairsbreadth, 0, 0, JevNumericalConsistency.Report, null)).Check);
    }

    [Fact]
    public void EdgesAreNotADistributionAndAreNotSummed()
    {
        // The edges sum to 1.6; only the distributions on either side of them are checked.
        var (plan, _) = MixedPlan();
        var decoded = SystemOneResponseReader.Read(Encoding.UTF8.GetBytes(MixedResponse().Box("b", 0.1, 0.6, 0.2, 0.7, 0.85).Build()), plan, Tolerance);

        Assert.Empty(ConsistencyCheck.Run(plan, decoded, Tolerance, Tolerance, JevNumericalConsistency.Report, null));
    }

    [Fact]
    public void StrictModeRefusesABoxWhoseEdgesCross()
    {
        var (plan, _) = BoxPlan();

        var exception = Assert.Throws<JevProtocolException>(() => ConsistencyCheck.Run(plan, ReadBox(plan, 0.1, 0.6, 0.7, 0.5), Tolerance, Tolerance, JevNumericalConsistency.Strict, "req_x"));

        Assert.Equal(JevProtocolError.NumericalInconsistency, exception.Error);
        Assert.Equal("req_x", exception.RequestId);
    }

    [Fact]
    public void ABoxAnswerIsReadThroughItsHandle()
    {
        var (plan, box) = MixedPlan();
        var answer = Result(plan, MixedResponse().Box("b", 0.1, 0.6, 0.2, 0.7, 0.85).Build()).Get(box);

        Assert.Equal(0.1, answer.Top);
        Assert.Equal(0.6, answer.Bottom);
        Assert.Equal(0.2, answer.Left);
        Assert.Equal(0.7, answer.Right);
        Assert.Equal(0.85, answer.Confidence);
        Assert.Equal("BoxAnswer", answer.ToString());
    }

    [Fact]
    public void ANullConfidenceStaysNullOnTheAnswer()
    {
        var (plan, box) = BoxPlan();

        Assert.Null(Result(plan, new SystemOneResponseBuilder().Box("b", 0.1, 0.6, 0.2, 0.7, null).Build()).Get(box).Confidence);
    }

    [Fact]
    public void ABoxHandleFromAnotherPlanOrADefaultOneIsRefused()
    {
        var (plan, _) = BoxPlan();
        var (_, other) = BoxPlan();
        var result = Result(plan, new SystemOneResponseBuilder().Box("b", 0.1, 0.6, 0.2, 0.7, 0.85).Build());

        Assert.Throws<InvalidOperationException>(() => result.Get(other));
        Assert.Throws<InvalidOperationException>(() => result.Get(default(JevBoxHandle)));
    }

    /// <summary>Box answers to put in <see cref="MixedResponse"/>, accepted and refused.</summary>
    private static IEnumerable<string> Corpus()
    {
        // Accepted.
        yield return """{"type":"box","box":{"top":0.1,"bottom":0.6,"left":0.2,"right":0.7},"confidence":0.85}""";
        yield return """{"type":"box","box":{"top":0.1,"bottom":0.6,"left":0.2,"right":0.7},"confidence":null}""";
        yield return """{"type":"box","box":{"top":0.7,"bottom":0.2,"left":0.6,"right":0.1},"confidence":0}""";
        yield return """{"type":"box","box":{"top":0,"bottom":1,"left":-0.0000009,"right":1.0000001},"confidence":1}""";
        yield return """{"type":"box","box":{"top":1e-1,"bottom":6E-1,"left":0.2,"right":0.7},"confidence":0.5}""";
        yield return """{"confidence":0.5,"extra":[1],"box":{"right":0.7,"unit":"fraction","left":0.2,"bottom":0.6,"top":0.1},"type":"box"}""";

        // Refused.
        yield return """{"type":"box","box":{"top":0.1,"bottom":1.5,"left":0.2,"right":0.7},"confidence":0.85}""";
        yield return """{"type":"box","box":{"top":0.1,"bottom":0.6,"left":0.2,"right":1e400},"confidence":0.85}""";
        yield return """{"type":"box","box":{"top":0.1,"bottom":0.6,"left":0.2,"right":0.7},"confidence":1e400}""";
        yield return """{"type":"box","box":{"top":0.1,"bottom":0.6,"left":0.2,"right":0.7},"confidence":-0.5}""";
        yield return """{"type":"box","box":{"top":"0.1","bottom":0.6,"left":0.2,"right":0.7},"confidence":0.85}""";
        yield return """{"type":"box","box":{"top":null,"bottom":0.6,"left":0.2,"right":0.7},"confidence":0.85}""";
        yield return """{"type":"box","box":[0.1,0.6,0.2,0.7],"confidence":0.85}""";
        yield return """{"type":"box","box":null,"confidence":0.85}""";
        yield return """{"type":"box","box":{"top":0.1,"bottom":0.6,"left":0.2},"confidence":0.85}""";
        yield return """{"type":"box","box":{"top":0.1,"bottom":0.6,"left":0.2,"right":0.7}}""";
        yield return """{"type":"box","confidence":0.85}""";
        yield return "{\"type\":\"box\",\"box\":{\"top\":0.1,\"bottom\":0.6,\"left\":0.2,\"right\":0.7,\"" + U("0074") + "op\":0.1},\"confidence\":0.85}";
        yield return """{"type":"box","box":{"top":0.1,"bottom":0.6,"left":0.2,"right":0.7},"confidence":0.85,"confidence":0.5}""";
        yield return "{\"type\":\"box\",\"box\":{\"a" + U("d800") + "\":1,\"top\":0.1,\"bottom\":0.6,\"left\":0.2,\"right\":0.7},\"confidence\":0.85}";
        yield return """{"box":{"top":0.1,"bottom":0.6,"left":0.2,"right":0.7},"confidence":0.85}""";
        yield return """{"type":"noul","box":{"top":0.1,"bottom":0.6,"left":0.2,"right":0.7},"confidence":0.85}""";
        yield return """{"type":"box","noul":0.5}""";
    }

    /// <summary>A backslash-u escape, written without a backslash-u in this source file.</summary>
    private static string U(string hex) => (char)92 + "u" + hex;

    private static Decoder Get(string name) => name == "reader"
        ? SystemOneResponseReader.Read
        : ReferenceResponseDecoder.Decode;

    private static DecodedResponse Decode(string decoder, string json, JevDecisionPlan plan)
        => Get(decoder)(Encoding.UTF8.GetBytes(json), plan, Tolerance);

    private static JevProtocolError Refuse(string decoder, string json, JevDecisionPlan plan)
        => Assert.Throws<JevProtocolException>(() => Decode(decoder, json, plan)).Error;

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

    private static (JevDecisionPlan Plan, JevBoxHandle Box) BoxPlan()
    {
        var builder = new JevDecisionPlanBuilder();
        var box = builder.AddBox("b", "the black square");
        return (builder.Build(), box);
    }

    /// <summary>A box between a choice and a score, so that its edges sit between two distributions.</summary>
    private static (JevDecisionPlan Plan, JevBoxHandle Box) MixedPlan()
    {
        var builder = new JevDecisionPlanBuilder();
        builder.AddChoice<bool>("c", "Is there a square?", [new(true, "yes"), new(false, "no")]);
        var box = builder.AddBox("b", "the black square");
        builder.AddScore("s", "How large is it?", ["small", "medium", "large"]);
        builder.AddNoul("n", "Is it in the top half?");
        return (builder.Build(), box);
    }

    /// <summary>The answers to <see cref="MixedPlan"/> other than the box, their numbers all agreeing with each other.</summary>
    private static SystemOneResponseBuilder MixedResponse() => new SystemOneResponseBuilder()
        .Choice("c", "yes", 0.9, ("yes", 0.95), ("no", 0.05))
        .Score("s", 1.0, 0.8, ["small", "medium", "large"], 0.1, 0.8, 0.1)
        .Noul("n", 0.97);

    /// <summary>A response with one answer, to question <c>b</c>, written verbatim.</summary>
    private static string Body(string answer) => new SystemOneResponseBuilder().Raw("b", answer).Build();

    /// <summary>A response with one box answer, its box and confidence written verbatim.</summary>
    private static string BoxBody(string box, string confidence = "0.9")
        => Body("{\"type\":\"box\",\"box\":" + box + ",\"confidence\":" + confidence + "}");

    private static DecodedResponse ReadBox(JevDecisionPlan plan, double top, double bottom, double left, double right)
        => SystemOneResponseReader.Read(Encoding.UTF8.GetBytes(new SystemOneResponseBuilder().Box("b", top, bottom, left, right, 0.5).Build()), plan, Tolerance);

    private static JevResult Result(JevDecisionPlan plan, string body)
    {
        var decoded = SystemOneResponseReader.Read(Encoding.UTF8.GetBytes(body), plan, Tolerance);
        var metadata = new JevResultMetadata(FakeResponses.RequestId, 1, new Version(2, 0), TimeSpan.FromMilliseconds(5), []);
        return new JevResult(plan, "jev-latest", decoded, metadata, null);
    }
}
