using Kkdev92.Jev.Extensions.AI;
using Kkdev92.Jev.TestSupport;
using Microsoft.Extensions.AI;

namespace Kkdev92.Jev.AiAotSmokeTests;

internal static class TicketGuardScenario
{
    public static async Task<bool> RunAsync()
    {
        var builder = new JevDecisionPlanBuilder();
        var department = builder.AddChoice<int>("department", "Which department?", [new(1, "billing"), new(2, "sales")]);
        var urgent = builder.AddNoul("urgent", "Does this need attention today?");
        var plan = builder.Build();
        var response = new SystemOneResponseBuilder().Choice("department", "billing", 0.8, ("billing", 0.9), ("sales", 0.1))
            .Noul("urgent", 0.9).Build();
        var handler = FakeHttpMessageHandler.Always(response);
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var judge = new JevClient(http, new JevClientOptions { AuthenticationMode = JevAuthenticationMode.None });
        using var leaf = new TicketChat();
        using var router = new JevRoutingChatClient<int>(judge,
            new(plan, r => JevRouteDecision<int>.UseRoute(r.Get(department).Value)), new Dictionary<int, IChatClient> { [1] = leaf });
        // This synthetic threshold illustrates a host decision policy. Calibrate real policies per model.
        var policy = new JevGuardPolicy(new(plan, r => r.Get(urgent).Probability >= 0.5 ? JevGuardDecision.Allow : JevGuardDecision.Review));
        using var guard = new JevGuardChatClient(router, judge, policy, policy,
            new JevGuardOptions { OutputStreamingMode = JevOutputStreamingMode.Buffer, TotalTimeout = TimeSpan.FromSeconds(5) }, leaveOpen: true);
        using var method = new CancellationTokenSource();
        using var enumeration = new CancellationTokenSource();
        var stream = guard.GetStreamingResponseAsync([new(ChatRole.User, "ticket")], cancellationToken: method.Token);
        if (handler.Requests.Count != 0) { return false; }

        var reply = string.Empty;
        await foreach (var update in stream.WithCancellation(enumeration.Token))
        {
            reply += update.Text;
        }
        return reply == "billing reply" && leaf.Calls == 1 && handler.Requests.Count == 3;
    }

    internal sealed class TicketChat : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "billing reply")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "billing reply");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

}
