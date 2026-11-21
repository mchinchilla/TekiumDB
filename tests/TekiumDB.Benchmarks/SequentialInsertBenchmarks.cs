// Copyright (c) Tekium. Licensed under the MIT License.

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Data.Sqlite;
using TekiumDB.Indexing;
using TekiumDB.Storage;

namespace TekiumDB.Benchmarks;

/// <summary>
/// Inserción secuencial de 100 000 filas (UUID v7 ascendente, valor de 100 bytes) en un archivo nuevo.
/// <see cref="RowsPerCommit"/> separa los dos regímenes: una sola transacción mide el costo de CPU del árbol;
/// transacciones de mil filas miden además el costo del commit durable (los flush a disco).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 7, invocationCount: 1)]
public class SequentialInsertBenchmarks
{
    private const int Rows = 100_000;

    private string _directory = string.Empty;
    private byte[][] _keys = [];
    private byte[] _payload = [];
    private int _run;
    private Pager? _pager;
    private BTree _tree = null!;
    private SqliteConnection? _sqlite;
    private SqliteCommand? _insert;
    private SqliteParameter _id = null!;

    [Params(1000, 100_000)]
    public int RowsPerCommit { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _directory = Workload.NewDirectory();
        _keys = Workload.Uuid7Keys(Rows);
        _payload = Workload.Payload(3);
    }

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    [IterationSetup(Target = nameof(Sqlite))]
    public void SetupSqlite()
    {
        _sqlite = Workload.OpenSqlite(Path.Combine(_directory, $"insert-{_run++}.sqlite"));
        Workload.Execute(_sqlite, "CREATE TABLE kv (id BLOB PRIMARY KEY, payload BLOB NOT NULL) WITHOUT ROWID;");
        _insert = _sqlite.CreateCommand();
        _insert.CommandText = "INSERT INTO kv (id, payload) VALUES ($id, $payload);";
        _id = _insert.Parameters.Add("$id", SqliteType.Blob);
        _insert.Parameters.Add("$payload", SqliteType.Blob).Value = _payload;
        _insert.Prepare();
    }

    [IterationCleanup(Target = nameof(Sqlite))]
    public void CleanupSqlite()
    {
        _insert?.Dispose();
        _sqlite?.Dispose();
    }

    [IterationSetup(Target = nameof(Tekium))]
    public async Task SetupTekiumAsync()
    {
        var device = FileStorageDevice.Open(Path.Combine(_directory, $"insert-{_run++}.tkdb"), FileMode.CreateNew);
        _pager = await Pager.CreateAsync(device, Workload.Options, default);
        _tree = new BTree(_pager);
    }

    [IterationCleanup(Target = nameof(Tekium))]
    public async Task CleanupTekiumAsync()
    {
        if (_pager is not null)
        {
            await _pager.DisposeAsync();
        }
    }

    [Benchmark(Baseline = true)]
    public void Sqlite()
    {
        for (var start = 0; start < Rows; start += RowsPerCommit)
        {
            var end = Math.Min(start + RowsPerCommit, Rows);
            using var transaction = _sqlite!.BeginTransaction();
            _insert!.Transaction = transaction;
            for (var i = start; i < end; i++)
            {
                _id.Value = _keys[i];
                _insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    [Benchmark]
    public ValueTask Tekium() => Workload.InsertTekiumAsync(_pager!, _tree, _keys, _payload, RowsPerCommit);
}
