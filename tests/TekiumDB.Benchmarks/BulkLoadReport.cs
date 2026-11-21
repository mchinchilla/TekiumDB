// Copyright (c) Tekium. Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TekiumDB.Benchmarks;

/// <summary>La misma factura con dos índices, para ver lo que cuesta cargarlos.</summary>
[TkTable("invoices_loaded")]
public sealed partial class InvoiceLoaded
{
    [TkKey]
    public Guid Id { get; set; }

    [TkIndex]
    public int Customer { get; set; }

    [TkIndex]
    public DateTimeOffset IssuedAt { get; set; }

    public decimal Total { get; set; }

    public bool Paid { get; set; }

    public string Notes { get; set; } = string.Empty;
}

/// <summary>
/// Criterio de la Fase 6.8: importar diez millones de filas ordenadas es un orden de magnitud más rápido
/// que insertarlas una por una. Cada modo corre en su propio proceso, para que el pico de memoria sea el suyo:
/// <c>-- bulk one|bulk|shuffled|indexed-one|indexed-bulk|sqlite [filas]</c>.
/// </summary>
internal static class BulkLoadReport
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync(string mode, int rows)
    {
        var directory = Workload.NewDirectory();
        try
        {
            var path = Path.Combine(directory, "bulk.tkdb");
            // El pico de memoria del proceso, muestreado: macOS no lo informa de otra forma.
            long peak = 0;
            using var sampling = new CancellationTokenSource();
            var sampler = Task.Run(async () =>
            {
                while (!sampling.IsCancellationRequested)
                {
                    peak = Math.Max(peak, Environment.WorkingSet);
                    await Task.Delay(20);
                }
            });

            var watch = Stopwatch.StartNew();
            long committed;
            if (mode == "sqlite")
            {
                path = Path.Combine(directory, "bulk.sqlite");
                committed = Sqlite(path, rows);
            }
            else
            {
                if (mode == "batched")
                {
                    committed = await BatchedAsync(path, rows);
                    goto done;
                }

                var database = await TekiumDb.OpenAsync(
                    Environment.GetEnvironmentVariable("TEKIUM_BULK_MEMORY") == "1" ? new DbOptions { InMemory = true } : new DbOptions { Path = path });
                await using (database)
                {
                    await using var writer = await database.BeginWriteAsync();
                    committed = mode switch
                    {
                        "generate" => Plain(rows, shuffled: false).LongCount(),
                        "one" => await OneByOneAsync(database.Table<InvoicePlain>(), writer, Plain(rows, shuffled: false)),
                        "shuffled-one" => await OneByOneAsync(database.Table<InvoicePlain>(), writer, Plain(rows, shuffled: true)),
                        "bulk" => await database.Table<InvoicePlain>().BulkInsertAsync(writer, Plain(rows, shuffled: false)),
                        "shuffled" => await database.Table<InvoicePlain>().BulkInsertAsync(writer, Plain(rows, shuffled: true)),
                        "indexed-one" => await OneByOneAsync(database.Table<InvoiceLoaded>(), writer, Indexed(rows)),
                        "indexed-bulk" => await database.Table<InvoiceLoaded>().BulkInsertAsync(writer, Indexed(rows)),
                        _ => throw new ArgumentException($"Unknown mode '{mode}'."),
                    };
                    var loaded = watch.Elapsed.TotalSeconds;
                    await writer.CommitAsync();
                    await Console.Out.WriteLineAsync(string.Create(
                        CultureInfo.InvariantCulture, $"  loading {loaded:F2} s, commit {watch.Elapsed.TotalSeconds - loaded:F2} s"));
                }
            }

        done:
            watch.Stop();
            await sampling.CancelAsync();
            await sampler;
            await Console.Out.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"{mode,-13} {committed,10} rows  {watch.Elapsed.TotalSeconds,7:F2} s  {committed / watch.Elapsed.TotalSeconds / 1000,7:F0} k rows/s  file {(File.Exists(path) ? new FileInfo(path).Length : 0) / 1048576.0,7:F0} MiB  peak memory {peak / 1048576.0,6:F0} MiB"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Una por una, confirmando cada mil filas: como se carga cuando no hay carga masiva.</summary>
    private static async Task<long> BatchedAsync(string path, int rows)
    {
        var database = await TekiumDb.OpenAsync(new DbOptions { Path = path });
        await using (database)
        {
            var table = database.Table<InvoicePlain>();
            long count = 0;
            TkWriteTransaction? writer = null;
            foreach (var row in Plain(rows, shuffled: false))
            {
                writer ??= await database.BeginWriteAsync();
                await table.InsertAsync(writer, row);
                if (++count % 1000 == 0)
                {
                    await writer.CommitAsync();
                    await writer.DisposeAsync();
                    writer = null;
                }
            }

            if (writer is not null)
            {
                await writer.CommitAsync();
                await writer.DisposeAsync();
            }

            return count;
        }
    }

    private static async Task<long> OneByOneAsync<T>(Table<T> table, TkWriteTransaction writer, IEnumerable<T> rows)
        where T : class, ITkRow<T>
    {
        long count = 0;
        foreach (var row in rows)
        {
            await table.InsertAsync(writer, row);
            count++;
        }

        return count;
    }

    /// <summary>Un orden que recorre todas las filas sin repetir, saltando por la tabla.</summary>
    private static int Position(int i, int rows, bool shuffled) => shuffled ? (int)((long)i * 7_368_787 % rows) : i;

    private static IEnumerable<InvoicePlain> Plain(int rows, bool shuffled)
    {
        for (var i = 0; i < rows; i++)
        {
            var row = Position(i, rows, shuffled);
            var mixed = unchecked((uint)row * 2654435761u);
            yield return new InvoicePlain
            {
                // Como un UUID v7, creciente con el número de fila, pero sin pagar su generador en cada fila.
                Id = new Guid(row >> 16, (short)row, 0x7000, 0x80, 0, 0, 0, 0, 0, 0, 1),
                Customer = (int)(mixed % 100_000),
                IssuedAt = Epoch.AddSeconds(mixed % 31_536_000),
                Total = (mixed % 1_000_000) / 100m,
                Paid = mixed % 10 != 0,
                Notes = "Invoice",
            };
        }
    }

    private static IEnumerable<InvoiceLoaded> Indexed(int rows)
    {
        foreach (var row in Plain(rows, shuffled: false))
        {
            yield return new InvoiceLoaded { Id = row.Id, Customer = row.Customer, IssuedAt = row.IssuedAt, Total = row.Total, Paid = row.Paid, Notes = row.Notes };
        }
    }

    private static long Sqlite(string path, int rows)
    {
        using var connection = Workload.OpenSqlite(path);
        Workload.Execute(
            connection,
            "CREATE TABLE invoices (id BLOB PRIMARY KEY, customer INTEGER NOT NULL, issued INTEGER NOT NULL, total REAL NOT NULL, paid INTEGER NOT NULL, notes TEXT NOT NULL) WITHOUT ROWID;");
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO invoices VALUES ($id, $customer, $issued, $total, $paid, $notes);";
        var id = insert.Parameters.Add("$id", SqliteType.Blob);
        var customer = insert.Parameters.Add("$customer", SqliteType.Integer);
        var issued = insert.Parameters.Add("$issued", SqliteType.Integer);
        var total = insert.Parameters.Add("$total", SqliteType.Real);
        var paid = insert.Parameters.Add("$paid", SqliteType.Integer);
        var notes = insert.Parameters.Add("$notes", SqliteType.Text);
        insert.Prepare();
        long count = 0;
        foreach (var row in Plain(rows, shuffled: false))
        {
            id.Value = row.Id.ToByteArray(bigEndian: true);
            customer.Value = row.Customer;
            issued.Value = row.IssuedAt.UtcTicks;
            total.Value = (double)row.Total;
            paid.Value = row.Paid ? 1 : 0;
            notes.Value = row.Notes;
            insert.ExecuteNonQuery();
            count++;
        }

        transaction.Commit();
        return count;
    }
}
