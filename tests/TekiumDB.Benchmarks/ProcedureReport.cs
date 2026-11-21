// Copyright (c) Tekium. Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using TekiumDB.Sql;

namespace TekiumDB.Benchmarks;

/// <summary>
/// Procedimientos guardados: lo que cuesta una vuelta de bucle del intérprete sin tocar datos, y el mismo
/// trabajo sobre filas hecho por un procedimiento guardado, por uno de C# y sentencia por sentencia desde
/// la aplicación. Se corre con <c>-- procedures [filas]</c>.
/// </summary>
internal static class ProcedureReport
{
    private const int Rounds = 5;

    public static async Task RunAsync(int rows)
    {
        var database = await TekiumDb.OpenAsync(new DbOptions
        {
            InMemory = true, CacheSizeMb = 512, AllowStoredProcedures = true, ProcedureInstructionLimit = int.MaxValue,
        });
        await using (database)
        {
            database.UseRoutines(TkRoutines.All);
            await using var connection = new TkConnection(database);
            await ExecuteAsync(connection, "CREATE TABLE orders (id BIGINT PRIMARY KEY, total DOUBLE NOT NULL, placed INT NOT NULL)");
            await ExecuteAsync(connection, "CREATE TABLE orders_archive (id BIGINT PRIMARY KEY, total DOUBLE NOT NULL)");
            await ExecuteAsync(connection, """
                CREATE PROCEDURE spin(n INT) RETURNS BIGINT AS $$
                DECLARE i INT := 0; total BIGINT := 0;
                BEGIN
                    WHILE i < n LOOP
                        total := total + i;
                        i := i + 1;
                    END LOOP;
                    RETURN total;
                END $$
                """);
            await ExecuteAsync(connection, """
                CREATE PROCEDURE archive_stored() RETURNS INT AS $$
                DECLARE n INT := 0;
                BEGIN
                    FOR r IN SELECT id, total FROM orders LOOP
                        INSERT INTO orders_archive (id, total) VALUES (r.id, r.total);
                        DELETE FROM orders WHERE id = r.id;
                        n := n + 1;
                    END LOOP;
                    RETURN n;
                END $$
                """);

            await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"En memoria; mejor de {Rounds} corridas."));
            await Console.Out.WriteLineAsync("| Qué | Tiempo | Por vuelta |");
            await Console.Out.WriteLineAsync("| --- | ---: | ---: |");

            const int Spins = 1_000_000;
            var spin = await BestAsync(connection, static _ => Task.CompletedTask, connection => ExecuteAsync(connection, $"CALL spin({Spins})"));
            await LineAsync($"Bucle vacío: {Spins:N0} vueltas (una condición y dos asignaciones)", spin, Spins);

            // Las tres formas se alternan, con una vuelta de calentamiento que no cuenta: la que corre primero
            // paga la base todavía creciendo, y medidas una detrás de otra la primera sale el doble de lenta.
            (string Name, Func<TkConnection, Task> Work)[] ways =
            [
                ("procedimiento guardado", static connection => ExecuteAsync(connection, "CALL archive_stored()")),
                ("procedimiento de C# con las mismas sentencias", static connection => ExecuteAsync(connection, "CALL archive_csharp()")),
                ("sentencia por sentencia desde la aplicación", ArchiveFromClientAsync),
            ];
            var best = new double[ways.Length];
            Array.Fill(best, double.MaxValue);
            for (var round = 0; round <= Rounds; round++)
            {
                for (var way = 0; way < ways.Length; way++)
                {
                    await FillAsync(connection, rows);
                    var clock = Stopwatch.StartNew();
                    await ways[way].Work(connection);
                    if (round > 0)
                    {
                        best[way] = Math.Min(best[way], clock.Elapsed.TotalMilliseconds);
                    }
                }
            }

            for (var way = 0; way < ways.Length; way++)
            {
                await LineAsync($"Archivar {rows:N0} filas: {ways[way].Name}", best[way], rows);
            }
        }
    }

    /// <summary>El mismo trabajo que <c>archive_stored</c>, con las mismas sentencias, escrito en C#.</summary>
    [TkProcedure("archive_csharp")]
    public static async ValueTask<int> ArchiveAsync(TkContext context, CancellationToken cancellationToken)
    {
        var n = 0;
        foreach (var row in await context.QueryAsync("SELECT id, total FROM orders", null, cancellationToken))
        {
            var values = new Dictionary<string, object?> { ["id"] = row[0], ["total"] = row[1] };
            await context.ExecuteAsync("INSERT INTO orders_archive (id, total) VALUES (@id, @total)", values, cancellationToken);
            await context.ExecuteAsync("DELETE FROM orders WHERE id = @id", values, cancellationToken);
            n++;
        }

        return n;
    }

    private static async Task ArchiveFromClientAsync(TkConnection connection)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        var rows = new List<(long Id, double Total)>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT id, total FROM orders";
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add((reader.GetInt64(0), reader.GetDouble(1)));
            }
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO orders_archive (id, total) VALUES (@id, @total)";
        var insertId = new TkParameter("id", 0L);
        var insertTotal = new TkParameter("total", 0d);
        insert.Parameters.Add(insertId);
        insert.Parameters.Add(insertTotal);
        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM orders WHERE id = @id";
        var deleteId = new TkParameter("id", 0L);
        delete.Parameters.Add(deleteId);
        foreach (var (id, total) in rows)
        {
            insertId.Value = id;
            insertTotal.Value = total;
            await insert.ExecuteNonQueryAsync();
            deleteId.Value = id;
            await delete.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task FillAsync(TkConnection connection, int rows)
    {
        await ExecuteAsync(connection, "DELETE FROM orders");
        await ExecuteAsync(connection, "DELETE FROM orders_archive");
        await using var transaction = await connection.BeginTransactionAsync();
        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO orders (id, total, placed) VALUES (@id, @total, @placed)";
        var id = new TkParameter("id", 0L);
        var total = new TkParameter("total", 0d);
        var placed = new TkParameter("placed", 0);
        insert.Parameters.Add(id);
        insert.Parameters.Add(total);
        insert.Parameters.Add(placed);
        for (var i = 0; i < rows; i++)
        {
            id.Value = (long)i;
            total.Value = i * 1.5;
            placed.Value = i % 30;
            await insert.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task<double> BestAsync(TkConnection connection, Func<TkConnection, Task> prepare, Func<TkConnection, Task> work)
    {
        var best = double.MaxValue;
        for (var round = 0; round < Rounds; round++)
        {
            await prepare(connection);
            var clock = Stopwatch.StartNew();
            await work(connection);
            best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
        }

        return best;
    }

    private static Task LineAsync(string what, double milliseconds, int turns) =>
        Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"| {what} | {milliseconds:N1} ms | {milliseconds * 1_000_000 / turns:N0} ns |"));

    private static async Task ExecuteAsync(TkConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
