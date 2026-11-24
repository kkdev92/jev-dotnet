using System.Net;
using Kkdev92.Jev.Extensions.AI;
using Kkdev92.Jev.Extensions.AI.Evaluation;
using Kkdev92.Jev.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace Kkdev92.Jev.Tests;

public sealed class EvaluatorTests
{
    [Theory]
    [InlineData("judge", JevAdapterFailureReason.EvaluationUnavailable)]
    [InlineData("projection", JevAdapterFailureReason.InvalidProjection)]
    [InlineData("metric", JevAdapterFailureReason.InvalidProjection)]
    public async Task ForeignCapturedHttpFailureNeverBecomesAMetricOrLeaksTheOriginalException(string origin, JevAdapterFailureReason reason)
    {
        var (_, _, plan, handle) = ChatAdapterTests.Judge();
        using var foreignHttp = new HttpClient(FakeHttpMessageHandler.Always("private-sentinel", HttpStatusCode.ServiceUnavailable));
        var foreignClient = new JevClient(foreignHttp, new() { AuthenticationMode = JevAuthenticationMode.None, CaptureErrorBody = true });
        var token = TestContext.Current.CancellationToken;
        var foreign = await Assert.ThrowsAsync<JevHttpException>(() => foreignClient.EvaluateAsync("text", plan, cancellationToken: token));
        Assert.NotNull(foreign.ErrorBody);
        foreign.Data["private-sentinel"] = "private-sentinel";
        var handler = origin == "judge" ? FakeHttpMessageHandler.Sequence(() => throw foreign)
            : FakeHttpMessageHandler.Always(new SystemOneResponseBuilder().Noul("q", 0.75).Build());
        using var http = new HttpClient(handler);
        var judge = new JevClient(http, new() { AuthenticationMode = JevAuthenticationMode.None });
        var evaluator = new JevEvaluator<double>(judge, new(plan, result => origin == "projection" ? throw foreign : result.Get(handle).Probability),
            [JevMetric.Numeric<double>("score", p => origin == "metric" ? throw foreign : p)],
            new() { FailureBehavior = JevEvaluationFailureBehavior.ReturnFailedMetrics });
        var ex = await Assert.ThrowsAnyAsync<JevAdapterException>(() => evaluator.EvaluateAsync([new(ChatRole.User, "ticket")],
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "reply")), cancellationToken: token).AsTask());
        Assert.Equal(reason, ex.ReasonCode);
        Assert.Equal(origin == "judge" ? JevAdapterStage.Evaluation : JevAdapterStage.ResultProjection, ex.Stage);
        Assert.Null(ex.InnerException);
        Assert.Empty(ex.Data);
        Assert.DoesNotContain("private-sentinel", ex.ToString(), StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneJudgmentCreatesEveryMetricAndOnlyCopiesExplicitValues(bool unavailable)
    {
        var (_, _, plan, handle) = ChatAdapterTests.Judge();
        var handler = unavailable ? FakeHttpMessageHandler.Always("private-response", HttpStatusCode.ServiceUnavailable)
            : FakeHttpMessageHandler.Always(new SystemOneResponseBuilder().Noul("q", 0.875).Build());
        var judge = new JevClient(new HttpClient(handler), new() { AuthenticationMode = JevAuthenticationMode.None, CaptureErrorBody = true });
        var evaluator = new JevEvaluator<double>(judge, new(plan, r => r.Get(handle).Probability),
            [JevMetric.Numeric<double>("confidence", p => p, _ => new(reason: "private-reason")), JevMetric.Boolean<double>("eligible", p => p > 0.5)],
            new() { FailureBehavior = JevEvaluationFailureBehavior.ReturnFailedMetrics });
        var results = await evaluator.EvaluateAsync([new(ChatRole.User, "question")], new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")),
            cancellationToken: TestContext.Current.CancellationToken);
        var metric = results.Get<NumericMetric>("confidence");
        var flag = results.Get<BooleanMetric>("eligible");
        Assert.Single(handler.Requests);
        Assert.Equal(2, results.Metrics.Count);
        Assert.Null(metric.Reason);
        Assert.Null(metric.Interpretation?.Reason);
        Assert.Null(metric.Metadata);
        if (unavailable)
        {
            Assert.Null(metric.Value);
            Assert.Null(flag.Value);
            Assert.True(metric.Interpretation?.Failed);
            Assert.Equal("JEV_EVALUATION_UNAVAILABLE", Assert.Single(metric.Diagnostics!).Message);
        }
        else
        {
            Assert.Equal(0.875, metric.Value);
            Assert.True(flag.Value);
            Assert.False(metric.Interpretation?.Failed);
        }
    }
}
