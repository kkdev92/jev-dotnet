using BenchmarkDotNet.Running;

// dotnet run --project src/benchmarks/Kkdev92.Jev.Benchmarks -c Release -- --filter '*'
// See src/benchmarks/BASELINE.md for what these measure, what they do not, and the recorded numbers.
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
