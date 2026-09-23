using BenchmarkDotNet.Attributes;
using Kkdev92.Jev.Decoding;

namespace Kkdev92.Jev.Benchmarks;

/// <summary>
/// P1: the two response decoders over the same bodies.
/// </summary>
/// <remarks>
/// <para>
/// The measurement behind the adoption gate in <c>src/benchmarks/BASELINE.md</c>. The reference
/// decoder — the source-generated wire models, then a mapping onto the plan with the same checks —
/// is the baseline: it is what a careful implementation on System.Text.Json looks like. The
/// hand-written reader is the production decoder because it clears the gate on these workloads,
/// which are fixed in advance.
/// </para>
/// <para>
/// Both receive the complete body as a span, as the client gives it to them after a bounded read,
/// so neither pays for I/O here.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class DecodeBenchmarks
{
    private JevDecisionPlan _plan = null!;
    private byte[] _body = null!;

    [ParamsSource(nameof(WorkloadNames))]
    public string Workload { get; set; } = Workloads.Triage;

    public static IEnumerable<string> WorkloadNames => Workloads.All;

    [GlobalSetup]
    public void Setup()
    {
        var workload = Workloads.Get(Workload);
        _plan = workload.BuildPlan();
        _body = workload.BuildResponse();

        // Both must accept the body, or the comparison is between two ways of failing.
        _ = ReferenceResponseDecoder.Decode(_body, _plan, 1e-6);
        _ = SystemOneResponseReader.Read(_body, _plan, 1e-6);
    }

    [Benchmark(Baseline = true, Description = "reference: STJ source-gen + mapping")]
    public object Reference() => ReferenceResponseDecoder.Decode(_body, _plan, 1e-6);

    [Benchmark(Description = "reader: single pass")]
    public object Reader() => SystemOneResponseReader.Read(_body, _plan, 1e-6);
}
