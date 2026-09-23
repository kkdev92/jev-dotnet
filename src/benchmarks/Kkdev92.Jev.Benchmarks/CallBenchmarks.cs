using System.Net;
using System.Net.Http.Headers;
using BenchmarkDotNet.Attributes;
using Kkdev92.Jev.TestSupport;

namespace Kkdev92.Jev.Benchmarks;

/// <summary>
/// P2: one whole logical call through <see cref="JevClient"/>, over an in-process handler.
/// </summary>
/// <remarks>
/// <para>
/// What the SDK costs per call with the network taken out: argument checks, the deadline, the
/// credential, the request body, the headers, the bounded read of the response and the decode. It
/// does not include sockets, TLS or the service's own time, which dwarf all of it and are not the
/// SDK's to spend. Those belong to the loopback and live tiers, which are not automated.
/// </para>
/// <para>
/// The handler reads the request body to the end, as a transport would, and answers with the same
/// bytes every time.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class CallBenchmarks
{
    private JevClient _client = null!;
    private HttpClient _httpClient = null!;
    private JevDecisionPlan _plan = null!;
    private string _state = string.Empty;

    [ParamsSource(nameof(WorkloadNames))]
    public string Workload { get; set; } = Workloads.Triage;

    public static IEnumerable<string> WorkloadNames => Workloads.All;

    [GlobalSetup]
    public void Setup()
    {
        var workload = Workloads.Get(Workload);
        _plan = workload.BuildPlan();
        _state = Workloads.State(1024, japanese: false);
        _httpClient = new HttpClient(new ReplayHandler(workload.BuildResponse())) { Timeout = Timeout.InfiniteTimeSpan };
        _client = new JevClient(_httpClient, new JevClientOptions { Credential = new StaticJevCredential("sk-benchmark-not-a-key") });
    }

    [GlobalCleanup]
    public void Cleanup() => _httpClient.Dispose();

    [Benchmark(Description = "EvaluateAsync, in-process handler")]
    public Task<JevResult> Evaluate() => _client.EvaluateAsync(_state, _plan);

    private sealed class ReplayHandler(byte[] body) : HttpMessageHandler
    {
        private static readonly MediaTypeHeaderValue Json = new("application/json");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await request.Content!.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
                RequestMessage = request,
            };

            response.Content.Headers.ContentType = Json;
            response.Headers.TryAddWithoutValidation("x-typesafe-request-id", FakeResponses.RequestId);

            return response;
        }
    }
}
