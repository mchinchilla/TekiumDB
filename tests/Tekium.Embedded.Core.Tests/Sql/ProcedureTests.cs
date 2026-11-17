// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Catalog;
using Tekium.Embedded.Sql;
using Tekium.Embedded.Tests.Database;
using Tekium.Embedded.Tests.Storage;
using static Tekium.Embedded.Tests.Sql.SqlTests;

namespace Tekium.Embedded.Tests.Sql;

/// <summary>
/// Procedimientos guardados en el archivo: se compilan al crearlos, se guardan como programa en el
/// catálogo y los corre una máquina virtual que sólo sabe calcular, correr SQL y llamar procedimientos.
/// </summary>
public abstract class ProcedureTests : DatabaseTests
{
    private const string Archive = """
        CREATE PROCEDURE archive_orders(p_before INT) RETURNS INT AS $$
        DECLARE n INT := 0;
        BEGIN
            FOR r IN SELECT id, total FROM orders WHERE placed < p_before LOOP
                INSERT INTO orders_archive (id, total) VALUES (r.id, r.total);
                DELETE FROM orders WHERE id = r.id;
                n := n + 1;
            END LOOP;
            RETURN n;
        END $$
        """;

    [Fact]
    public async Task A_procedure_with_a_loop_and_embedded_sql_runs_and_is_kept_in_the_file()
    {
        var device = NewDevice();
        await using (var database = await OpenWithAsync(device))
        {
            await using var connection = new TkConnection(database);
            await OrdersAsync(connection);
            await RunAsync(connection, Archive);

            // En una transacción que se deshace: lo que hizo se va con ella.
            await using (var transaction = await connection.BeginTransactionAsync(Ct))
            {
                Assert.Equal(["100"], await QueryAsync(connection, "CALL archive_orders(10)"));
                Assert.Equal(["200"], await QueryAsync(connection, "SELECT COUNT(*) FROM orders"));
                Assert.Equal(["100"], await QueryAsync(connection, "SELECT COUNT(*) FROM orders_archive"));
                await transaction.RollbackAsync(Ct);
            }

            Assert.Equal(["300"], await QueryAsync(connection, "SELECT COUNT(*) FROM orders"));
            Assert.Equal(["0"], await QueryAsync(connection, "SELECT COUNT(*) FROM orders_archive"));

            // Sola, confirma. Las filas archivadas son exactamente las que cumplían la condición.
            Assert.Equal(["100"], await QueryAsync(connection, "CALL archive_orders(@before)", ("before", 10)));
            Assert.Equal(
                Enumerable.Range(0, 300).Where(static i => i % 30 < 10).Select(static i => $"{i}|{i * 1.5m}"),
                await QueryAsync(connection, "SELECT id, total FROM orders_archive ORDER BY id"));
            Assert.Equal(
                Enumerable.Range(0, 300).Where(static i => i % 30 >= 10).Select(static i => $"{i}"),
                await QueryAsync(connection, "SELECT id FROM orders ORDER BY id"));
            Assert.Equal(["0"], await QueryAsync(connection, "CALL archive_orders(10)"));
            Assert.Empty((await database.VerifyAsync(Ct)).Findings);
            await AssertNoLeaksAsync(database);
        }

        // Está en el archivo: otra apertura lo encuentra y lo corre, sin volver a crearlo.
        await using (var reopened = await OpenWithAsync(device))
        {
            await using var connection = new TkConnection(reopened);
            Assert.Equal(["100"], await QueryAsync(connection, "CALL Archive_Orders(20)"));
            Assert.Equal(["100"], await QueryAsync(connection, "SELECT COUNT(*) FROM orders"));

            // Y viaja con un respaldo.
            var copy = NewDevice();
            await reopened.BackupAsync(_ => copy, null, null, Ct);
            await using var restored = await OpenWithAsync(copy);
            await using var elsewhere = new TkConnection(restored);
            Assert.Equal(["100"], await QueryAsync(elsewhere, "CALL archive_orders(30)"));
            Assert.Equal(["0"], await QueryAsync(elsewhere, "SELECT COUNT(*) FROM orders"));
            Assert.Equal(["100"], await QueryAsync(connection, "SELECT COUNT(*) FROM orders"));
        }
    }

