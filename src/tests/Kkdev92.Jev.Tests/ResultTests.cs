using System.Text;
using System.Text.Json;
using Kkdev92.Jev.Decoding;
using Kkdev92.Jev.TestSupport;
using static Kkdev92.Jev.Tests.TestPlans;

namespace Kkdev92.Jev.Tests;

public sealed class ResultTests
{
    private static JevResult Result(JevDecisionPlan plan, string body)
    {
        var decoded = SystemOneResponseReader.Read(Encoding.UTF8.GetBytes(body), plan, 1e-6);
        var metadata = new JevResultMetadata(FakeResponses.RequestId, 1, new Version(2, 0), TimeSpan.FromMilliseconds(5), []);
        return new JevResult(plan, "jev-latest", decoded, metadata, null);
    }

    [Fact]
    public void AnswersAreReadThroughTypedHandlesAndMapBackToTheCallersValues()
    {
        var (plan, department, frustration, urgent) = Triage();
        var result = Result(plan, TriageResponse());

        var choice = result.Get(department);
        Assert.Equal(Department.Billing, choice.Value);
        Assert.Equal("billing", choice.Label);
        Assert.Equal(0, choice.Index);
        Assert.Equal(0.81, choice.Confidence);
        Assert.Equal(3, choice.OptionCount);
        Assert.Equal([0.88, 0.12, 0.0], choice.Probabilities.ToArray());
        Assert.Equal(0.12, choice.GetProbability("technical"));
        Assert.Equal(0.12, choice.GetProbability(1));
        Assert.Equal(Department.Sales, choice.GetValue(2));
        Assert.Equal("sales", choice.GetLabel(2));

        var score = result.Get(frustration);
        Assert.Equal(1.05, score.Value);
        Assert.Equal(0.92, score.Confidence);
        Assert.Equal(3, score.LevelCount);
        Assert.Equal(0.95, score.GetProbability(1));
        Assert.Equal("Frustrated", score.Legend[1].GetString());

        Assert.Equal(0.95, result.Get(urgent).Probability);

        Assert.Equal("jev-latest", result.RequestedModel);
        Assert.Equal("jev-1.13.0", result.ActualModel);
        Assert.Equal(new JevUsage(120, 12), result.Usage);
        Assert.Equal(FakeResponses.RequestId, result.Metadata.RequestId);
        Assert.Null(result.RawResponseBody);
    }

    [Fact]
    public void AHandleFromAnotherPlanIsRefusedEvenWithTheSameIds()
    {
        var (plan, _, _, _) = Triage();
        var (_, otherDepartment, otherFrustration, otherUrgent) = Triage();
        var result = Result(plan, TriageResponse());

        Assert.Throws<InvalidOperationException>(() => result.Get(otherDepartment));
        Assert.Throws<InvalidOperationException>(() => result.Get(otherFrustration));
        Assert.Throws<InvalidOperationException>(() => result.Get(otherUrgent));
    }

    [Fact]
    public void ADefaultHandleIsRefused()
    {
        var (plan, _, _, _) = Triage();
        var result = Result(plan, TriageResponse());

        Assert.Throws<InvalidOperationException>(() => result.Get(default(JevChoiceHandle<Department>)));
        Assert.Throws<InvalidOperationException>(() => result.Get(default(JevScoreHandle)));
        Assert.Throws<InvalidOperationException>(() => result.Get(default(JevNoulHandle)));
    }

    [Fact]
    public void ADefaultAnswerRefusesToBeRead()
    {
        Assert.Throws<InvalidOperationException>(() => default(ChoiceAnswer<int>).Value);
        Assert.Throws<InvalidOperationException>(() => default(ChoiceAnswer<int>).Confidence);
        Assert.Throws<InvalidOperationException>(() => default(ScoreAnswer).Value);
        Assert.Equal("ChoiceAnswer (default)", default(ChoiceAnswer<int>).ToString());
    }

    [Fact]
    public void OutOfRangeLookupsAreArgumentErrors()
    {
        var (plan, department, frustration, _) = Triage();
        var result = Result(plan, TriageResponse());

        Assert.Throws<ArgumentOutOfRangeException>(() => result.Get(department).GetProbability(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => result.Get(department).GetProbability(-1));
        Assert.Throws<ArgumentException>(() => result.Get(department).GetProbability("Billing"));
        Assert.Throws<ArgumentOutOfRangeException>(() => result.Get(frustration).GetProbability(3));
    }

    [Fact]
    public void ReferenceTypeValuesAreReturnedAsTheSameInstance()
    {
        var value = new object();
        var builder = new JevDecisionPlanBuilder();
        var handle = builder.AddChoice<object>("c", "q", [new(value, "a"), new(new object(), "b")]);
        var plan = builder.Build();

        var result = Result(plan, new SystemOneResponseBuilder().Choice("c", "a", 1, ("a", 1), ("b", 0)).Build());

        Assert.Same(value, result.Get(handle).Value);
    }

    [Fact]
    public void ALegendOutlivesTheBufferItWasReadFrom()
    {
        var (plan, _, frustration, _) = Triage();
        var body = Encoding.UTF8.GetBytes(TriageResponse());
        var decoded = SystemOneResponseReader.Read(body, plan, 1e-6);

        body.AsSpan().Clear();

        var result = new JevResult(plan, "m", decoded, default, null);
        Assert.Equal("Calm", result.Get(frustration).Legend[0].GetString());
    }

    [Fact]
    public void NothingThatHoldsAnAnswerPrintsIt()
    {
        var (plan, department, frustration, urgent) = Triage();
        var result = Result(plan, TriageResponse());

        Assert.Equal("JevResult (3 answers from jev-1.13.0)", result.ToString());
        Assert.Equal("ChoiceAnswer (3 options)", result.Get(department).ToString());
        Assert.Equal("ScoreAnswer (3 levels)", result.Get(frustration).ToString());
        Assert.Equal("NoulAnswer", result.Get(urgent).ToString());
    }

    [Fact]
    public void ALegendValueIsIndependentJson()
    {
        var (plan, _, frustration, _) = Triage();
        var result = Result(plan, TriageResponse());

        Assert.Equal(JsonValueKind.String, result.Get(frustration).Legend[2].ValueKind);
        Assert.Equal("\"Very angry\"", result.Get(frustration).Legend[2].GetRawText());
    }
}
