// Copyright (c) Tekium. Licensed under the MIT License.

using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using TekiumDB.Indexing;
using TekiumDB.Storage;

namespace TekiumDB.Benchmarks;

/// <summary>
/// Lectura puntual por clave primaria (UUID v7 de 16 bytes, valor de 100 bytes) sobre un millón de filas,
/// con la caché caliente en ambos motores. Cada invocación hace <see cref="Lookups"/> búsquedas de claves al azar.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class PointReadBenchmarks
{
    private const int Rows = 1_000_000;
    private const int Lookups = 1000;
    private const int LookupPool = 1 << 16;

    private readonly byte[] _buffer = new byte[Workload.PayloadSize];
    private string _directory = string.Empty;
    private byte[][] _lookupKeys = [];
    private ReadOnlyMemory<byte>[] _lookupMemory = [];
    private int _position;
    private Pager? _pager;
    private BTree _tree = null!;
    private SqliteConnection? _sqlite;
    private SqliteCommand _select = null!;
    private SqliteParameter _id = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _directory = Workload.NewDirectory();
        var keys = Workload.Uuid7Keys(Rows);
        var payload = Workload.Payload(1);

        var tekiumPath = Path.Combine(_directory, "point.tkdb");
        await Workload.FillTekiumAsync(tekiumPath, keys, payload, batchSize: 10_000);
        (_pager, _tree) = await Workload.OpenTekiumWarmAsync(tekiumPath);

        _sqlite = Workload.OpenSqlite(Path.Combine(_directory, "point.sqlite"));
        Workload.Execute(_sqlite, "CREATE TABLE kv (id BLOB PRIMARY KEY, payload BLOB NOT NULL) WITHOUT ROWID;");
        using (var transaction = _sqlite.BeginTransaction())
        using (var insert = _sqlite.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO kv (id, payload) VALUES ($id, $payload);";
            var id = insert.Parameters.Add("$id", SqliteType.Blob);
            insert.Parameters.Add("$payload", SqliteType.Blob).Value = payload;
            foreach (var key in keys)
            {
                id.Value = key;
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        Workload.Execute(_sqlite, "SELECT sum(length(payload)) FROM kv;");
        _select = _sqlite.CreateCommand();
        _select.CommandText = "SELECT payload FROM kv WHERE id = $id;";
        _id = _select.Parameters.Add("$id", SqliteType.Blob);
        _select.Prepare();

        var random = new Random(42);
        _lookupKeys = new byte[LookupPool][];
        _lookupMemory = new ReadOnlyMemory<byte>[LookupPool];
        for (var i = 0; i < LookupPool; i++)
        {
            _lookupKeys[i] = keys[random.Next(Rows)];
            _lookupMemory[i] = _lookupKeys[i];
        }
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        _select.Dispose();
        _sqlite?.Dispose();
        if (_pager is not null)
        {
            await _pager.DisposeAsync();
        }

        Directory.Delete(_directory, recursive: true);
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Lookups)]
    public long Sqlite()
    {
        long bytes = 0;
        for (var i = 0; i < Lookups; i++)
        {
            _id.Value = _lookupKeys[_position++ & (LookupPool - 1)];
            using var reader = _select.ExecuteReader();
            reader.Read();
            bytes += reader.GetBytes(0, 0, _buffer, 0, _buffer.Length);
        }

        return bytes;
    }

    [Benchmark(OperationsPerInvoke = Lookups)]
    public async ValueTask<long> Tekium()
    {
        long bytes = 0;
        for (var i = 0; i < Lookups; i++)
        {
            bytes += await _tree.GetAsync(_lookupMemory[_position++ & (LookupPool - 1)], _buffer, default);
        }

        return bytes;
    }
}