    [Fact]
    public async Task The_instruction_limit_stops_a_loop_that_never_ends()
    {
        await using var database = await OpenWithAsync(NewDevice(), limit: 20_000);
        database.UseRoutines(Core.Tests.TkRoutines.All);
        await using var connection = new TkConnection(database);
        await OrdersAsync(connection);
        await RunAsync(connection, "CREATE PROCEDURE spin() AS $$ DECLARE n INT := 0; BEGIN WHILE TRUE LOOP n := n + 1; END LOOP; END $$");
        await RefusedAsync<TkSqlException>(connection, "CALL spin()", "was stopped after 20000 instructions");

        // Lo que escribió antes de que lo cortaran no queda.
        await RunAsync(connection, """
            CREATE PROCEDURE flood() AS $$
            DECLARE n BIGINT := 1000;
            BEGIN
                WHILE n > 0 LOOP
                    INSERT INTO orders (id, total, placed) VALUES (n, 1, 1);
                    n := n + 1;
                END LOOP;
            END $$
            """);
        await RefusedAsync<TkSqlException>(connection, "CALL flood()", "DbOptions.ProcedureInstructionLimit");
        Assert.Equal(["300"], await QueryAsync(connection, "SELECT COUNT(*) FROM orders"));

        // Dentro de una transacción de la aplicación, un procedimiento que falla a medias la deja sin poder confirmarse.
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await RefusedAsync<TkSqlException>(connection, "CALL flood()", "was stopped");
            await Assert.ThrowsAnyAsync<Exception>(() => transaction.CommitAsync(Ct));
        }

        Assert.Equal(["300"], await QueryAsync(connection, "SELECT COUNT(*) FROM orders"));

        // El presupuesto es de la llamada entera: lo gastan también los procedimientos que ella llama.
        await RunAsync(connection, "CREATE PROCEDURE outer_spin() AS $$ BEGIN CALL spin(); END $$");
        await RefusedAsync<TkSqlException>(connection, "CALL outer_spin()", "Procedure spin was stopped");

        // Dos llamados que cada uno cabe en el presupuesto, pero juntos no: se cortan.
        await RunAsync(connection, "CREATE PROCEDURE busy() RETURNS INT AS $$ DECLARE n INT := 0; BEGIN WHILE n < 3000 LOOP n := n + 1; END LOOP; RETURN n; END $$");
        await RunAsync(connection, "CREATE PROCEDURE both() AS $$ DECLARE a INT; BEGIN CALL busy() INTO a; CALL busy() INTO a; END $$");
        await RefusedAsync<TkSqlException>(connection, "CALL both()", "was stopped after 20000 instructions");

        // Y cada llamada tiene el suyo: una que termina sigue funcionando después.
        Assert.Equal(["3000"], await QueryAsync(connection, "CALL busy()"));
        Assert.Equal(["3000"], await QueryAsync(connection, "CALL busy()"));

