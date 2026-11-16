// Copyright (c) Tekium. Licensed under the MIT License.

using BenchmarkDotNet.Attributes;
using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Benchmarks.Storage;

/// <summary>
/// Benchmark de humo de la Fase 0: prueba que el arnés (BenchmarkDotNet + diagnosers) funciona
/// contra el núcleo. Los benchmarks reales contra SQLite y LiteDB llegan en la Fase 0.5.
/// </summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
public class PageSizeBenchmarks
{
    [Params(8192, 12288)]
    public int Bytes { get; set; }

    [Benchmark]
    public bool TryFromBytes() => PageSize.TryFromBytes(Bytes, out _);
}
