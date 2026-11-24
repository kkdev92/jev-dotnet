using Kkdev92.Jev;
using Kkdev92.Jev.Extensions.AI.Evaluation;
using Kkdev92.Jev.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;

// Reporting persists the synthetic conversation and host-selected metric values in this directory.
if (args.Length != 1)
{
    Console.Error.WriteLine("Supply the output directory for the synthetic evaluation report.");
    return 2;
}

var questions = new JevDecisionPlanBuilder();
var answered = questions.AddNoul("answered", "Does the reply address the billing question?");
var response = new SystemOneResponseBuilder().Noul("answered", 0.9).Build();
using var http = new HttpClient(FakeHttpMessageHandler.Always(response)) { Timeout = Timeout.InfiniteTimeSpan };
var judge = new JevClient(http, new JevClientOptions { AuthenticationMode = JevAuthenticationMode.None });
var evaluator = new JevEvaluator<double>(judge, new(questions.Build(), result => result.Get(answered).Probability),
    [JevMetric.Numeric<double>("acknowledged", value => value)]);
var reporting = DiskBasedReportingConfiguration.Create(args[0], [evaluator], enableResponseCaching: false);
await using var scenario = await reporting.CreateScenarioRunAsync("SyntheticBillingTicket");
var result = await scenario.EvaluateAsync([new ChatMessage(ChatRole.User, "Please check the second charge.")],
    new ChatResponse(new ChatMessage(ChatRole.Assistant, "I will review the second charge.")));
return result.Get<NumericMetric>("acknowledged").Value == 0.9 ? 0 : 1;