        // El de una llamada no pasa a la siguiente, aunque vayan en la misma transacción: dos que juntas lo exceden, corren.
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            Assert.Equal(["3000"], await QueryAsync(connection, "CALL busy()"));
            Assert.Equal(["3000"], await QueryAsync(connection, "CALL busy()"));
            await transaction.CommitAsync(Ct);
        }

        // La profundidad es cuántos hay uno dentro de otro, no cuántos se han llamado: veinte seguidos no la agotan.
        await RunAsync(connection, "CREATE PROCEDURE one() RETURNS INT AS $$ BEGIN RETURN 1; END $$");
        await RunAsync(connection, """
            CREATE PROCEDURE twenty() RETURNS INT AS $$
            DECLARE i INT := 0; total INT := 0; got INT;
            BEGIN
                WHILE i < 20 LOOP CALL one() INTO got; total := total + got; i := i + 1; END LOOP;
                RETURN total;
            END $$
            """);
        Assert.Equal(["20"], await QueryAsync(connection, "CALL twenty()"));

        // La recursión sin fin se corta por profundidad, directa o pasando por un procedimiento de C#.
        await RunAsync(connection, "CREATE PROCEDURE down(n INT) AS $$ BEGIN CALL down(n + 1); END $$");
        await RefusedAsync<TkSqlException>(connection, "CALL down(1)", "more than 8 deep");
        // La mayor profundidad que se puede pedir se alcanza sin agotar la pila; más no se acepta.
        await using (var deep = await OpenWithAsync(NewDevice(), depth: DbOptions.MaxProcedureCallDepth))
        {
            await using var deeply = new TkConnection(deep);
            await RunAsync(deeply, "CREATE PROCEDURE down(n INT) AS $$ BEGIN CALL down(n + 1); END $$");
            await RefusedAsync<TkSqlException>(deeply, "CALL down(1)", "more than 256 deep");
        }

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await OpenWithAsync(NewDevice(), depth: DbOptions.MaxProcedureCallDepth + 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await OpenWithAsync(NewDevice(), limit: 0));

        await RunAsync(connection, "CREATE PROCEDURE bounce() AS $$ BEGIN CALL relay(); END $$");
        await RefusedAsync<TkSqlException>(connection, "CALL bounce()", "DbOptions.ProcedureCallDepth");
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task Control_flow_variables_and_calls_behave_like_the_same_code_in_csharp()
    {
        await using var database = await OpenWithAsync(NewDevice());
        var (items, _) = await FillAsync(database);
        database.UseRoutines(Core.Tests.TkRoutines.All);
        await using var connection = new TkConnection(database);

        await RunAsync(connection, """
            CREATE PROCEDURE classify(n INT) RETURNS TEXT AS $$
            BEGIN
                IF n < 0 THEN RETURN 'negative';
                ELSIF n = 0 THEN RETURN 'zero';
                ELSIF n < 10 THEN RETURN 'small';
                ELSE RETURN 'large';
                END IF;
            END $$
            """);
        foreach (var n in new[] { -5, 0, 3, 9, 10, 500 })
        {
            Assert.Equal([n < 0 ? "negative" : n == 0 ? "zero" : n < 10 ? "small" : "large"], await QueryAsync(connection, "CALL classify(@n)", ("n", n)));
        }

        // Con NULL ninguna condición es verdadera: cae en el ELSE.
        Assert.Equal(["large"], await QueryAsync(connection, "CALL classify(NULL)"));

        await RunAsync(connection, """
            CREATE PROCEDURE sum_to(n INT) RETURNS BIGINT AS $$
            DECLARE total BIGINT := 0; i INT := 1;
            BEGIN
                WHILE i <= n LOOP
                    IF i % 3 = 0 THEN total := total + i * 2; ELSE total := total + i; END IF;
                    i := i + 1;
                END LOOP;
                RETURN total;
            END $$
            """);
        Assert.Equal([$"{Enumerable.Range(1, 200).Sum(static i => i % 3 == 0 ? i * 2L : i)}"], await QueryAsync(connection, "CALL sum_to(200)"));
        Assert.Equal(["0"], await QueryAsync(connection, "CALL sum_to(0)"));

        // Un procedimiento llama a otro, y a sí mismo.
        await RunAsync(connection, """
            CREATE PROCEDURE fact(n INT) RETURNS BIGINT AS $$
            DECLARE rest BIGINT;
            BEGIN
                IF n <= 1 THEN RETURN 1; END IF;
                CALL fact(n - 1) INTO rest;
                RETURN n * rest;
            END $$
            """);
        Assert.Equal(["120"], await QueryAsync(connection, "CALL fact(5)"));

        // SELECT … INTO: la primera fila, o NULL si no hay ninguna.
        await RunAsync(connection, """
            CREATE PROCEDURE stats(who TEXT) RETURNS BIGINT AS $$
            DECLARE c BIGINT; m INT;
            BEGIN
                SELECT COUNT(*), MAX(qty) INTO c, m FROM items WHERE owner = who;
                RETURN c * 1000 + m;
            END $$
            """);
        var owned = items.Where(static i => i.Owner == "owner3").ToList();
        Assert.Equal([$"{(owned.Count * 1000L) + owned.Max(static i => i.Qty)}"], await QueryAsync(connection, "CALL stats('owner3')"));
        Assert.Empty(await QueryAsync(connection, "CALL stats('nobody')"));

        // Sin ninguna fila, SELECT … INTO deja NULL, no lo que la variable tenía.
        await RunAsync(connection, """
            CREATE PROCEDURE first_qty(who TEXT) RETURNS INT AS $$
            DECLARE q INT := -1;
            BEGIN
                SELECT qty INTO q FROM items WHERE owner = who ORDER BY id LIMIT 1;
                RETURN q;
            END $$
            """);
        Assert.Equal([$"{owned.MinBy(static i => i.Id)!.Qty}"], await QueryAsync(connection, "CALL first_qty('owner3')"));
        Assert.Empty(await QueryAsync(connection, "CALL first_qty('nobody')"));

        // Dos bucles seguidos pueden llamar igual a su fila.
        await RunAsync(connection, """
            CREATE PROCEDURE twice(top INT) RETURNS BIGINT AS $$
            DECLARE total BIGINT := 0;
            BEGIN
                FOR r IN SELECT id FROM items WHERE id < top LOOP total := total + r.id; END LOOP;
                FOR r IN SELECT id FROM items WHERE id < top LOOP total := total + r.id; END LOOP;
                RETURN total;
            END $$
            """);
        Assert.Equal([$"{items.Where(static i => i.Id < 25).Sum(static i => i.Id) * 2}"], await QueryAsync(connection, "CALL twice(25)"));

        // Bucles anidados sobre consultas, con la fila del de afuera leída desde el de adentro, y RAISE.
        await RunAsync(connection, """
            CREATE PROCEDURE pairs(top INT) RETURNS BIGINT AS $$
            DECLARE total BIGINT := 0;
            BEGIN
                FOR a IN SELECT id, qty FROM items WHERE id < top LOOP
                    FOR b IN SELECT id AS other FROM items WHERE id < a.id AND id >= a.id - 2 LOOP
                        total := total + a.qty + b.other;
                    END LOOP;
                    IF a.qty < 0 THEN RAISE 'negative quantity in ' || a.id; END IF;
                END LOOP;
                RETURN total;
            END $$
            """);
        var expected = 0L;
        foreach (var a in items.Where(static i => i.Id < 40))
        {
            expected += items.Where(b => b.Id < a.Id && b.Id >= a.Id - 2).Sum(b => a.Qty + b.Id);
        }

        Assert.Equal([$"{expected}"], await QueryAsync(connection, "CALL pairs(40)"));
        await RunAsync(connection, "CREATE PROCEDURE refuse(what TEXT) AS $$ BEGIN RAISE EXCEPTION 'refused: ' || what; END $$");
        await RefusedAsync<TkSqlException>(connection, "CALL refuse('this')", "refused: this");

        // Funciones y procedimientos de C# desde dentro; y lo que escriben va en la misma transacción.
        await RunAsync(connection, """
            CREATE PROCEDURE tidy(below INT) RETURNS TEXT AS $$
            DECLARE dropped INT;
            BEGIN
                CALL drop_small(below) INTO dropped;
                RETURN slugify('Dropped ' || dropped);
            END $$
            """);
        var small = items.Count(static i => i.Qty < 10);
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            Assert.Equal([$"dropped-{small}"], await QueryAsync(connection, "CALL tidy(10)"));
            await transaction.RollbackAsync(Ct);
        }

        Assert.Equal([$"{items.Count}"], await QueryAsync(connection, "SELECT COUNT(*) FROM items"));

        // Una variable convierte lo que se le asigna a su tipo, o lo rechaza; y los errores de cuenta no escapan como otra cosa.
        await RunAsync(connection, "CREATE PROCEDURE typed(v DOUBLE) RETURNS INT AS $$ DECLARE n INT; BEGIN n := v; RETURN n + 1; END $$");
        Assert.Equal(["43"], await QueryAsync(connection, "CALL typed(42)"));
        await using (var command = connection.CreateCommand())
        {
            // El resultado es del tipo que el procedimiento declara, no del que salió de la cuenta.
            command.CommandText = "CALL typed(42)";
            Assert.IsType<int>(await command.ExecuteScalarAsync(Ct));
        }

        await RefusedAsync<TkSqlException>(connection, "CALL typed(99999999999)", "'n'");
        await RefusedAsync<TkSqlException>(connection, "CALL typed('forty')", "'v'");
        await RunAsync(connection, "CREATE PROCEDURE ratio(a INT, b INT) RETURNS INT AS $$ BEGIN RETURN a / b; END $$");
        Assert.Equal(["3"], await QueryAsync(connection, "CALL ratio(7, 2)"));
        await RefusedAsync<TkSqlException>(connection, "CALL ratio(7, 0)", "Division by zero");
        await RefusedAsync<TkSqlException>(connection, "CALL ratio(7)", "takes 2 arguments");
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task What_is_wrong_with_a_procedure_is_an_error_when_it_is_created()
    {
        await using var database = await OpenWithAsync(NewDevice());
        await FillAsync(database);
        database.UseRoutines(Core.Tests.TkRoutines.All);
        await using var connection = new TkConnection(database);

        (string Body, string Mention)[] wrong =
        [
            ("BEGIN n := 1; END", "no variable 'n'"),
            ("BEGIN RETURN 1; END", "does not declare RETURNS"),
            ("DECLARE a INT; a INT; BEGIN a := 1; END", "more than once"),
            ("BEGIN CREATE TABLE t (id INT PRIMARY KEY); END", "cannot be used in a procedure"),
            ("BEGIN SELECT id FROM items; END", "does nothing with its rows"),
            ("BEGIN FOR r IN SELECT * FROM items LOOP DELETE FROM items WHERE id = 1; END LOOP; END", "must name each of its columns"),
            ("BEGIN FOR r IN SELECT qty + 1 FROM items LOOP DELETE FROM items WHERE id = 1; END LOOP; END", "must name each of its columns"),
            ("DECLARE x INT; BEGIN x := y + 1; END", "no variable 'y'"),
            ("DECLARE x INT; BEGIN FOR r IN SELECT id FROM items LOOP x := r.qty; END LOOP; END", "has no column 'qty'"),
            ("DECLARE x INT; BEGIN FOR r IN SELECT id FROM items LOOP x := 1; END LOOP; x := r.id; END", "no variable 'r.id'"),
            ("DECLARE r INT; BEGIN FOR r IN SELECT id FROM items LOOP r := 1; END LOOP; END", "needs a name of its own"),
            ("DECLARE x INT; BEGIN FOR r IN SELECT id FROM items LOOP FOR r IN SELECT id FROM items LOOP x := r.id; END LOOP; END LOOP; END", "used by another FOR loop"),
            ("DECLARE x INT; BEGIN FOR r IN SELECT id FROM items LOOP x := 1; END LOOP; FOR r IN SELECT qty FROM items LOOP x := 1; END LOOP; END", "used by another FOR loop"),
            ("DECLARE qty INT := 5; BEGIN UPDATE items SET price = 1 WHERE qty > 5; END", "is a variable of the procedure and a column of table 'items'"),
            ("BEGIN IF 1 = 1 THEN END", "Expected"),
            ("DECLARE x INT; BEGIN x := @p; END", "no parameters like @p"),
            ("DECLARE x INT; BEGIN FOR r IN SELECT id INTO x FROM items LOOP x := 1; END LOOP; END", "cannot have INTO"),
            ("DECLARE x INT; BEGIN x := COUNT(*); END", "aggregate"),
            ("DECLARE a INT; b INT; BEGIN SELECT id INTO a, b FROM items; END", "as many columns as variables"),
            ("DECLARE x INT; BEGIN x := nowhere(1); END", "Unknown function NOWHERE"),
            ("DECLARE x INT; BEGIN x := 1 END", "Expected ;"),
            ("BEGIN CALL classify(1) INTO missing; END", "no variable 'missing'"),
        ];
        foreach (var (body, mention) in wrong)
        {
            await RefusedAsync<TkSqlException>(connection, $"CREATE PROCEDURE broken() AS $$ {body} $$", mention);
            await RefusedAsync<TkSqlException>(connection, "CALL broken()", "no procedure 'broken'");
        }

        await RefusedAsync<TkSqlException>(connection, "CREATE PROCEDURE broken() AS BEGIN END", "between $$ and $$");

        // INTO es del lenguaje de los procedimientos: una consulta suelta no lo tiene.
        await RefusedAsync<TkSqlException>(connection, "SELECT id INTO x FROM items", "Expected");
        await RefusedAsync<TkSqlException>(connection, "CREATE PROCEDURE broken() AS $$ BEGIN END", "no closing $$");
        await RefusedAsync<TkSqlException>(connection, "CREATE PROCEDURE drop_small() AS $$ BEGIN RETURN; END $$", "registered a routine named 'drop_small'");

        // Crear, reemplazar y quitar. Lo que cambia una transacción deshecha no queda, tampoco en lo ya cargado.
        await RunAsync(connection, "CREATE PROCEDURE answer() RETURNS INT AS $$ BEGIN RETURN 1; END $$");
        await RefusedAsync<TkSqlException>(connection, "CREATE PROCEDURE ANSWER() RETURNS INT AS $$ BEGIN RETURN 2; END $$", "already a procedure");
        Assert.Equal(["1"], await QueryAsync(connection, "CALL answer()"));
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await RunAsync(connection, "CREATE OR REPLACE PROCEDURE answer() RETURNS INT AS $$ BEGIN RETURN 2; END $$");
            Assert.Equal(["2"], await QueryAsync(connection, "CALL answer()"));
            await transaction.RollbackAsync(Ct);
        }

        Assert.Equal(["1"], await QueryAsync(connection, "CALL answer()"));
        await RunAsync(connection, "CREATE OR REPLACE PROCEDURE answer() RETURNS INT AS $$ BEGIN RETURN 3; END $$");
        Assert.Equal(["3"], await QueryAsync(connection, "CALL Answer()"));
        await RunAsync(connection, "DROP PROCEDURE answer");
        await RefusedAsync<TkSqlException>(connection, "CALL answer()", "no procedure 'answer'");
        await RefusedAsync<TkSqlException>(connection, "DROP PROCEDURE answer", "no procedure 'answer'");
        await RunAsync(connection, "DROP PROCEDURE IF EXISTS answer");
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task Stored_procedures_are_off_unless_the_application_turns_them_on()
    {
        var device = NewDevice();
        await using (var database = await OpenWithAsync(device))
        {
            await using var connection = new TkConnection(database);
            await RunAsync(connection, "CREATE PROCEDURE answer() RETURNS INT AS $$ BEGIN RETURN 42; END $$");
            Assert.Equal(["42"], await QueryAsync(connection, "CALL answer()"));
        }

        // Abierta como se abre por omisión, la base no corre lo que el archivo trae, ni acepta más.
        await using (var database = await OpenAsync(device))
        {
            await using var connection = new TkConnection(database);
            await RefusedAsync<TkSqlException>(connection, "CALL answer()", "Stored procedures are off");
            await RefusedAsync<TkSqlException>(connection, "CREATE PROCEDURE other() AS $$ BEGIN RETURN; END $$", "DbOptions.AllowStoredProcedures");
            await RefusedAsync<TkSqlException>(connection, "CALL nowhere()", "no procedure 'nowhere'");

            // Quitarlo sí se puede: no corre nada.
            await RunAsync(connection, "DROP PROCEDURE answer");
        }

        await using var reopened = await OpenWithAsync(device);
        await using var again = new TkConnection(reopened);
        await RefusedAsync<TkSqlException>(again, "CALL answer()", "no procedure 'answer'");
    }

    [Fact]
    public async Task A_damaged_program_is_refused_or_runs_within_its_limits_and_never_breaks_the_engine()
    {
        await using var database = await OpenWithAsync(NewDevice(), limit: 5_000);
        await using var connection = new TkConnection(database);
        await OrdersAsync(connection);
        await RunAsync(connection, Archive);
        byte[] program;
        await using (var read = database.BeginRead())
        {
            program = await CatalogStore.ReadAsync(read.Source, read.CatalogRoot, CatalogStore.ProcedureKey("archive_orders"), Ct);
        }

        // Un byte cambiado en cada posición del programa guardado: o se rechaza diciendo por qué, o hace otra
        // cosa dentro de lo que un procedimiento puede hacer. Nunca una excepción del motor, ni un cuelgue.
        var refused = 0;
        for (var at = 0; at < program.Length; at++)
        {
            foreach (var flip in new byte[] { 0x01, 0x80, 0xFF })
            {
                var damaged = (byte[])program.Clone();
                damaged[at] ^= flip;
                await using var transaction = await connection.BeginTransactionAsync(Ct);
                await database.Tree.PutAsync(((TkDbTransaction)transaction).Writer.Inner, CatalogStore.ProcedureKey("archive_orders"), damaged, Ct);
                try
                {
                    await QueryAsync(connection, "CALL archive_orders(10)");
                }
                catch (Exception exception) when (exception is TkSqlException or TkDuplicateKeyException or TkConstraintException)
                {
                    refused++;
                }

                await transaction.RollbackAsync(Ct);
            }
        }

        Assert.InRange(refused, program.Length, program.Length * 3);

        // Lo que no empieza como un programa, o es de otro formato, se rechaza diciéndolo.
        var header = 5 + 4 + "archive_orders".Length;
        foreach (var (at, mention) in new[]
        {
            (0, "does not start as a procedure does"), (4, "this build runs format 1"),
            (header + 2, "does not add up"), (header + 5 + 4 + "p_before".Length, "has unknown type"),
        })
        {
            var damaged = (byte[])program.Clone();
            damaged[at] ^= 0x40;
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            await database.Tree.PutAsync(((TkDbTransaction)transaction).Writer.Inner, CatalogStore.ProcedureKey("archive_orders"), damaged, Ct);
            await RefusedAsync<TkSqlException>(connection, "CALL archive_orders(10)", mention);
            await transaction.RollbackAsync(Ct);
        }

        // El original sigue intacto y corriendo.
        Assert.Equal(["100"], await QueryAsync(connection, "CALL archive_orders(10)"));

        // Cortado en cualquier punto, también se rechaza.
        for (var length = 0; length < program.Length; length += 7)
        {
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            await database.Tree.PutAsync(((TkDbTransaction)transaction).Writer.Inner, CatalogStore.ProcedureKey("archive_orders"), program.AsMemory(0, length), Ct);
            await RefusedAsync<TkSqlException>(connection, "CALL archive_orders(10)", "cannot be run");
            await transaction.RollbackAsync(Ct);
        }

        await AssertNoLeaksAsync(database);
    }

    private static ValueTask<TekiumDb> OpenWithAsync(TestDevice device, int limit = 1_000_000, int depth = 8) =>
        TekiumDb.OpenAsync(
            _ => device,
            new DbOptions
            {
                InMemory = true,
                PageSize = Options.PageSize,
                CacheSizeMb = Options.CacheSizeMb,
                KeyDerivation = Options.KeyDerivation,
                AllowStoredProcedures = true,
                ProcedureInstructionLimit = limit,
                ProcedureCallDepth = depth,
            },
            Ct);

    private static async Task OrdersAsync(TkConnection connection)
    {
        await RunAsync(connection, "CREATE TABLE orders (id BIGINT PRIMARY KEY, total DECIMAL NOT NULL, placed INT NOT NULL)");
        await RunAsync(connection, "CREATE TABLE orders_archive (id BIGINT PRIMARY KEY, total DECIMAL NOT NULL)");
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        for (var i = 0; i < 300; i++)
        {
            await RunAsync(connection, "INSERT INTO orders (id, total, placed) VALUES (@id, @total, @placed)", ("id", (long)i), ("total", i * 1.5m), ("placed", i % 30));
        }

        await transaction.CommitAsync(Ct);
    }
}

public sealed class ProcedureTests_File : ProcedureTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class ProcedureTests_Memory : ProcedureTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
