// Copyright (c) Tekium. Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using Tekium.Embedded.Sql;

namespace Tekium.Embedded.Benchmarks;

/// <summary>
/// Lo que cuesta cambiar el esquema de una tabla con filas: cada cambio de ALTER TABLE sobre una tabla de
/// <c>rows</c> filas con dos índices, en un archivo, con su commit. Tiempo, memoria pedida, memoria del
/// proceso y lo que crece el archivo. Se corre con <c>-- rewrite [filas]</c>.
/// </summary>
internal static class RewriteReport
{
    public static async Task RunAsync(int rows)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rewrite-{Environment.ProcessId}.tkdb");
        try
        {
            var database = await TekiumDb.OpenAsync(new DbOptions { Path = path, CacheSizeMb = 256 });
            await using (database)
            {
                await using var connection = new TkConnection(database);
                await ExecuteAsync(connection, "CREATE TABLE stock (id BIGINT PRIMARY KEY, code TEXT NOT NULL, qty INT, price DECIMAL, note TEXT)");
                await ExecuteAsync(connection, "CREATE UNIQUE INDEX by_code ON stock (code)");
                await ExecuteAsync(connection, "CREATE INDEX by_qty ON stock (qty)");
                var watch = Stopwatch.StartNew();
                await using (var transaction = await connection.BeginTransactionAsync())
                {
                    await using var insert = connection.CreateCommand();
                    insert.CommandText = "INSERT INTO stock (id, code, qty, price, note) VALUES (@id, @code, @qty, @price, @note)";
                    var id = new TkParameter("id", 0L);
                    var code = new TkParameter("code", string.Empty);
                    var qty = new TkParameter("qty", 0);
                    var price = new TkParameter("price", 0m);
                    var note = new TkParameter("note", "a note of some length, as rows have");
                    insert.Parameters.Add(id);
                    insert.Parameters.Add(code);
                    insert.Parameters.Add(qty);
                    insert.Parameters.Add(price);
                    insert.Parameters.Add(note);
                    for (var i = 0; i < rows; i++)
                    {
                        id.Value = (long)i;
                        code.Value = string.Create(CultureInfo.InvariantCulture, $"c{i:D9}");
                        qty.Value = i % 1000;
                        price.Value = i / 100m;
                        await insert.ExecuteNonQueryAsync();
                    }

                    await transaction.CommitAsync();
                }

                await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"{rows:N0} filas, dos índices (uno único), en archivo. Cargadas en {watch.Elapsed.TotalSeconds:N1} s; archivo de {Size(path):N0} MB."));
                await Console.Out.WriteLineAsync("| Cambio | Tiempo | Por fila | Memoria pedida | Memoria residente (antes → pico) | El archivo crece |");
                await Console.Out.WriteLineAsync("| --- | ---: | ---: | ---: | ---: | ---: |");
                await MeasureAsync(connection, path, rows, "RENAME COLUMN (los índices sólo cambian de nombre)", "ALTER TABLE stock RENAME COLUMN note TO memo");
                await MeasureAsync(connection, path, rows, "ALTER COLUMN de INT a BIGINT, con su índice reconstruido", "ALTER TABLE stock ALTER COLUMN qty BIGINT");
                await MeasureAsync(connection, path, rows, "DROP COLUMN", "ALTER TABLE stock DROP COLUMN memo");
                await MeasureAsync(connection, path, rows, "ADD COLUMN sin DEFAULT (no reescribe)", "ALTER TABLE stock ADD COLUMN extra INT");
                await MeasureAsync(connection, path, rows, "ADD COLUMN con DEFAULT (Fase 17: reescribe)", "ALTER TABLE stock ADD COLUMN flag INT NOT NULL DEFAULT 1");
                await MeasureAsync(connection, path, rows, "RENAME TO (no mueve filas)", "ALTER TABLE stock RENAME TO goods");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task MeasureAsync(TkConnection connection, string path, int rows, string what, string sql)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = Size(path);
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        // The resident size, sampled while the change runs: PeakWorkingSet64 is not kept on every system.
        var start = Environment.WorkingSet;
        var peak = start;
        using var done = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!done.IsCancellationRequested)
            {
                peak = Math.Max(peak, Environment.WorkingSet);
                await Task.Delay(5).ConfigureAwait(false);
            }
        });
        var timer = Stopwatch.StartNew();
        await ExecuteAsync(connection, sql);
        timer.Stop();
        await done.CancelAsync();
        await sampler;
        var asked = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"| {what} | {timer.Elapsed.TotalSeconds:N2} s | {timer.Elapsed.TotalNanoseconds / rows / 1000:N2} µs | {asked / 1048576.0:N0} MB | {start / 1048576.0:N0} → {peak / 1048576.0:N0} MB | {Size(path) - before:N0} MB |"));
    }

    private static double Size(string path) => new FileInfo(path).Length / 1048576.0;

    private static async Task ExecuteAsync(TkConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
