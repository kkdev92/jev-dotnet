using System.Text.Json;
using System.Text.Json.Serialization;
using Kkdev92.Jev;
using Kkdev92.Jev.AiAotSmokeTests;
using Kkdev92.Jev.DependencyInjection;
using Kkdev92.Jev.Extensions.AI;
using Kkdev92.Jev.Extensions.AI.Evaluation;
using Kkdev92.Jev.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.DependencyInjection;

if (!await TicketGuardScenario.RunAsync()) { return 1; }
var builder = new JevDecisionPlanBuilder();
var urgency = builder.AddScore<int>("Rate urgency", [new(10, "low"), new(90, "high")]);
var plan = builder.Build();
var response = new SystemOneResponseBuilder().Score("q0", 0.75, 0.5, ["low", "high"], 0.25, 0.75).Build();
var handler = FakeHttpMessageHandler.Always(response);
var services = new ServiceCollection();
services.AddJev(o => o.AuthenticationMode = JevAuthenticationMode.None).ConfigurePrimaryHttpMessageHandler(() => handler);
using var provider = services.BuildServiceProvider();
var judge = provider.GetRequiredService<JevClient>();
var projection = new JevProjection<double>(plan, r => r.GetScore(urgency).Value);
var function = JevAIFunction.Create(judge, projection, SmokeJson.Default.Double,
    JsonElement.Parse("""{"type":"number"}"""), "rate_urgency", "Estimate ticket urgency.");
var functionValue = await function.InvokeAsync(new AIFunctionArguments { ["state"] = "ticket" });
if (functionValue is not JsonElement { ValueKind: JsonValueKind.Number } element || element.GetDouble() != 0.75) { return 1; }

IEvaluator evaluator = new JevEvaluator<double>(judge, projection,
    [JevMetric.Numeric<double>("urgency", value => value), JevMetric.Boolean<double>("review", value => value >= 0.5)]);
var metrics = await evaluator.EvaluateAsync([new(ChatRole.User, "question")], new ChatResponse(new ChatMessage(ChatRole.Assistant, "reply")));
if (metrics.Get<NumericMetric>("urgency").Value != 0.75 || metrics.Get<BooleanMetric>("review").Value != true || handler.Requests.Count != 2) { return 1; }
Console.WriteLine("AI guard, routing, buffered stream, decision function and evaluation passed");
return 0;

[JsonSerializable(typeof(double))]
internal sealed partial class SmokeJson : JsonSerializerContext;
