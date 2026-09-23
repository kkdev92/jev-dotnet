using BenchmarkDotNet.Attributes;

namespace Kkdev92.Jev.Benchmarks;

/// <summary>
/// P1: what building a plan costs, which is what a caller pays once per plan rather than per call.
/// </summary>
/// <remarks>
/// Kept visible rather than hidden behind the per-call numbers: a caller
/// whose questions change on every request builds a plan every time, and pays this on top of the
/// call. The reuse ratio that makes a plan worthwhile is the build cost against what it saves on
/// each call, and both halves are measured — here and in <see cref="RequestBenchmarks"/>.
/// </remarks>
[MemoryDiagnoser]
public class PlanBenchmarks
{
    private Workload _workload = null!;

    [ParamsSource(nameof(WorkloadNames))]
    public string Workload { get; set; } = Workloads.Triage;

    public static IEnumerable<string> WorkloadNames => Workloads.All;

    [GlobalSetup]
    public void Setup() => _workload = Workloads.Get(Workload);

    [Benchmark(Description = "build plan")]
    public JevDecisionPlan Build() => _workload.BuildPlan();
}
