// Copyright (c) Tekium. Licensed under the MIT License.

using System.Globalization;
using Microsoft.Data.Sqlite;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Storage;
using Tekium.Embedded.Transactions;

namespace Tekium.Embedded.Benchmarks;

/// <summary>
/// Cuánto ocupa el archivo frente a los datos que contiene después de muchas actualizaciones.
/// Copy-on-write reescribe el camino entero de cada fila que cambia: esto mide lo que cuesta en espacio.
/// No es un benchmark de tiempo, así que no usa BenchmarkDotNet: se corre con <c>-- space</c>.
/// </summary>
internal static class SpaceAmplification
{
    private const int Rows = 100_000;
    private const int Transactions = 2_000;
    private const int UpdatesPerTransaction = 50;

    public static async Task RunAsync()
    {
        var keys = Workload.Uuid7Keys(Rows);
        var logical = (long)Rows * (keys[0].Length + Workload.PayloadSize);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{Rows:N0} filas, {logical / 1048576.0:F1} MiB de datos; {Transactions:N0} transacciones de {UpdatesPerTransaction} actualizaciones al azar."));
        Console.WriteLine("| Base | Tras la carga inicial | Tras las actualizaciones | Páginas vivas | Páginas libres | Veces los datos |");
        Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: |");

        await TekiumAsync("TekiumDB", keys, logical, holdReader: false);
        await TekiumAsync("TekiumDB con un lector retenido todo el tiempo", keys, logical, holdReader: true);
        Sqlite(keys, logical);
    }

    private static async Task TekiumAsync(string name, byte[][] keys, long logical, bool holdReader)
    {
        // En memoria: el tamaño es el mismo que en archivo y no se pagan cuatro mil flushes.
        var pager = await Pager.CreateAsync(new MemoryStorageDevice(), Workload.Options, default);
        await using (pager)
        {
            var tree = new BTree(pager);
            var pageBytes = pager.PageSize.Bytes;
            await Workload.InsertTekiumAsync(pager, tree, keys, Workload.Payload(3), Rows);
            var initial = (long)pager.Current.PageCount * pageBytes;

            var reader = holdReader ? ReadTransaction.Begin(pager) : null;
            var random = new Random(17);
            var payload = new byte[Workload.PayloadSize];
            for (var i = 0; i < Transactions; i++)
            {
                await using var transaction = await pager.BeginWriteAsync(default);
                for (var update = 0; update < UpdatesPerTransaction; update++)
                {
                    random.NextBytes(payload);
                    await tree.PutAsync(transaction, keys[random.Next(Rows)], payload, default);
                }

                await transaction.CommitAsync(default);
            }

            var final = (long)pager.Current.PageCount * pageBytes;
            var free = pager.FreePageCount + pager.FreeListPageCount;
            Report(name, initial, final, pager.Current.PageCount - free, free, logical);
            if (reader is not null)
            {
                await reader.DisposeAsync();
            }
        }
    }

    private static void Sqlite(byte[][] keys, long logical)
    {
        var directory = Workload.NewDirectory();
        try
        {
            var path = Path.Combine(directory, "space.sqlite");
            using (var connection = Workload.OpenSqlite(path))
            {
                // El tamaño no depende de la durabilidad: sin sync para no esperar cuatro mil veces al disco.
                // SQLite queda con su página por defecto de 4 KiB; se compara el tamaño, no el número de páginas.
                Workload.Execute(connection, "PRAGMA synchronous=OFF;");
                Workload.Execute(connection, "CREATE TABLE kv (id BLOB PRIMARY KEY, payload BLOB NOT NULL) WITHOUT ROWID;");
                using var upsert = connection.CreateCommand();
                upsert.CommandText = "INSERT INTO kv (id, payload) VALUES ($id, $payload) ON CONFLICT(id) DO UPDATE SET payload = excluded.payload;";
                var id = upsert.Parameters.Add("$id", SqliteType.Blob);
                var payloadParameter = upsert.Parameters.Add("$payload", SqliteType.Blob);
                payloadParameter.Value = Workload.Payload(3);
                using (var transaction = connection.BeginTransaction())
                {
                    upsert.Transaction = transaction;
                    foreach (var key in keys)
                    {
                        id.Value = key;
                        upsert.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }

                var initial = Size(connection);
                var random = new Random(17);
                var payload = new byte[Workload.PayloadSize];
                payloadParameter.Value = payload;
                for (var i = 0; i < Transactions; i++)
                {
                    using var transaction = connection.BeginTransaction();
                    upsert.Transaction = transaction;
                    for (var update = 0; update < UpdatesPerTransaction; update++)
                    {
                        random.NextBytes(payload);
                        id.Value = keys[random.Next(Rows)];
                        upsert.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }

                var free = Scalar(connection, "PRAGMA freelist_count;");
                var pages = Scalar(connection, "PRAGMA page_count;");
                Report("SQLite (WAL, tras checkpoint)", initial, Size(connection), pages - free, free, logical);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static long Size(SqliteConnection connection)
    {
        Workload.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        return Scalar(connection, "PRAGMA page_count;") * Scalar(connection, "PRAGMA page_size;");
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static void Report(string name, long initial, long final, long live, long free, long logical) =>
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"| {name} | {initial / 1048576.0:F1} MiB | {final / 1048576.0:F1} MiB | {live:N0} | {free:N0} | {(double)final / logical:F2}× |"));
}
