// Copyright (c) Tekium. Licensed under the MIT License.

using System.Globalization;
using TekiumDB.Sql;
using TekiumDB.Tests.Database;
using TekiumDB.Tests.Storage;
using static TekiumDB.Tests.Sql.SqlTests;

namespace TekiumDB.Tests.Sql;

/// <summary>
/// Funciones y procedimientos escritos en C# y llamados desde SQL. El generador escribe el código que
/// les pasa los argumentos; aquí se prueba que SQL los llama bien, y cuántas veces.
/// </summary>
public abstract class RoutineTests : DatabaseTests
{
    [Fact]
    public async Task Scalar_functions_take_and_return_values_of_every_supported_type()
    {
        await using var database = await OpenAsync(NewDevice());
        var (items, _) = await FillAsync(database);
        database.UseRoutines(Core.Tests.TkRoutines.All);
        await using var connection = new TkConnection(database);

        Assert.Equal(["hello--world-2"], await QueryAsync(connection, "SELECT slugify('Hello, World 2')"));
        Assert.Equal(["12.10"], await QueryAsync(connection, "SELECT with_tax(11, 0.1)"));
        Assert.Equal(
            items.Take(20).Select(static i => $"{i.Id}|{TestRoutines.Slugify(i.Owner + " " + i.Qty)}|{TestRoutines.WithTax(i.Price, 0.25).ToString(CultureInfo.InvariantCulture)}"),
            await QueryAsync(connection, "SELECT id, slugify(owner || ' ' || qty), with_tax(price, 0.25) FROM items WHERE id < 20 ORDER BY id"));

        // En el WHERE, en el ORDER BY, dentro de un agregado.
        Assert.Equal(
            items.Where(static i => TestRoutines.WithTax(i.Price, 0.5) > 140m).Select(static i => $"{i.Id}"),
            await QueryAsync(connection, "SELECT id FROM items WHERE with_tax(price, 0.5) > 140 ORDER BY id"));
        Assert.Equal(
            [$"{items.Sum(static i => TestRoutines.BigSum(i.Id, i.Qty))}"],
            await QueryAsync(connection, "SELECT SUM(big_sum(id, qty)) FROM items"));

        // NULL para un parámetro que no lo admite: no se llama a la función y el resultado es NULL. Para uno que sí, se le pasa.
        Assert.Equal(["||c|1005|7|"], await QueryAsync(connection, "SELECT slugify(NULL), initial(NULL), initial('code'), big_sum(5, NULL), positive(7), positive(-7)"));
        Assert.Equal(
            items.Where(static i => i.Id < 30).Select(static i => $"{i.Id}|{TestRoutines.Initial(i.Code)}"),
            await QueryAsync(connection, "SELECT id, initial(code) FROM items WHERE id < 30 ORDER BY id"));

        // Fechas, identificadores, bytes y banderas.
        var id = Guid.CreateVersion7();
        Assert.Equal(
            ["True|False|True|False|030201"],
            await QueryAsync(
                connection,
                "SELECT same_id(@id, @id), same_id(@id, @other), is_recent(@at, @since, FALSE), is_recent(@at, @since, TRUE), reversed(x'010203')",
                ("id", id), ("other", Guid.CreateVersion7()), ("at", new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero)), ("since", new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc))));
    }

    [Fact]
    public async Task A_deterministic_function_is_called_once_per_distinct_argument_and_folded_when_constant()
    {
        await using var database = await OpenAsync(NewDevice());
        var (items, _) = await FillAsync(database);
        database.UseRoutines(Core.Tests.TkRoutines.All);
        await using var connection = new TkConnection(database);

        // Argumentos constantes: una sola llamada para toda la consulta, aunque toque cuatrocientas filas.
        TestRoutines.Reset();
        Assert.Equal([$"{items.Count(static i => i.Qty < 84)}"], await QueryAsync(connection, "SELECT COUNT(*) FROM items WHERE qty < counted(@half)", ("half", 42L)));
        Assert.Equal(1, TestRoutines.CountedCalls);

        // Argumentos de la fila: una llamada por valor distinto.
        TestRoutines.Reset();
        Assert.Equal([$"{items.Sum(static i => i.Qty * 2L)}"], await QueryAsync(connection, "SELECT SUM(counted(qty)) FROM items"));
        Assert.Equal(items.Select(static i => i.Qty).Distinct().Count(), TestRoutines.CountedCalls);

        // Sin esa marca, una por fila; y con argumentos constantes, también.
        TestRoutines.Reset();
        Assert.Equal([$"{items.Sum(static i => i.Qty + 1L)}"], await QueryAsync(connection, "SELECT SUM(noisy(qty)) FROM items"));
        Assert.Equal(items.Count, TestRoutines.NoisyCalls);
        TestRoutines.Reset();
        await QueryAsync(connection, "SELECT noisy(1) FROM items WHERE id < 10");
        Assert.Equal(10, TestRoutines.NoisyCalls);
    }

    [Fact]
    public async Task A_vectorized_function_is_called_once_per_block_of_rows()
    {
        await using var database = await OpenAsync(NewDevice());
        database.UseRoutines(Core.Tests.TkRoutines.All);
        await using var connection = new TkConnection(database);
        await RunAsync(connection, "CREATE TABLE points (id BIGINT PRIMARY KEY, a DOUBLE, b DOUBLE, n INT NOT NULL, f REAL NOT NULL)");
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            for (var i = 0; i < 2500; i++)
            {
                await RunAsync(
                    connection,
                    "INSERT INTO points (id, a, b, n, f) VALUES (@id, @a, @b, @n, @f)",
                    ("id", (long)i), ("a", i % 7 == 0 ? null : i * 1.5), ("b", i * 0.5), ("n", i % 10), ("f", (float)i));
            }

            await transaction.CommitAsync(Ct);
        }

        // 2 500 filas: tres bloques de hasta 1 024, no 2 500 llamadas.
        TestRoutines.Reset();
        var deltas = await QueryAsync(connection, "SELECT id, abs_delta(a, b) FROM points ORDER BY id");
        Assert.Equal(3, TestRoutines.VectorCalls);
        Assert.Equal(
            Enumerable.Range(0, 2500).Select(static i => i % 7 == 0 ? $"{i}|" : $"{i}|{Math.Abs((i * 1.5) - (i * 0.5)).ToString(CultureInfo.InvariantCulture)}"),
            deltas);

        // Una función determinista deja de recordar resultados cuando los argumentos casi no se repiten: aquí,
        // al llegar a 1 024 distintos habiendo acertado menos veces que eso. Desde ahí, una llamada por fila.
        TestRoutines.Reset();
        Assert.Equal([$"{Enumerable.Range(0, 2500).Sum(static i => i / 2 * 2L)}"], await QueryAsync(connection, "SELECT SUM(counted(id / 2)) FROM points"));
        Assert.Equal(1024 + 453, TestRoutines.CountedCalls);

        // Pero si se repiten bastante sigue recordando, por muchos distintos que haya: cada valor llega cuatro veces.
        await RunAsync(connection, "CREATE TABLE twice (k INT PRIMARY KEY)");
        await RunAsync(connection, "INSERT INTO twice (k) VALUES (0), (1)");
        TestRoutines.Reset();
        Assert.Equal(
            [$"{Enumerable.Range(0, 2500).Sum(static i => i / 2 * 4L)}"],
            await QueryAsync(connection, "SELECT SUM(counted(p.id / 2)) FROM points p JOIN twice t ON t.k >= 0"));
        Assert.Equal(1250, TestRoutines.CountedCalls);

        // Otros tipos de elemento, constantes como argumento, una dentro de otra, y junto a un agregado.
        Assert.Equal(
            Enumerable.Range(0, 5).Select(static i => $"{(long)i * (i % 10)}|{(i / 2f).ToString(CultureInfo.InvariantCulture)}|{Math.Abs(Math.Abs((i * 1.5) - (i * 0.5)) - 3).ToString(CultureInfo.InvariantCulture)}")
                .Select(static (row, i) => i == 0 ? row[..row.LastIndexOf('|')] + "|" : row),
            await QueryAsync(connection, "SELECT scaled(id, n), halved(f), abs_delta(abs_delta(a, b), 3) FROM points WHERE id < 5 ORDER BY id"));
        Assert.Equal(
            [$"{Enumerable.Range(0, 2500).Sum(static i => (long)i * (i % 10))}"],
            await QueryAsync(connection, "SELECT SUM(scaled(id, n)) FROM points"));

        // Dentro de un agregado y como clave de grupo también va por bloques.
        TestRoutines.Reset();
        Assert.Equal(
            Enumerable.Range(0, 2500).Where(static i => i % 7 != 0).GroupBy(static i => i % 10)
                .Select(static g => $"{g.Key}|{g.Sum(static i => Math.Abs((i * 1.5) - (i * 0.5))).ToString(CultureInfo.InvariantCulture)}").Order(StringComparer.Ordinal),
            (await QueryAsync(connection, "SELECT n, SUM(abs_delta(a, b)) FROM points WHERE a IS NOT NULL GROUP BY n")).Order(StringComparer.Ordinal));
        Assert.Equal(3, TestRoutines.VectorCalls);

        // Fuera de la lista de columnas se la llama fila por fila, y da lo mismo.
        TestRoutines.Reset();
        Assert.Equal(
            [$"{Enumerable.Range(0, 2500).Count(static i => i % 7 != 0 && Math.Abs((i * 1.5) - (i * 0.5)) > 2000)}"],
            await QueryAsync(connection, "SELECT COUNT(*) FROM points WHERE abs_delta(a, b) > 2000"));
        Assert.Equal(2500, TestRoutines.VectorCalls);

        // Con LIMIT y sin orden no se leen todas las filas: un bloque basta.
        TestRoutines.Reset();
        Assert.Equal(3, (await QueryAsync(connection, "SELECT abs_delta(a, b) FROM points LIMIT 3")).Count);
        Assert.Equal(1, TestRoutines.VectorCalls);
    }

    [Fact]
    public async Task A_procedure_runs_inside_the_transaction_of_its_call()
    {
        await using var database = await OpenAsync(NewDevice());
        var (items, _) = await FillAsync(database);
        database.UseRoutines(Core.Tests.TkRoutines.All);
        await using var connection = new TkConnection(database);
        var small = items.Count(static i => i.Qty < 10);

        // En una transacción que se deshace: lo que hizo el procedimiento se va con ella.
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            Assert.Equal([$"{small}"], await QueryAsync(connection, "CALL drop_small(10)"));
            Assert.Equal([$"{items.Count - small}"], await QueryAsync(connection, "SELECT COUNT(*) FROM items"));
            await transaction.RollbackAsync(Ct);
        }

        Assert.Equal([$"{items.Count}"], await QueryAsync(connection, "SELECT COUNT(*) FROM items"));

        // Sola, confirma; con parámetros; y sin valor de retorno no devuelve filas.
        Assert.Equal([$"{small}"], await QueryAsync(connection, "CALL drop_small(@below)", ("below", 10)));
        Assert.Equal([$"{items.Count - small}"], await QueryAsync(connection, "SELECT COUNT(*) FROM items"));
        Assert.Empty(await QueryAsync(connection, "CALL rename_owner('owner3', 'renamed')"));
        Assert.Equal([$"{items.Count(static i => i.Owner == "owner3" && i.Qty >= 10)}"], await QueryAsync(connection, "SELECT COUNT(*) FROM items WHERE owner = 'renamed'"));
        await RunAsync(connection, "CALL rename_owner('owner4', NULL)");
        Assert.Equal([$"{items.Count(static i => i.Owner == "owner4" && i.Qty >= 10)}"], await QueryAsync(connection, "SELECT COUNT(*) FROM items WHERE owner = 'nobody'"));

        // SQL desde dentro del procedimiento, en la misma transacción.
        var owned = items.Count(static i => i.Owner == "owner5" && i.Qty >= 10);
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            Assert.Equal([$"{owned}"], await QueryAsync(connection, "CALL zero_owner('owner5')"));
            await transaction.RollbackAsync(Ct);
        }

        Assert.Equal(["0"], await QueryAsync(connection, "SELECT COUNT(*) FROM items WHERE owner = 'owner5' AND qty = 0"));
        Assert.Equal([$"{owned}"], await QueryAsync(connection, "CALL zero_owner('owner5')"));
        Assert.Equal([$"{owned}"], await QueryAsync(connection, "SELECT COUNT(*) FROM items WHERE owner = 'owner5' AND qty = 0"));

        // El procedimiento recibe el token de cancelación de la sentencia, no uno vacío.
        Assert.Equal(["True"], await QueryAsync(connection, "CALL can_cancel()"));

        await RefusedAsync<TkSqlException>(connection, "CALL drop_small(NULL)", "NULL for an argument that cannot be NULL");
        await RefusedAsync<TkSqlException>(connection, "CALL drop_small(1, 2)", "takes 1 arguments");
        await RefusedAsync<TkSqlException>(connection, "CALL missing()", "no procedure 'missing'");
        await RefusedAsync<TkSqlException>(connection, "CALL slugify('x')", "is a function, not a procedure");
        await RefusedAsync<TkSqlException>(connection, "SELECT drop_small(1)", "is a procedure");
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task A_function_the_application_did_not_register_is_an_error_before_any_row_is_read()
    {
        var device = NewDevice();
        await using var database = await OpenAsync(device);
        await FillAsync(database);
        await using var connection = new TkConnection(database);

        // Sin registrar: el error sale al preparar la consulta, sin tocar el archivo.
        var reads = device.ReadCount;
        await RefusedAsync<TkSqlException>(connection, "SELECT id FROM items WHERE slugify(owner) = 'x'", "Unknown function SLUGIFY");
        await RefusedAsync<TkSqlException>(connection, "SELECT id, slugify(owner) FROM items", "the functions the application registers");
        Assert.InRange(device.ReadCount - reads, 0, 4);

        database.UseRoutines(Core.Tests.TkRoutines.All);
        await RunAsync(connection, "CREATE VIEW slugs AS SELECT id, slugify(owner) AS slug FROM items");
        Assert.Equal(["owner" + (await QueryAsync(connection, "SELECT owner FROM items WHERE id = 0"))[0][5..]], await QueryAsync(connection, "SELECT slug FROM slugs WHERE id = 0"));
        await RefusedAsync<TkSqlException>(connection, "SELECT slugify(owner, 1) FROM items", "takes 1 arguments");
        await RefusedAsync<TkSqlException>(connection, "SELECT fails(qty) FROM items", "Function fails failed: too big");

        // Registrar lo mismo otra vez no hace nada; otra rutina con un nombre ya usado se rechaza.
        database.UseRoutines(Core.Tests.TkRoutines.All);
        Assert.Throws<ArgumentException>(() => database.UseRoutines([TkRoutine.Function("slugify", 1, false, static _ => null)]));

        // Las funciones viven en la aplicación: otra que abra el mismo archivo sin registrarlas no puede usar la vista.
        var copy = NewDevice();
        await database.BackupAsync(_ => copy, null, null, Ct);
        await using var other = await OpenAsync(copy);
        await using var elsewhere = new TkConnection(other);
        await RefusedAsync<TkSqlException>(elsewhere, "SELECT * FROM slugs", "Unknown function SLUGIFY");
    }
}

[Collection("routines")]
public sealed class RoutineTests_File : RoutineTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

[Collection("routines")]
public sealed class RoutineTests_Memory : RoutineTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
