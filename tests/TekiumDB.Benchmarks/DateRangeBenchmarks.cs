// Copyright (c) Tekium. Licensed under the MIT License.

using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using TekiumDB.Indexing;
using TekiumDB.Storage;

namespace TekiumDB.Benchmarks;

/// <summary>
/// Rango de fechas sobre un millón de filas (una por segundo, valor de 100 bytes), con la caché caliente.
/// SQLite usa su camino más rápido: la fecha es el rowid (<c>INTEGER PRIMARY KEY</c>).
/// Ambos leen la clave y copian el valor de cada fila.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class DateRangeBenchmarks
{
    private const int Rows = 1_000_000;

    private readonly byte[] _buffer = new byte[Workload.PayloadSize];
    private string _directory = string.Empty;
    private int _start;
    private Pager? _pager;
    private BTree _tree = null!;
    private SqliteConnection? _sqlite;
    private SqliteCommand _select = null!;
    private SqliteParameter _from = null!;
    private SqliteParameter _to = null!;

    [Params(1000, 100_000)]
    public int RangeRows { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _directory = Workload.NewDirectory();
        var payload = Workload.Payload(2);
        var keys = new byte[Rows][];
        for (var i = 0; i < Rows; i++)
        {
            keys[i] = Workload.TimestampKey(Workload.Ticks(i));
        }

        var tekiumPath = Path.Combine(_directory, "range.tkdb");
        await Workload.FillTekiumAsync(tekiumPath, keys, payload, batchSize: 10_000);
        (_pager, _tree) = await Workload.OpenTekiumWarmAsync(tekiumPath);

        _sqlite = Workload.OpenSqlite(Path.Combine(_directory, "range.sqlite"));
        Workload.Execute(_sqlite, "CREATE TABLE ev (ts INTEGER PRIMARY KEY, payload BLOB NOT NULL);");
        using (var transaction = _sqlite.BeginTransaction())
        using (var insert = _sqlite.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO ev (ts, payload) VALUES ($ts, $payload);";
            var ts = insert.Parameters.Add("$ts", SqliteType.Integer);
            insert.Parameters.Add("$payload", SqliteType.Blob).Value = payload;
            for (var i = 0; i < Rows; i++)
            {
                ts.Value = Workload.Ticks(i);
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        Workload.Execute(_sqlite, "SELECT sum(length(payload)) FROM ev;");
        _select = _sqlite.CreateCommand();
        _select.CommandText = "SELECT ts, payload FROM ev WHERE ts >= $from AND ts < $to;";
        _from = _select.Parameters.Add("$from", SqliteType.Integer);
        _to = _select.Parameters.Add("$to", SqliteType.Integer);
        _select.Prepare();
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

    [Benchmark(Baseline = true)]
    public long Sqlite()
    {
        var first = NextStart();
        _from.Value = Workload.Ticks(first);
        _to.Value = Workload.Ticks(first + RangeRows);
        long checksum = 0;
        using var reader = _select.ExecuteReader();
        while (reader.Read())
        {
            checksum += reader.GetInt64(0) & 0xFF;
            checksum += reader.GetBytes(1, 0, _buffer, 0, _buffer.Length);
        }

        return checksum;
    }

    [Benchmark]
    public async ValueTask<long> Tekium()
    {
        var first = NextStart();
        ReadOnlyMemory<byte> from = Workload.TimestampKey(Workload.Ticks(first));
        ReadOnlyMemory<byte> to = Workload.TimestampKey(Workload.Ticks(first + RangeRows));
        long checksum = 0;
        await foreach (var entry in _tree.RangeAsync(from, to, default))
        {
            checksum += entry.Key.Span[7];
            entry.Value.Span.CopyTo(_buffer);
            checksum += entry.Value.Length;
        }

        return checksum;
    }

    // Cada invocación arranca en otro punto de la tabla, el mismo para ambos motores.
    private int NextStart()
    {
        _start = (_start + 104_729) % (Rows - RangeRows);
        return _start;
    }
}
