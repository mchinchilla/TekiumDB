// Copyright (c) Tekium. Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Benchmarks;

/// <summary>
/// Inserción por UUID: cuánto tarda y qué tan llenas quedan las hojas según cómo llegan las claves.
/// Un UUID v7 crece de un milisegundo al siguiente, pero los de un mismo milisegundo llegan en cualquier
/// orden; el v4 es el caso malo, al azar. Se corre con <c>-- uuid [filas]</c>.
/// </summary>
internal static class UuidInsertReport
{
    private const int RowsPerCommit = 10_000;

    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync(int rows)
    {
        var directory = Workload.NewDirectory();
        try
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{rows:N0} filas de {Workload.PayloadSize} bytes, commits de {RowsPerCommit:N0}, sobre archivo."));
            Console.WriteLine("| Claves | TekiumDB | Hojas | Ocupación | Archivo | SQLite | Archivo SQLite |");
            Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: |");

            // Una vuelta chica antes, para no medir la compilación del código.
            await TekiumAsync(Path.Combine(directory, "warm.tkdb"), 20_000, static i => Spread(i, 1));
            Sqlite(Path.Combine(directory, "warm.sqlite"), 20_000, static i => Spread(i, 1));

            var regimes = new (string Name, Func<int, Guid> Key)[]
            {
                ("v7, uno por milisegundo", static i => Spread(i, 1)),
                ("v7, 4 por milisegundo", static i => Spread(i, 4)),
                ("v7, 16 por milisegundo", static i => Spread(i, 16)),
                ("v7, 64 por milisegundo", static i => Spread(i, 64)),
                ("v7, 1 000 por milisegundo", static i => Spread(i, 1000)),
                ("v7, `Guid.CreateVersion7()` al insertar", static _ => Guid.CreateVersion7()),
                ("v4, al azar", static _ => Guid.NewGuid()),
            };
            for (var i = 0; i < regimes.Length; i++)
            {
                var (name, key) = regimes[i];
                var (elapsed, leaves, occupancy, size) = await TekiumAsync(Path.Combine(directory, $"{i}.tkdb"), rows, key);
                var (sqliteElapsed, sqliteSize) = Sqlite(Path.Combine(directory, $"{i}.sqlite"), rows, key);
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"| {name} | {elapsed.TotalMilliseconds:F0} ms | {leaves:N0} | {occupancy:P1} | {size / 1048576.0:F1} MiB | {sqliteElapsed.TotalMilliseconds:F0} ms | {sqliteSize / 1048576.0:F1} MiB |"));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // Lo que sigue a la marca de tiempo es azar: los de un mismo milisegundo no llegan en orden.
    private static Guid Spread(int row, int perMillisecond) => Guid.CreateVersion7(Epoch.AddMilliseconds(row / perMillisecond));

    private static async Task<(TimeSpan Elapsed, int Leaves, double Occupancy, long Size)> TekiumAsync(
        string path, int rows, Func<int, Guid> next)
    {
        var payload = Workload.Payload(3);
        var key = new byte[16];
        var pager = await Pager.CreateAsync(FileStorageDevice.Open(path, FileMode.CreateNew), Workload.Options, default);
        await using (pager)
        {
            var tree = new BTree(pager);
            var watch = Stopwatch.StartNew();
            for (var start = 0; start < rows; start += RowsPerCommit)
            {
                await using var transaction = await pager.BeginWriteAsync(default);
                for (var i = start; i < Math.Min(start + RowsPerCommit, rows); i++)
                {
                    next(i).TryWriteBytes(key, bigEndian: true, out _);
                    await tree.PutAsync(transaction, key, payload, default);
                }

                await transaction.CommitAsync(default);
            }

            watch.Stop();
            long used = 0;
            long capacity = 0;
            var leaves = 0;
            using var read = pager.BeginRead();
            var pending = new Stack<PageNumber>();
            pending.Push(read.Root);
            while (pending.TryPop(out var number))
            {
                using var page = await pager.PinAsync(number, default);
                if (BTreeNode.IsLeaf(page.Span))
                {
                    used += BTreeNode.UsedSpace(page.Span);
                    capacity += BTreeNode.Capacity(page.Span);
                    leaves++;
                    continue;
                }

                for (var i = 0; i <= BTreeNode.Count(page.Span); i++)
                {
                    pending.Push(BTreeNode.Child(page.Span, i));
                }
            }

            return (watch.Elapsed, leaves, (double)used / capacity, (long)pager.Current.PageCount * pager.PageSize.Bytes);
        }
    }

    private static (TimeSpan Elapsed, long Size) Sqlite(string path, int rows, Func<int, Guid> next)
    {
        var payload = Workload.Payload(3);
        var key = new byte[16];
        TimeSpan elapsed;
        using (var connection = Workload.OpenSqlite(path))
        {
            Workload.Execute(connection, "CREATE TABLE kv (id BLOB PRIMARY KEY, payload BLOB NOT NULL) WITHOUT ROWID;");
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO kv (id, payload) VALUES ($id, $payload);";
            var id = insert.Parameters.Add("$id", SqliteType.Blob);
            insert.Parameters.Add("$payload", SqliteType.Blob).Value = payload;
            insert.Prepare();
            var watch = Stopwatch.StartNew();
            for (var start = 0; start < rows; start += RowsPerCommit)
            {
                using var transaction = connection.BeginTransaction();
                insert.Transaction = transaction;
                for (var i = start; i < Math.Min(start + RowsPerCommit, rows); i++)
                {
                    next(i).TryWriteBytes(key, bigEndian: true, out _);
                    id.Value = key;
                    insert.ExecuteNonQuery();
                }

                transaction.Commit();
            }

            elapsed = watch.Elapsed;
            Workload.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        return (elapsed, new FileInfo(path).Length);
    }
}
