using System.Text;
using Kkdev92.Jev.Decoding;
using Kkdev92.Jev.TestSupport;

namespace Kkdev92.Jev.Tests;

public sealed class ConsistencyCheckTests
{
    private const double Tolerance = 1e-6;

    private static (JevDecisionPlan Plan, DecodedResponse Decoded) Decode(string answerId, string answer, Action<JevDecisionPlanBuilder> build)
    {
        var builder = new JevDecisionPlanBuilder();
        build(builder);
        var plan = builder.Build();
        var body = new SystemOneResponseBuilder().Raw(answerId, answer).Build();
        return (plan, SystemOneResponseReader.Read(Encoding.UTF8.GetBytes(body), plan, Tolerance));
    }

    private static void Choice(JevDecisionPlanBuilder b) => b.AddChoice<string>("c", "q", [new("a", "a"), new("b", "b"), new("c", "c")]);

    private static void Score(JevDecisionPlanBuilder b) => b.AddScore("s", "q", ["0", "1", "2"]);

    [Fact]
    public void AConsistentAnswerProducesNoWarning()
    {
        var (plan, decoded) = Decode("c", """{"type":"choice","choice":"b","confidence":0.5,"probabilities":{"a":0.2,"b":0.5,"c":0.3}}""", Choice);
        Assert.Empty(ConsistencyCheck.Run(plan, decoded, Tolerance, Tolerance, JevNumericalConsistency.Report, null));
    }

    [Fact]
    public void AFloatingPointHairsbreadthIsNotAWarning()
    {
        // 0.1 + 0.2 + 0.7 is not exactly 1 in binary floating point.
        var (plan, decoded) = Decode("c", """{"type":"choice","choice":"c","confidence":0.5,"probabilities":{"a":0.1,"b":0.2,"c":0.7}}""", Choice);
        Assert.Empty(ConsistencyCheck.Run(plan, decoded, Tolerance, Tolerance, JevNumericalConsistency.Report, null));
    }

    [Fact]
    public void ADistributionThatDoesNotSumToOneIsReportedAndLeftAlone()
    {
        var (plan, decoded) = Decode("c", """{"type":"choice","choice":"b","confidence":0.5,"probabilities":{"a":0.2,"b":0.5,"c":0.2}}""", Choice);

        var warning = Assert.Single(ConsistencyCheck.Run(plan, decoded, Tolerance, Tolerance, JevNumericalConsistency.Report, null));

        Assert.Equal(JevConsistencyCheck.ProbabilitySum, warning.Check);
        Assert.Equal("c", warning.QuestionId);
        Assert.Equal(-0.1, warning.Deviation, 9);

        // Nothing renormalised.
        Assert.Equal([0.2, 0.5, 0.2], decoded.Probabilities);
    }

    [Fact]
    public void AChoiceThatIsNotTheMostProbableOptionIsReported()
    {
        var (plan, decoded) = Decode("c", """{"type":"choice","choice":"a","confidence":0.5,"probabilities":{"a":0.2,"b":0.5,"c":0.3}}""", Choice);

        var warning = Assert.Single(ConsistencyCheck.Run(plan, decoded, Tolerance, Tolerance, JevNumericalConsistency.Report, null));
        Assert.Equal(JevConsistencyCheck.ChoiceIsMostProbable, warning.Check);
    }

    [Fact]
    public void ATieAtTheTopIsConsistentWithEitherChoice()
    {
        var (plan, decoded) = Decode("c", """{"type":"choice","choice":"a","confidence":0.5,"probabilities":{"a":0.4,"b":0.4,"c":0.2}}""", Choice);
        Assert.Empty(ConsistencyCheck.Run(plan, decoded, Tolerance, Tolerance, JevNumericalConsistency.Report, null));
    }

    [Fact]
    public void AScoreThatIsNotTheExpectationOfItsLevelsIsReportedAndKept()
    {
        // 0*0 + 1*0.57 + 2*0.43 = 1.43; the answer says 1.5.
        var (plan, decoded) = Decode("s", """{"type":"score","score":1.5,"confidence":0.35,"legend":{"0":"a","1":"b","2":"c"},"probabilities":{"0":0.0,"1":0.57,"2":0.43}}""", Score);

        var warning = Assert.Single(ConsistencyCheck.Run(plan, decoded, Tolerance, Tolerance, JevNumericalConsistency.Report, null));

        Assert.Equal(JevConsistencyCheck.ScoreIsExpectation, warning.Check);
        Assert.Equal(0.07, warning.Deviation, 9);
        Assert.Equal(1.5, decoded.Slots[0].Value);
    }

    [Fact]
    public void TheDocumentationsOwnExampleIsConsistent()
    {
        // From https://docs.typesafe.ai/primitives/score: score 1.43 with probabilities 0, 0.57, 0.43.
        var (plan, decoded) = Decode("s", """{"type":"score","score":1.43,"confidence":0.35,"legend":{"0":"a","1":"b","2":"c"},"probabilities":{"0":0.0,"1":0.57,"2":0.43}}""", Score);
        Assert.Empty(ConsistencyCheck.Run(plan, decoded, Tolerance, Tolerance, JevNumericalConsistency.Report, null));
    }

    [Fact]
    public void StrictModeRefusesInsteadOfReporting()
    {
        var (plan, decoded) = Decode("c", """{"type":"choice","choice":"b","confidence":0.5,"probabilities":{"a":0.2,"b":0.5,"c":0.2}}""", Choice);

        var exception = Assert.Throws<JevProtocolException>(() => ConsistencyCheck.Run(plan, decoded, Tolerance, Tolerance, JevNumericalConsistency.Strict, "req_x"));

        Assert.Equal(JevProtocolError.NumericalInconsistency, exception.Error);
        Assert.Equal("req_x", exception.RequestId);
    }

    [Fact]
    public void AWiderToleranceAcceptsCoarselyRoundedProbabilities()
    {
        // Three options rounded to two places each: 0.33 * 3 = 0.99.
        var (plan, decoded) = Decode("c", """{"type":"choice","choice":"a","confidence":0.0,"probabilities":{"a":0.33,"b":0.33,"c":0.33}}""", Choice);

        Assert.NotEmpty(ConsistencyCheck.Run(plan, decoded, Tolerance, Tolerance, JevNumericalConsistency.Report, null));
        Assert.Empty(ConsistencyCheck.Run(plan, decoded, 0.02, 0, JevNumericalConsistency.Report, null));
    }
}
