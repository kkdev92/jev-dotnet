using Kkdev92.Jev.TestSupport;

namespace Kkdev92.Jev.Tests;

public sealed class TypedScoreTests
{
    [Fact]
    public async Task ExplicitOrderOwnsTheLegendAndLeavesExpectedScoreContinuous()
    {
        var builder = new JevDecisionPlanBuilder();
        JevScoreLevel<int>[] levels = [new(90, "first"), new(10, "second"), new(30, "third")];
        Assert.Throws<ArgumentException>(() => builder.AddScore<int>("rate", [levels[0], levels[0]]));
        var handle = builder.AddScore<int>("rate", levels);
        levels[0] = new(99, "replacement");
        Assert.Equal("q0", handle.Id);
        var plan = builder.Build();
        var handler = FakeHttpMessageHandler.Always(new SystemOneResponseBuilder().Score("q0", 1.03, 0.5,
            ["first", "second", "third"], 0.45, 0.1, 0.45).Build());
        var client = new JevClient(new HttpClient(handler), new() { AuthenticationMode = JevAuthenticationMode.None });
        var result = await client.EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken);
        var score = result.GetScore(handle);
        Assert.Equal(1.03, score.Value);
        Assert.Equal(90, score.MostLikely);
        Assert.Equal(0.45, score.GetProbabilityOf(30));
        Assert.Equal(10, score.GetLevel(1));
        Assert.Throws<ArgumentException>(() => score.GetProbabilityOf(99));
        Assert.Throws<ArgumentOutOfRangeException>(() => score.GetLevel(3));
        Assert.Throws<InvalidOperationException>(() => result.Get<int>(default));
        Assert.Throws<InvalidOperationException>(() => result.GetScore<int>(default));
        Assert.Throws<InvalidOperationException>(() => default(ScoreAnswer<int>).Raw);
    }
}
