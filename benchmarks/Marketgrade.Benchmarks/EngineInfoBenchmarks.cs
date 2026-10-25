using BenchmarkDotNet.Attributes;
using Marketgrade.Engine;

namespace Marketgrade.Benchmarks;

[MemoryDiagnoser]
public class EngineInfoBenchmarks
{
    [Benchmark]
    public int VersionLength() => EngineInfo.Version.Length;
}
