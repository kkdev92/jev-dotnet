using BenchmarkDotNet.Attributes;
using Kkdev92.Jev.Requests;

namespace Kkdev92.Jev.Benchmarks;

/// <summary>
/// P1: writing one request body, with the plan's questions already encoded.
/// </summary>
/// <remarks>
/// <para>
/// The per-call half of the plan trade-off: the questions are copied in as bytes, so what grows
/// with the call is the state. Two scripts, because the encoder matters most outside ASCII: a
/// Japanese character is three bytes of UTF-8, and would be six as an escape.
/// </para>
/// <para>
/// The limit is <see cref="int.MaxValue"/> so no workload trips it; the check itself is still paid.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class RequestBenchmarks
{
    private JevDecisionPlan _plan = null!;
    private JevContent _state;

    [Params(256, 4096, 32768)]
    public int StateBytes { get; set; }

    [Params("ascii", "japanese")]
    public string Script { get; set; } = "ascii";

    [GlobalSetup]
    public void Setup()
    {
        _plan = Workloads.Get(Workloads.Triage).BuildPlan();
        _state = Workloads.State(StateBytes, Script == "japanese");
    }

    [Benchmark(Description = "encode request body")]
    public int Encode() => RequestBodyWriter.WriteEvaluate(JevClientOptions.DefaultModelName, StateSource.FromContent(_state), _plan, int.MaxValue).Length;
}
