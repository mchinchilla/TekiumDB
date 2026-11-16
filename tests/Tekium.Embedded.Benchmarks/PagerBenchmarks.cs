// Copyright (c) Tekium. Licensed under the MIT License.

using BenchmarkDotNet.Attributes;
using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Benchmarks;

/// <summary>
/// El camino caliente del pager: tomar un snapshot y fijar una página que ya está en caché.
/// Criterio de salida de la Fase 1: la columna Allocated debe ser cero.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class PagerBenchmarks
{
    private const int Pages = 1024;

    private string _directory = string.Empty;
    private Pager? _pager;
    private PageNumber[] _numbers = [];
    private int _position;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _directory = Workload.NewDirectory();
        _pager = await Pager.CreateAsync(
            FileStorageDevice.Open(Path.Combine(_directory, "pager.tkdb"), FileMode.CreateNew), Workload.Options, default);
        _numbers = new PageNumber[Pages];
        await using var transaction = await _pager.BeginWriteAsync(default);
        for (var i = 0; i < Pages; i++)
        {
            transaction.Allocate(PageType.BTreeLeaf, out _numbers[i]).Span.Fill((byte)i);
        }

        await transaction.CommitAsync(default);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_pager is not null)
        {
            await _pager.DisposeAsync();
        }

        Directory.Delete(_directory, recursive: true);
    }

    [Benchmark]
    public int ReadCachedPage()
    {
        using var snapshot = _pager!.BeginRead();
        if (!_pager.TryPin(_numbers[_position++ & (Pages - 1)], out var page))
        {
            return -1;
        }

        using (page)
        {
            return page.Span[64];
        }
    }
}
