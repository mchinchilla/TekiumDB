// Copyright (c) Tekium. Licensed under the MIT License.

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace TekiumDB.Benchmarks;

/// <summary>
/// Criterio de la Fase 6.7: la latencia de escritura no se mueve con el feed activo. Los mismos commits
/// con el feed apagado, encendido (conservando cien commits) y encendido con un consumidor leyendo cada
/// cambio mientras se escribe. Sobre archivo, donde manda el fsync, y en memoria, donde se ve la CPU.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 2, iterationCount: 9, invocationCount: 1)]
public class ChangeFeedBenchmarks
{
    private const int FileCommits = 200;
    private const int MemoryCommits = 2000;
    private const int RowsPerCommit = 20;
    private const int Rows = 20_000;
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private string _directory = string.Empty;
    private int _run;
    private TekiumDb? _database;
    private Guid[] _ids = [];
    private CancellationTokenSource? _stop;
    private Task<int>? _consumer;
    private int _cuts;

    public enum Feed
    {
        Off,
        On,
        Watched,
    }

    [Params(Feed.Off, Feed.On, Feed.Watched)]
    public Feed Mode { get; set; }

    /// <summary>Lo que ocupa el archivo después de la misma carga con y sin conservar commits.</summary>
    public static async Task ReportSpaceAsync()
    {
        foreach (var retention in new[] { 0, 10, 100, 1000 })
        {
            var benchmark = new ChangeFeedBenchmarks { _directory = Workload.NewDirectory() };
            try
            {
                var path = Path.Combine(benchmark._directory, "space.tkdb");
                benchmark._database = await TekiumDb.OpenAsync(new DbOptions { Path = path, ChangeRetention = retention });
                await benchmark.FillAsync();
                var loaded = new FileInfo(path).Length;
                await benchmark.CommitAsync(2000);
                await benchmark._database.DisposeAsync();
                await Console.Out.WriteLineAsync(
                    $"retention {retention,5}: {loaded / 1048576.0,7:F1} MiB after loading {Rows} rows, {new FileInfo(path).Length / 1048576.0,7:F1} MiB after 2000 commits of {RowsPerCommit} updates");
            }
            finally
            {
                Directory.Delete(benchmark._directory, recursive: true);
            }
        }
    }

    /// <summary>Si el consumidor del benchmark sigue el paso del escritor: cuántos cambios ve y cuántas veces lo cortan.</summary>
    public static async Task ReportConsumerAsync()
    {
        foreach (var memory in new[] { true, false })
        {
            var benchmark = new ChangeFeedBenchmarks { Mode = Feed.Watched, _directory = Workload.NewDirectory() };
            try
            {
                var commits = memory ? MemoryCommits : FileCommits;
                await benchmark.OpenAsync(memory
                    ? new DbOptions { InMemory = true, ChangeRetention = benchmark.Retention }
                    : new DbOptions { Path = Path.Combine(benchmark._directory, "consumer.tkdb"), ChangeRetention = benchmark.Retention });
                await benchmark.CommitAsync(commits);
                await Task.Delay(500);
                await benchmark._stop!.CancelAsync();
                var seen = await benchmark._consumer!;
                await benchmark._database!.DisposeAsync();
                await Console.Out.WriteLineAsync(
                    $"{(memory ? "memory" : "file  ")}: the consumer saw {seen} of {commits * RowsPerCommit} changes and was cut off {benchmark._cuts} times");
            }
            finally
            {
                Directory.Delete(benchmark._directory, recursive: true);
            }
        }
    }

    [GlobalSetup]
    public void Setup() => _directory = Workload.NewDirectory();

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    [IterationSetup(Target = nameof(CommitsOnFile))]
    public Task SetupFileAsync() => OpenAsync(new DbOptions { Path = Path.Combine(_directory, $"feed-{_run++}.tkdb"), ChangeRetention = Retention });

    [IterationSetup(Target = nameof(CommitsInMemory))]
    public Task SetupMemoryAsync() => OpenAsync(new DbOptions { InMemory = true, ChangeRetention = Retention });

    [IterationCleanup]
    public async Task CleanupIterationAsync()
    {
        if (_consumer is not null)
        {
            await _stop!.CancelAsync();
            await _consumer;
            _stop.Dispose();
            _consumer = null;
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Benchmark]
    public Task CommitsOnFile() => CommitAsync(FileCommits);

    [Benchmark]
    public Task CommitsInMemory() => CommitAsync(MemoryCommits);

    private int Retention => Mode == Feed.Off ? 0 : 100;

    private async Task OpenAsync(DbOptions options)
    {
        _database = await TekiumDb.OpenAsync(options);
        await FillAsync();
        if (Mode == Feed.Watched)
        {
            _stop = new CancellationTokenSource();
            var database = _database;
            var since = database.LastTransactionId;
            var stop = _stop.Token;
            _consumer = Task.Run(async () =>
            {
                var seen = 0;
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        await foreach (var change in database.WatchAsync<InvoicePlain>(since, stop))
                        {
                            seen += change.After is null ? 0 : 1;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (TkChangeFeedException)
                    {
                        // Quedó demasiado atrás: retoma desde ahora, y se cuenta.
                        Interlocked.Increment(ref _cuts);
                        since = database.LastTransactionId;
                    }
                }

                return seen;
            });
        }
    }

    private async Task FillAsync()
    {
        _ids = new Guid[Rows];
        var table = _database!.Table<InvoicePlain>();
        await using var writer = await _database.BeginWriteAsync();
        for (var i = 0; i < Rows; i++)
        {
            _ids[i] = Guid.CreateVersion7(Epoch.AddSeconds(i));
            await table.InsertAsync(writer, new InvoicePlain { Id = _ids[i], Customer = i, Notes = "Loaded" });
        }

        await writer.CommitAsync();
    }

    /// <summary>Cada commit actualiza veinte filas repartidas por la tabla.</summary>
    private async Task CommitAsync(int commits)
    {
        var table = _database!.Table<InvoicePlain>();
        for (var commit = 0; commit < commits; commit++)
        {
            await using var writer = await _database.BeginWriteAsync();
            for (var i = 0; i < RowsPerCommit; i++)
            {
                var row = unchecked((int)(((uint)((commit * RowsPerCommit) + i) * 2654435761u) % Rows));
                await table.UpsertAsync(writer, new InvoicePlain { Id = _ids[row], Customer = commit, Notes = "Updated" });
            }

            await writer.CommitAsync();
        }
    }
}
