// Copyright (c) Tekium. Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.Numerics.Tensors;
using Tekium.Embedded.Sql;

namespace Tekium.Embedded.Benchmarks;

/// <summary>
/// Funciones de C# llamadas desde SQL: lo que cuesta por fila una función escalar, una vectorizada y la
/// misma cuenta escrita en SQL, y lo que ahorra marcar una función como determinista. Se corre con
/// <c>-- routines [filas]</c>.
/// </summary>
internal static class RoutineReport
{
    private const int Rounds = 5;

    public static async Task RunAsync(int rows)
    {
        var database = await TekiumDb.OpenAsync(new DbOptions { InMemory = true, CacheSizeMb = 1024 });
        await using (database)
        {
            database.UseRoutines(TkRoutines.All);
            await using var connection = new TkConnection(database);
            await ScalarAsync(connection, "CREATE TABLE readings (id BIGINT PRIMARY KEY, a DOUBLE NOT NULL, b DOUBLE NOT NULL, kind INT NOT NULL)");
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO readings (id, a, b, kind) VALUES (@id, @a, @b, @kind)";
                var id = new TkParameter("id", 0L);
                var a = new TkParameter("a", 0d);
                var b = new TkParameter("b", 0d);
                var kind = new TkParameter("kind", 0);
                insert.Parameters.Add(id);
                insert.Parameters.Add(a);
                insert.Parameters.Add(b);
                insert.Parameters.Add(kind);
                for (var i = 0; i < rows; i++)
                {
                    id.Value = (long)i;
                    a.Value = i * 1.5;
                    b.Value = i * 0.25;
                    kind.Value = i % 100;
                    await insert.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();
            }

            await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{rows:N0} filas en memoria; mejor de {Rounds} corridas."));
            await Console.Out.WriteLineAsync("| Consulta | Tiempo | Por fila | Sobre el recorrido |");
            await Console.Out.WriteLineAsync("| --- | ---: | ---: | ---: |");
            var scan = await MeasureAsync(connection, "Sólo recorrer: `SUM(a)`", "SELECT SUM(a) FROM readings", rows, 0);
            await MeasureAsync(connection, "Dos columnas: `SUM(a - b)`", "SELECT SUM(a - b) FROM readings", rows, scan);
            await MeasureAsync(connection, "Escrita en SQL: `SUM(ABS(a - b))`", "SELECT SUM(ABS(a - b)) FROM readings", rows, scan);
            await MeasureAsync(connection, "Función escalar", "SELECT SUM(delta_scalar(a, b)) FROM readings", rows, scan);
            await MeasureAsync(connection, "Función escalar determinista, argumentos todos distintos", "SELECT SUM(delta_once(a, b)) FROM readings", rows, scan);
            await MeasureAsync(connection, "Función vectorizada", "SELECT SUM(delta_vector(a, b)) FROM readings", rows, scan);
            await MeasureAsync(connection, "Vectorizada, llamada fila por fila (en el WHERE)", "SELECT COUNT(*) FROM readings WHERE delta_vector(a, b) >= 0", rows, scan);
            await MeasureAsync(connection, "Función cara, 100 argumentos distintos", "SELECT SUM(costly(kind)) FROM readings", rows, scan);
            await MeasureAsync(connection, "La misma, marcada determinista", "SELECT SUM(costly_once(kind)) FROM readings", rows, scan);
        }
    }

    [TkFunction("delta_scalar")]
    public static double DeltaScalar(double a, double b) => Math.Abs(a - b);

    [TkFunction("delta_once", Deterministic = true)]
    public static double DeltaOnce(double a, double b) => Math.Abs(a - b);

    [TkFunction("delta_vector", Vectorized = true)]
    public static void DeltaVector(ReadOnlySpan<double> a, ReadOnlySpan<double> b, Span<double> result)
    {
        TensorPrimitives.Subtract(a, b, result);
        TensorPrimitives.Abs(result, result);
    }

    [TkFunction("costly")]
    public static double Costly(int kind)
    {
        var total = 0d;
        for (var i = 1; i <= 200; i++)
        {
            total += Math.Sqrt(kind + i);
        }

        return total;
    }

    [TkFunction("costly_once", Deterministic = true)]
    public static double CostlyOnce(int kind) => Costly(kind);

    private static async Task<double> MeasureAsync(TkConnection connection, string name, string sql, int rows, double scan)
    {
        var best = double.MaxValue;
        for (var round = 0; round < Rounds; round++)
        {
            var clock = Stopwatch.StartNew();
            await ScalarAsync(connection, sql);
            best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
        }

        var perRow = best * 1_000_000 / rows;
        var over = scan > 0 ? string.Create(CultureInfo.InvariantCulture, $"{(best - scan) * 1_000_000 / rows:N0} ns") : "—";
        await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"| {name} | {best:N0} ms | {perRow:N0} ns | {over} |"));
        return best;
    }

    private static async Task<object?> ScalarAsync(TkConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
