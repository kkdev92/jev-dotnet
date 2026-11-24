using Kkdev92.Jev.TestSupport;

namespace Kkdev92.Jev.ContractTests;

public sealed class LocalEndpointContractTests
{
    // Response examples published for two other servers of the same API, retrieved 2026-11-24.
    // The numbers and their formatting are exactly as published; only the model names are
    // replaced. Offline decoder checks: no model runs here.
    //
    // The first serves without a key, and its probabilities sum to 0.9999.
    private const string RoundedChoiceExample = """
        {"model":"local-model","answers":{"label":{"type":"choice","choice":"bug","probabilities":{"billing":0.0125,"bug":0.9781,"account":0.0093},"confidence":0.8906}},"usage":{"input_tokens":174,"output_tokens":1}}
        """;

    // The second answers a Choice, a Noul and a Score in one response.
    private const string ThreeKindsExample = """
        {"model":"multilingual-model","answers":{
          "team":{"type":"choice","choice":"billing","confidence":1.0,"probabilities":{"billing":1.0,"technical":0.0,"sales":0.0,"other":0.0}},
          "refund":{"type":"noul","noul":0.9794},
          "urgency":{"type":"score","score":1.9005,"confidence":0.698,"legend":{"0":"not urgent","1":"soon","2":"today"},"probabilities":{"0":0.004,"1":0.0915,"2":0.9045}}
        },"usage":{"input_tokens":172,"output_tokens":0}}
        """;

    [Theory]
    [InlineData(JevNumericalConsistency.Report)]
    [InlineData(JevNumericalConsistency.Strict)]
    public async Task PublishedExamplesKeepTheReportedNumbers(JevNumericalConsistency consistency)
    {
        var token = TestContext.Current.CancellationToken;
        var builder = new JevDecisionPlanBuilder();
        var label = builder.AddChoice<int>("label", "Classify", [new(0, "billing"), new(1, "bug"), new(2, "account")]);
        var call = Client(RoundedChoiceExample, consistency, keyless: true).EvaluateAsync("text", builder.Build(), cancellationToken: token);
        if (consistency == JevNumericalConsistency.Strict)
        {
            var error = await Assert.ThrowsAsync<JevProtocolException>(() => call);
            Assert.Equal(JevProtocolError.NumericalInconsistency, error.Error);
        }
        else
        {
            var result = await call;
            Assert.Equal(0.8906, result.Get(label).Confidence);
            Assert.Equal(0.9781, result.Get(label).GetProbability("bug"));
            Assert.Equal(JevConsistencyCheck.ProbabilitySum, Assert.Single(result.Metadata.Warnings).Check);
        }

        builder = new JevDecisionPlanBuilder();
        var team = builder.AddChoice<int>("team", "Classify", [new(0, "billing"), new(1, "technical"), new(2, "sales"), new(3, "other")]);
        var refund = builder.AddNoul("refund", "Refund requested?");
        var urgency = builder.AddScore("urgency", "Rate urgency", ["not urgent", "soon", "today"]);
        var response = await Client(ThreeKindsExample, consistency).EvaluateAsync("text", builder.Build(), cancellationToken: token);
        Assert.Empty(response.Metadata.Warnings);
        Assert.Equal("multilingual-model", response.ActualModel);
        Assert.Equal(0, response.Usage.OutputTokens);
        Assert.Equal("billing", response.Get(team).Label);
        Assert.Equal(0.9794, response.Get(refund).Probability);
        Assert.Equal(1.9005, response.Get(urgency).Value);
    }

    [Fact]
    public async Task ALocalCatalogDoesNotBecomeATypeSafeModelList()
    {
        var client = Client("""{"object":"list","data":[{"id":"local-model","object":"model","created":0}]}""", JevNumericalConsistency.Report, keyless: true);
        await Assert.ThrowsAsync<JevProtocolException>(() => client.GetModelsAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    private static JevClient Client(string json, JevNumericalConsistency consistency, bool keyless = false)
        => Clients.Create(FakeHttpMessageHandler.Always(json), o =>
        {
            o.BaseAddress = new Uri("http://127.0.0.1:8080");
            o.AllowLoopbackHttp = true;
            o.DefaultModel = "local-model";
            o.NumericalConsistency = consistency;
            if (keyless)
            {
                o.AuthenticationMode = JevAuthenticationMode.None;
                o.Credential = null;
            }
        });
}
