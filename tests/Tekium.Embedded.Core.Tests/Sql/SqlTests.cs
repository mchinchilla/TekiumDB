// Copyright (c) Tekium. Licensed under the MIT License.

using System.Data.Common;
using System.Globalization;
using Tekium.Embedded.Sql;
using Tekium.Embedded.Tests.Database;
using Tekium.Embedded.Tests.Storage;

namespace Tekium.Embedded.Tests.Sql;

/// <summary>
/// SQL de punta a punta, por el proveedor ADO.NET: lo que devuelve cada consulta se compara con hacer lo
/// mismo sobre objetos en memoria.
/// </summary>
public abstract class SqlTests : DatabaseTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_table_is_created_filled_changed_and_queried_with_sql_alone()
    {
        await using var database = await OpenAsync(NewDevice());
        await using var connection = new TkConnection(database);

        Assert.Equal(-1, await RunAsync(connection, """
            CREATE TABLE products (
                id INT PRIMARY KEY,
                name VARCHAR(80) NOT NULL,
                price DECIMAL(10, 2) NOT NULL,
                stock BIGINT,
                added TIMESTAMP,
                sku TEXT UNIQUE)
            """));
        Assert.Equal(-1, await RunAsync(connection, "CREATE INDEX by_price ON products (price)"));
        Assert.Equal(3, await RunAsync(connection, """
            INSERT INTO products (id, name, price, stock, added, sku) VALUES
                (1, 'Pencil', 0.50, 1200, '2026-03-01T10:00:00Z', 'P-1'),
                (2, 'Notebook', 3.25, 300, NULL, 'N-1'),
                (3, 'It''s a pen', 1.10, NULL, '2026-03-02T08:30:00+02:00', NULL)
            """));
        Assert.Equal(1, await RunAsync(connection, "INSERT INTO products (id, name, price) VALUES (@id, @name, @price)", ("id", 4), ("name", "Eraser"), ("price", 0.75m)));

        var rows = await QueryAsync(connection, "SELECT id, name, price, stock FROM products WHERE price < 2 ORDER BY price DESC");
        Assert.Equal(["3|It's a pen|1.10|", "4|Eraser|0.75|", "1|Pencil|0.50|1200"], rows);
        Assert.Equal(["2026-03-01T10:00:00.0000000+00:00"], await QueryAsync(connection, "SELECT added FROM products WHERE id = 1"));
        // El instante se compara como instante, y el desplazamiento con que se escribió se conserva.
        Assert.Equal(["2026-03-02T08:30:00.0000000+02:00"], await QueryAsync(connection, "SELECT added FROM products WHERE added > '2026-03-02T06:00:00Z' AND added < '2026-03-02T07:00:00Z'"));

        Assert.Equal(2, await RunAsync(connection, "UPDATE products SET price = price * 2, stock = COALESCE(stock, 0) + 10 WHERE price < 1"));
        Assert.Equal(["1|1.00|1210", "4|1.50|10"], await QueryAsync(connection, "SELECT id, price, stock FROM products WHERE id IN (1, 4) ORDER BY id"));
        Assert.Equal(1, await RunAsync(connection, "UPDATE products SET id = 40 WHERE id = 4"));
        Assert.Equal(["40|Eraser"], await QueryAsync(connection, "SELECT id, name FROM products WHERE name = 'Eraser'"));
        Assert.Equal(["Eraser"], await QueryAsync(connection, "SELECT name FROM products WHERE id = 40"));
        Assert.Empty(await QueryAsync(connection, "SELECT name FROM products WHERE id = 4"));
        Assert.Equal(1, await RunAsync(connection, "DELETE FROM products WHERE sku IS NULL AND id < 10"));
        Assert.Equal(["3"], await QueryAsync(connection, "SELECT COUNT(*) FROM products"));
        Assert.Equal(["1.00"], await QueryAsync(connection, "SELECT price FROM products WHERE sku = 'P-1'"));

        // Lo que SQL rechaza, lo rechaza entero: después de cada error la tabla está como antes.
        await RefusedAsync<TkDuplicateKeyException>(connection, "INSERT INTO products (id, name, price) VALUES (1, 'Again', 1)", "already has a row");
        await RefusedAsync<TkConstraintException>(connection, "INSERT INTO products (id, name, price, sku) VALUES (9, 'Other', 1, 'P-1')", "UNIQUE");
        await RefusedAsync<TkSqlException>(connection, "INSERT INTO products (id, price) VALUES (9, 1)", "cannot be NULL");
        await RefusedAsync<TkSqlException>(connection, "INSERT INTO products (id, name, price) VALUES (9, 'x', 'cheap')", "cannot be stored");
        await RefusedAsync<TkSqlException>(connection, "UPDATE products SET nothing = 1", "no column 'nothing'");
        Assert.Equal(["3"], await QueryAsync(connection, "SELECT COUNT(*) FROM products"));

        Assert.Equal(-1, await RunAsync(connection, "DROP INDEX by_price ON products"));
        Assert.Equal(-1, await RunAsync(connection, "DROP TABLE products"));
        await RefusedAsync<TkSqlException>(connection, "SELECT * FROM products", "no table or view 'products'");
        Assert.Equal(["catalog"], (await database.VerifyAsync(Ct)).Trees.Select(static tree => tree.Name));
        await AssertNoLeaksAsync(database);
        Assert.True((await database.VerifyAsync(Ct)).IsHealthy);
    }

    [Fact]
    public async Task Queries_give_what_the_same_question_gives_over_the_rows_in_memory()
    {
        await using var database = await OpenAsync(NewDevice());
        var (items, owners) = await FillAsync(database);
        await using var connection = new TkConnection(database);

        // Condiciones de todas las formas, sobre la tabla que también escribe la API tipada.
        var conditions = new (string Where, Func<Item, bool> Expected)[]
        {
            ("id = 17", i => i.Id == 17),
            ("id >= 100 AND id < 130", i => i.Id is >= 100 and < 130),
            ("id BETWEEN 5 AND 9", i => i.Id is >= 5 and <= 9),
            ("id NOT BETWEEN 5 AND 395", i => i.Id is < 5 or > 395),
            ("owner = 'owner3'", i => i.Owner == "owner3"),
            ("owner = 'owner3' AND qty > 50", i => i.Owner == "owner3" && i.Qty > 50),
            ("owner = 'owner3' OR owner = 'owner4'", i => i.Owner is "owner3" or "owner4"),
            ("owner IN ('owner1', 'owner2') AND NOT active", i => i.Owner is "owner1" or "owner2" && !i.Active),
            ("owner NOT IN ('owner1', 'owner2', 'owner3')", i => i.Owner is not ("owner1" or "owner2" or "owner3")),
            ("owner <> 'owner0' AND qty <= 3", i => i.Owner != "owner0" && i.Qty <= 3),
            ("code IS NULL", i => i.Code is null),
            ("code IS NOT NULL AND price > 90", i => i.Code is not null && i.Price > 90),
            ("code LIKE 'code-1%'", i => i.Code is not null && i.Code.StartsWith("code-1", StringComparison.Ordinal)),
            ("code LIKE '%-7'", i => i.Code is not null && i.Code.EndsWith("-7", StringComparison.Ordinal)),
            ("code NOT LIKE 'code-1_'", i => i.Code is not null && !(i.Code.Length == 7 && i.Code.StartsWith("code-1", StringComparison.Ordinal))),
            ("price * 2 > 150", i => i.Price * 2 > 150),
            ("qty + 1 = 10 OR qty % 25 = 0", i => i.Qty + 1 == 10 || i.Qty % 25 == 0),
            ("price > 10.5 AND price < 11.5", i => i.Price is > 10.5m and < 11.5m),
            ("active AND qty > 90", i => i.Active && i.Qty > 90),
            ("active = FALSE AND qty < 5", i => !i.Active && i.Qty < 5),
            ("at IS NOT NULL AND at < '2026-01-03T00:00:00Z'", i => i.At is not null && i.At < Epoch.AddDays(2)),
            ("NOT (id > 10)", i => i.Id <= 10),
            ("-qty < -95", i => i.Qty > 95),
            ("LENGTH(owner) = 6 AND UPPER(owner) = 'OWNER5'", i => i.Owner == "owner5"),
            ("code = NULL", static _ => false),
            ("NOT (code = 'x')", i => i.Code is not null && i.Code != "x"),
            ("code IS NULL OR qty > 98", i => i.Code is null || i.Qty > 98),
            ("id > 100 AND id < 50", static _ => false),
            ("id > 100 AND id <= 110", i => i.Id is > 100 and <= 110),
            ("id > 1.5 AND id < 4.5", i => i.Id is 2 or 3 or 4),
            ("id = 2.5", static _ => false),
            ("owner = 'owner3' AND qty <= 50", i => i.Owner == "owner3" && i.Qty <= 50),
            ("owner = 'owner3' AND qty > 50 AND qty < 60", i => i.Owner == "owner3" && i.Qty is > 50 and < 60),
            ("owner > 'owner3' AND owner <= 'owner5'", i => i.Owner is "owner4" or "owner5"),
            ("code < 'code-2'", i => i.Code is not null && string.CompareOrdinal(i.Code, "code-2") < 0),
            ("code >= 'code-98'", i => i.Code is not null && string.CompareOrdinal(i.Code, "code-98") >= 0),
            ("owner = 'owner1' OR owner = 'owner2' AND qty > 90", i => i.Owner == "owner1" || (i.Owner == "owner2" && i.Qty > 90)),
            ("NOT (code = 'x' OR qty > 1000)", i => i.Code is not null && i.Code != "x"),
            ("owner NOT IN ('owner1', NULL)", static _ => false),
            ("owner IN ('owner1', NULL)", i => i.Owner == "owner1"),
            ("'2026-01-03T00:00:00Z' > at", i => i.At is not null && i.At < Epoch.AddDays(2)),
        };
        foreach (var (where, expected) in conditions)
        {
            Assert.True(
                items.Where(expected).Select(static i => i.Id.ToString(CultureInfo.InvariantCulture)).SequenceEqual(await QueryAsync(connection, $"SELECT id FROM items WHERE {where} ORDER BY id")),
                $"WHERE {where}");
        }

        // Con parámetros, lo mismo.
        Assert.Equal(
            items.Where(static i => i.Owner == "owner2" && i.Price >= 20m && i.At > Epoch.AddHours(30)).Select(static i => $"{i.Id}"),
            await QueryAsync(connection, "SELECT id FROM items WHERE owner = @owner AND price >= @price AND at > @since ORDER BY id", ("owner", "owner2"), ("price", 20m), ("since", Epoch.AddHours(30))));

        // Orden, límites y distintos.
        Assert.Equal(
            items.OrderByDescending(static i => i.Price).ThenBy(static i => i.Id).Skip(5).Take(7).Select(static i => $"{i.Id}"),
            await QueryAsync(connection, "SELECT id FROM items ORDER BY price DESC, id LIMIT 7 OFFSET 5"));
        Assert.Equal(
            items.Select(static i => i.Owner).Distinct().Order(StringComparer.Ordinal),
            await QueryAsync(connection, "SELECT DISTINCT owner FROM items ORDER BY owner"));
        Assert.Equal(
            items.OrderBy(static i => i.Code is null ? 0 : 1).ThenBy(static i => i.Code, StringComparer.Ordinal).ThenBy(static i => i.Id).Take(4).Select(static i => $"{i.Id}"),
            (await QueryAsync(connection, "SELECT id, code FROM items ORDER BY 2, 1 LIMIT 4")).Select(static row => row.Split('|')[0]));
        Assert.Equal(items.Take(3).Select(static i => $"{i.Id}"), await QueryAsync(connection, "SELECT id FROM items LIMIT 3"));
        Assert.Equal([$"{items.Count}"], await QueryAsync(connection, "SELECT COUNT(*) FROM items LIMIT 5"));

        // Agregados.
        Assert.Equal(
            [$"{items.Count}|{items.Count(static i => i.Code is not null)}|{items.Sum(static i => i.Qty)}|{items.Min(static i => i.Price)}|{items.Max(static i => i.Price)}|{items.Select(static i => i.Owner).Distinct().Count()}"],
            await QueryAsync(connection, "SELECT COUNT(*), COUNT(code), SUM(qty), MIN(price), MAX(price), COUNT(DISTINCT owner) FROM items"));
        Assert.Equal(
            items.GroupBy(static i => i.Owner).Where(static g => g.Count() > 40).OrderBy(static g => g.Key, StringComparer.Ordinal)
                .Select(static g => $"{g.Key}|{g.Count()}|{g.Sum(static i => i.Qty)}|{g.Average(static i => i.Price).ToString(CultureInfo.InvariantCulture)}"),
            (await QueryAsync(connection, "SELECT owner, COUNT(*) AS n, SUM(qty), AVG(price) FROM items GROUP BY owner HAVING COUNT(*) > 40 ORDER BY owner"))
                .Select(static row => row[..row.LastIndexOf('|')] + "|" + decimal.Parse(row[(row.LastIndexOf('|') + 1)..], CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)));
        Assert.Equal(
            items.GroupBy(static i => (i.Owner, i.Active)).OrderByDescending(static g => g.Count()).ThenBy(static g => g.Key.Owner, StringComparer.Ordinal).ThenBy(static g => g.Key.Active)
                .Take(5).Select(static g => $"{g.Key.Owner}|{g.Key.Active}|{g.Count()}"),
            await QueryAsync(connection, "SELECT owner, active, COUNT(*) FROM items GROUP BY owner, active ORDER BY COUNT(*) DESC, owner, active LIMIT 5"));
        Assert.Equal(["0||"], await QueryAsync(connection, "SELECT COUNT(*), SUM(qty), MAX(price) FROM items WHERE id < 0"));
        Assert.Empty(await QueryAsync(connection, "SELECT owner, COUNT(*) FROM items WHERE id < 0 GROUP BY owner"));

        // Joins: por clave, por índice, y por valor cuando no hay por dónde buscar.
        var byOwner = owners.ToDictionary(static o => o.Name);
        Assert.Equal(
            items.Where(i => byOwner[i.Owner].Region == "north" && i.Qty > 80).OrderBy(static i => i.Id).Select(i => $"{i.Id}|{byOwner[i.Owner].Id}"),
            await QueryAsync(connection, "SELECT i.id, o.id FROM items i JOIN owners o ON o.name = i.owner WHERE o.region = 'north' AND i.qty > 80 ORDER BY i.id"));
        Assert.Equal(
            owners.OrderBy(static o => o.Id).Select(o => $"{o.Name}|{items.Count(i => i.Owner == o.Name)}"),
            await QueryAsync(connection, "SELECT o.name, COUNT(*) FROM owners o INNER JOIN items i ON i.owner = o.name GROUP BY o.name, o.id ORDER BY o.id"));
        Assert.Equal(
            items.Where(static i => i.Id < 20 && i.Qty < 8).OrderBy(static i => i.Id).Select(i => $"{i.Id}|{owners.Single(o => o.Id == i.Qty).Name}"),
            await QueryAsync(connection, "SELECT i.id, o.name FROM items i JOIN owners o ON o.id = i.qty WHERE i.id < 20 ORDER BY i.id"));
        Assert.Equal(
            (from a in items where a.Id < 6 from b in items where b.Id < 6 && b.Qty > a.Qty orderby a.Id, b.Id select $"{a.Id}|{b.Id}").ToList(),
            await QueryAsync(connection, "SELECT a.id, b.id FROM items a JOIN items b ON b.qty > a.qty WHERE a.id < 6 AND b.id < 6 ORDER BY a.id, b.id"));
        Assert.Equal(
            items.Where(i => i.Id < 40 && byOwner[i.Owner].Region == "south").OrderBy(static i => i.Id).Select(static i => $"{i.Id}|{i.Owner}"),
            await QueryAsync(connection, "SELECT items.id, owners.name FROM items JOIN owners ON items.owner = owners.name AND owners.region = 'south' WHERE items.id < 40 ORDER BY items.id"));
        Assert.Equal(["1|2|abc|"], await QueryAsync(connection, "SELECT 1, 1 + 1, 'a' || 'bc', NULL"));

        // Una condición del ON que queda sin saberse (el código es NULL) no une la fila.
        Assert.Equal(
            items.Where(static i => i.Id < 60 && i.Code is not null && string.CompareOrdinal(i.Code, "code-3") > 0).OrderBy(static i => i.Id).Select(static i => $"{i.Id}"),
            await QueryAsync(connection, "SELECT i.id FROM items i JOIN owners o ON o.name = i.owner AND i.code > 'code-3' WHERE i.id < 60 ORDER BY i.id"));
    }

    [Fact]
    public async Task Explain_shows_the_path_chosen_with_the_rows_expected_and_the_rows_found()
    {
        await using var database = await OpenAsync(NewDevice());
        var (items, _) = await FillAsync(database);
        await using var connection = new TkConnection(database);

        var lookup = await PlanAsync(connection, "SELECT * FROM items WHERE id = 17");
        Assert.Equal(("  Key lookup in items (Id = 17)", 1L, 1L), lookup[^1]);

        var range = await PlanAsync(connection, "SELECT * FROM items WHERE id >= 100 AND id < 130 AND qty > 50");
        Assert.StartsWith("Filter:", range[0].Plan, StringComparison.Ordinal);
        Assert.Equal(items.Count(static i => i.Id is >= 100 and < 130 && i.Qty > 50), range[0].Actual);
        Assert.Equal(("  Key range of items (Id >= 100 and Id < 130)", 30L), (range[1].Plan, range[1].Actual));
        Assert.InRange(range[1].Estimated!.Value, 10, 90);

        // Entre el índice y recorrer la tabla, gana el que se espera que dé menos filas; y la estimación se parece a lo que salió.
        var index = await PlanAsync(connection, "SELECT * FROM items WHERE owner = 'owner3'");
        var expected = items.Count(static i => i.Owner == "owner3");
        // Dos índices empiezan por Owner; sirve cualquiera, y se elige el que su árbol estima más chico.
        Assert.Matches(@"^  Index Owner(_Qty)? of items \(Owner = 'owner3'\)$", index[^1].Plan);
        Assert.Equal(expected, index[^1].Actual);
        Assert.InRange(index[^1].Estimated!.Value, expected / 3, expected * 3);
        Assert.StartsWith("  Index Owner_Qty of items (Owner = 'owner3', Qty >= 90)", (await PlanAsync(connection, "SELECT * FROM items WHERE owner = 'owner3' AND qty >= 90"))[^1].Plan, StringComparison.Ordinal);
        var coded = items.First(static i => i.Code is not null);
        Assert.Equal(
            ($"  Index Code of items (Code = '{coded.Code}')", 1L, 1L),
            (await PlanAsync(connection, $"SELECT * FROM items WHERE code = '{coded.Code}' AND owner <> 'nobody'"))[^1]);

        // Lo que el camino lee es lo que el rango dice, ni una fila más: los extremos y los NULL.
        Assert.Equal(("  Key range of items (Id > 100 and Id <= 110)", 10L), ((await PlanAsync(connection, "SELECT * FROM items WHERE id > 100 AND id <= 110"))[^1].Plan, (await PlanAsync(connection, "SELECT * FROM items WHERE id > 100 AND id <= 110"))[^1].Actual));
        var below = await PlanAsync(connection, "SELECT * FROM items WHERE code < 'code-2'");
        Assert.Equal(("  Index Code of items (Code < 'code-2')", (long)items.Count(static i => i.Code is not null && string.CompareOrdinal(i.Code, "code-2") < 0)), (below[^1].Plan, below[^1].Actual));
        Assert.Equal("  Scan of items", (await PlanAsync(connection, "SELECT * FROM items WHERE id > 1.5"))[^1].Plan);

        var scan = await PlanAsync(connection, "SELECT * FROM items WHERE price > 50");
        Assert.Equal(("  Scan of items", (long)items.Count), (scan[^1].Plan, scan[^1].Actual));
        Assert.InRange(scan[^1].Estimated!.Value, items.Count / 3, items.Count * 3);

        var whole = await PlanAsync(connection, "SELECT owner, COUNT(*) FROM items i JOIN owners o ON o.name = i.owner WHERE i.id < 50 GROUP BY owner ORDER BY 2 DESC LIMIT 3");
        Assert.Equal(
            ["Limit 3", "  Sort by 2 DESC", "    Group by owner", "      Filter: i.id < 50", "        Join: index name_unique of owners on o.name = i.owner", "          Key range of items (Id < 50)"],
            whole.Select(static line => line.Plan));
        Assert.Equal([3L, 8L, 8L, 50L, 50L, 50L], whole.Select(static line => line.Actual));
    }

    [Fact]
    public async Task A_view_is_a_stored_query_that_can_use_other_views_and_cannot_end_up_using_itself()
    {
        await using var database = await OpenAsync(NewDevice());
        var (items, _) = await FillAsync(database);
        await using var connection = new TkConnection(database);

        await RunAsync(connection, "CREATE VIEW busy AS SELECT id, owner, qty FROM items WHERE qty > 80");
        await RunAsync(connection, "CREATE VIEW busy_by_owner AS SELECT owner, COUNT(*) AS n, MAX(qty) AS most FROM busy GROUP BY owner");
        await RunAsync(connection, "CREATE VIEW top_owners AS SELECT owner, n FROM busy_by_owner WHERE n > 3");

        // Una vista dentro de otra dentro de otra se resuelve, y se puede unir y filtrar como una tabla.
        var busy = items.Where(static i => i.Qty > 80).ToList();
        Assert.Equal(busy.Select(static i => $"{i.Id}"), await QueryAsync(connection, "SELECT id FROM busy ORDER BY id"));
        Assert.Equal(
            busy.GroupBy(static i => i.Owner).Where(static g => g.Count() > 3).OrderBy(static g => g.Key, StringComparer.Ordinal).Select(static g => $"{g.Key}|{g.Count()}"),
            await QueryAsync(connection, "SELECT * FROM top_owners ORDER BY owner"));
        Assert.Equal(
            busy.GroupBy(static i => i.Owner).Where(static g => g.Count() > 3).OrderBy(static g => g.Key, StringComparer.Ordinal).Select(static g => $"{g.Key}|{g.Max(static i => i.Qty)}"),
            await QueryAsync(connection, "SELECT t.owner, b.most FROM top_owners t JOIN busy_by_owner b ON b.owner = t.owner ORDER BY t.owner"));
        Assert.Contains("View busy_by_owner", string.Join('\n', (await PlanAsync(connection, "SELECT * FROM top_owners")).Select(static line => line.Plan)), StringComparison.Ordinal);

        // Un ciclo se rechaza al crearla, y la vista queda como estaba.
        var cycle = await RefusedAsync<TkSqlException>(connection, "CREATE OR REPLACE VIEW busy AS SELECT owner AS id, owner, n AS qty FROM top_owners", "refers to itself");
        Assert.EndsWith("refers to itself: busy -> top_owners -> busy_by_owner -> busy.", cycle, StringComparison.Ordinal);
        await RefusedAsync<TkSqlException>(connection, "CREATE VIEW loop AS SELECT * FROM loop", "refers to itself");
        Assert.Equal(busy.Select(static i => $"{i.Id}"), await QueryAsync(connection, "SELECT id FROM busy ORDER BY id"));

        // Lo que una vista no puede ser.
        await RefusedAsync<TkSqlException>(connection, "CREATE VIEW broken AS SELECT nothing FROM items", "no column 'nothing'");
        await RefusedAsync<TkSqlException>(connection, "CREATE VIEW broken AS SELECT * FROM missing", "no table or view 'missing'");
        await RefusedAsync<TkSqlException>(connection, "CREATE VIEW busy AS SELECT 1 AS one FROM items", "already a view");
        await RefusedAsync<TkSqlException>(connection, "CREATE VIEW items AS SELECT 1 AS one FROM owners", "already a table");
        await RefusedAsync<TkSqlException>(connection, "CREATE TABLE busy (id INT PRIMARY KEY)", "already a view");
        await RefusedAsync<TkSqlException>(connection, "INSERT INTO busy (id) VALUES (1)", "is a view");

        // Reemplazarla cambia lo que ven las que la usan; borrarla deja a las otras sin de dónde leer.
        await RunAsync(connection, "CREATE OR REPLACE VIEW busy AS SELECT id, owner, qty FROM items WHERE qty > 95");
        Assert.Equal([$"{items.Count(static i => i.Qty > 95)}"], await QueryAsync(connection, "SELECT COUNT(*) FROM busy"));
        await RunAsync(connection, "DROP VIEW busy");
        await RefusedAsync<TkSqlException>(connection, "SELECT * FROM top_owners", "no table or view 'busy'");
        await RunAsync(connection, "DROP VIEW IF EXISTS busy");
        await RefusedAsync<TkSqlException>(connection, "DROP VIEW busy", "no view 'busy'");

        // Las herramientas conocen las vistas: la base se verifica y se compacta con ellas.
        Assert.True((await database.VerifyAsync(Ct)).IsHealthy);
        var copy = NewDevice();
        await database.BackupAsync(_ => copy, null, null, Ct);
        await using var restored = await OpenAsync(copy);
        await using var other = new TkConnection(restored);
        await RunAsync(other, "CREATE VIEW busy AS SELECT id, owner, qty FROM items WHERE qty > 95");
        Assert.Equal(
            await QueryAsync(connection, "SELECT owner, COUNT(*) FROM items WHERE qty > 95 GROUP BY owner HAVING COUNT(*) > 3 ORDER BY owner"),
            await QueryAsync(other, "SELECT * FROM top_owners ORDER BY owner"));
    }

    [Fact]
    public async Task What_is_not_valid_sql_or_is_not_supported_says_what_and_where()
    {
        await using var database = await OpenAsync(NewDevice());
        await FillAsync(database);
        await using var connection = new TkConnection(database);

        await RefusedAsync<TkSqlException>(connection, "SELEC id FROM items", "Expected SELECT, INSERT");
        await RefusedAsync<TkSqlException>(connection, "SELECT id FROM items WHERE", "Expected an expression but found the end of the statement, at position 27");
        await RefusedAsync<TkSqlException>(connection, "SELECT id FROM items WHERE id = 'abc", "no closing quote");
        await RefusedAsync<TkSqlException>(connection, "SELECT id FROM items; SELECT 1", "Expected the end of the statement");
        await RefusedAsync<TkSqlException>(connection, "SELECT id FROM items LEFT JOIN owners ON 1 = 1", "outer and cross joins are not supported");
        await RefusedAsync<TkSqlException>(connection, "SELECT id FROM items WHERE id IN (SELECT id FROM owners)", "a subquery is not supported");
        await RefusedAsync<TkSqlException>(connection, "SELECT id FROM (SELECT id FROM items) x", "create a view");
        await RefusedAsync<TkSqlException>(connection, "SELECT id FROM items UNION SELECT id FROM owners", "UNION is not supported");
        await RefusedAsync<TkSqlException>(connection, "SELECT id FROM items i JOIN owners o ON o.name = i.owner", "more than one table");
        await RefusedAsync<TkSqlException>(connection, "SELECT x.id FROM items", "no column 'x.id'");
        await RefusedAsync<TkSqlException>(connection, "SELECT owner, COUNT(*) FROM items", "must be in the GROUP BY");
        await RefusedAsync<TkSqlException>(connection, "SELECT id FROM items WHERE COUNT(*) > 1", "aggregate and cannot be used here");
        await RefusedAsync<TkSqlException>(connection, "SELECT id FROM items WHERE id = @missing", "parameter @missing, which was not given");
        await RefusedAsync<TkSqlException>(connection, "SELECT id / 0 FROM items", "Division by zero");
        await RefusedAsync<TkSqlException>(connection, "SELECT SQRT(id) FROM items", "Unknown function SQRT");
        await RefusedAsync<TkSqlException>(connection, "SELECT id FROM items i JOIN items i ON 1 = 1", "names two tables");
        await RefusedAsync<TkSqlException>(connection, "CREATE TABLE t (a INT, b INT)", "exactly one PRIMARY KEY");
        await RefusedAsync<TkSqlException>(connection, "CREATE TABLE t (a DOUBLE PRIMARY KEY)", "cannot be the primary key");
        await RefusedAsync<TkSqlException>(connection, "CREATE TABLE t (a INT PRIMARY KEY, b WIBBLE)", "Unknown column type 'WIBBLE'");
        await RefusedAsync<TkSqlException>(connection, "CREATE TABLE items (a INT PRIMARY KEY)", "already a table");
        await RefusedAsync<TkSqlException>(connection, "CREATE UNIQUE INDEX dup ON items (owner)", "cannot be unique");
        await RefusedAsync<TkSqlException>(connection, "CREATE INDEX Owner ON items (qty)", "already has an index");

        // Una tabla que sólo su tipo puede mantener se lee con SQL, y no se escribe.
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await database.Table<Ticket>().InsertAsync(writer, new Ticket { Id = 1, Owner = "a", Title = "One" }, Ct);
            await writer.CommitAsync(Ct);
        }

        Assert.Equal(["1|One"], await QueryAsync(connection, "SELECT id, title FROM tickets"));
        await RefusedAsync<TkSqlException>(connection, "UPDATE tickets SET title = 'Two'", "through its row type");
        await RefusedAsync<TkSqlException>(connection, "DROP TABLE tickets", "cannot be changed through SQL");

        // Y los métodos síncronos de ADO.NET no existen: el motor no tiene E/S síncrona.
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        Assert.Contains("ExecuteReaderAsync", Assert.Throws<NotSupportedException>(() => command.ExecuteReader()).Message, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => command.ExecuteNonQuery());
        Assert.Throws<NotSupportedException>(() => command.ExecuteScalar());
        Assert.Throws<NotSupportedException>(() => connection.BeginTransaction());
        Assert.Throws<NotSupportedException>(() => new TkConnection("Data Source=x.tkdb").Open());
    }

    [Fact]
    public async Task Statements_run_in_a_transaction_commit_or_vanish_together_and_alone_commit_by_themselves()
    {
        await using var database = await OpenAsync(NewDevice());
        await using var connection = new TkConnection(database);
        await RunAsync(connection, "CREATE TABLE log (id BIGINT PRIMARY KEY, what TEXT NOT NULL)");

        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await RunAsync(connection, "INSERT INTO log (id, what) VALUES (1, 'one'), (2, 'two')");
            Assert.Equal(["2"], await QueryAsync(connection, "SELECT COUNT(*) FROM log"));

            // Otra conexión a la misma base no lo ve hasta que se confirme.
            await using var other = new TkConnection(database);
            Assert.Equal(["0"], await QueryAsync(other, "SELECT COUNT(*) FROM log"));
            await transaction.RollbackAsync(Ct);
        }

        Assert.Equal(["0"], await QueryAsync(connection, "SELECT COUNT(*) FROM log"));
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await RunAsync(connection, "INSERT INTO log (id, what) VALUES (1, 'one')");

            // La API tipada y SQL, en la misma transacción.
            await database.Table<Counter>().InsertAsync(((TkDbTransaction)transaction).Writer, new Counter { Id = 5, Value = 50 }, Ct);
            await transaction.CommitAsync(Ct);
        }

        Assert.Equal(["1|one"], await QueryAsync(connection, "SELECT id, what FROM log"));
        Assert.Equal(["5|50"], await QueryAsync(connection, "SELECT id, value FROM counter"));

        // Soltada sin confirmar, se deshace; y deja libre al escritor.
        await using (await connection.BeginTransactionAsync(Ct))
        {
            await RunAsync(connection, "DELETE FROM log");
        }

        Assert.Equal(["1"], await QueryAsync(connection, "SELECT COUNT(*) FROM log"));
        Assert.Equal(1, await RunAsync(connection, "DELETE FROM log"));

        // El mismo texto se analiza una vez, por más que se ejecute.
        var parsed = connection.Engine.Parsed;
        for (var i = 0; i < 20; i++)
        {
            await RunAsync(connection, "INSERT INTO log (id, what) VALUES (@id, @what)", ("id", (long)i), ("what", $"entry {i}"));
        }

        Assert.Equal(parsed + 1, connection.Engine.Parsed);
        Assert.Equal(["20"], await QueryAsync(connection, "SELECT COUNT(*) FROM log"));
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task Partial_indexes_and_rows_of_older_schemas_are_handled_as_the_typed_api_handles_them()
    {
        await using var database = await OpenAsync(NewDevice());
        await using var connection = new TkConnection(database);

        // Un índice parcial sobre columnas: SQL lo mantiene al escribir, y no lo usa para leer lo que no tiene.
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await database.Table<Chore>().InsertAsync(writer, new Chore { Id = 0, Due = 5, Title = "first" }, Ct);
            await writer.CommitAsync(Ct);
        }

        for (var i = 1; i <= 60; i++)
        {
            await RunAsync(connection, "INSERT INTO chores (id, due, done, title) VALUES (@id, @due, @done, @title)", ("id", (long)i), ("due", i % 7), ("done", i % 3 == 0), ("title", $"chore {i}"));
        }

        Assert.Equal(10, await RunAsync(connection, "UPDATE chores SET done = TRUE WHERE id > 50"));
        Assert.Equal(5, await RunAsync(connection, "UPDATE chores SET done = FALSE WHERE id <= 15 AND done"));
        Assert.Equal(6, await RunAsync(connection, "DELETE FROM chores WHERE id BETWEEN 20 AND 25"));
        var chores = await ToListAsync(database.Table<Chore>().ScanAsync(Ct));
        Assert.Equal(55, chores.Count);
        Assert.Equal(chores.Count(static c => !c.Done), await CountIndexEntriesAsync(database, "chores", "pending"));
        var pending = Chore.Fields.Done.EqualTo(false).And(Chore.Fields.Due.EqualTo(3));
        Assert.Equal(new TkPlan(TkAccess.IndexScan, "pending"), await database.Table<Chore>().ExplainAsync(pending, Ct));
        Assert.Equal(
            chores.Where(static c => !c.Done && c.Due == 3).Select(static c => c.Id),
            (await ToListAsync(database.Table<Chore>().QueryAsync(pending, Ct))).Select(static c => c.Id).Order());
        Assert.Equal(
            chores.Where(static c => c.Due == 3).Select(static c => $"{c.Id}"),
            await QueryAsync(connection, "SELECT id FROM chores WHERE due = 3 ORDER BY id"));
        await AssertNoLeaksAsync(database);

        // Filas escritas con una versión anterior del tipo: cada valor va a la columna del mismo nombre.
        var old = Guid.CreateVersion7();
        var recent = Guid.CreateVersion7();
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await database.Table<ContactV1>().InsertAsync(writer, new ContactV1 { Id = old, Name = "Ana", Legacy = 7, Phone = "555" }, Ct);
            await writer.CommitAsync(Ct);
        }

        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await database.Table<ContactV2>().InsertAsync(writer, new ContactV2 { Id = recent, FullName = "Eva", Score = 3, Phone = "777" }, Ct);
            await writer.CommitAsync(Ct);
        }

        Assert.Equal(["Ana|555|0||", "Eva|777|3||"], await QueryAsync(connection, "SELECT name, phone, score, notes, since FROM contacts ORDER BY phone"));
        var typed = await ToListAsync(database.Table<ContactV2>().ScanAsync(Ct));
        Assert.Equal(["Ana|555|0||", "Eva|777|3||"], typed.OrderBy(static c => c.Phone, StringComparer.Ordinal).Select(static c => $"{c.FullName}|{c.Phone}|{c.Score}|{c.Notes}|{c.Since}"));
    }

    [Fact]
    public async Task A_statement_that_fails_inside_a_transaction_leaves_nothing_of_itself_behind()
    {
        await using var database = await OpenAsync(NewDevice());
        var (items, _) = await FillAsync(database);
        await using var connection = new TkConnection(database);
        await RunAsync(connection, "CREATE VIEW cheap AS SELECT id FROM items WHERE price < 10");

        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            // Un índice único que no puede serlo, una vista que no se puede resolver, y una vista reemplazada por otra inválida.
            await RefusedAsync<TkSqlException>(connection, "CREATE UNIQUE INDEX dup ON items (owner)", "cannot be unique");
            await RefusedAsync<TkSqlException>(connection, "CREATE VIEW broken AS SELECT nothing FROM items", "no column 'nothing'");
            await RefusedAsync<TkSqlException>(connection, "CREATE OR REPLACE VIEW cheap AS SELECT id FROM missing", "no table or view 'missing'");

            // La transacción sigue, y nada de eso quedó en ella.
            await RefusedAsync<TkSqlException>(connection, "SELECT * FROM broken", "no table or view 'broken'");
            Assert.Equal([$"{items.Count(static i => i.Price < 10)}"], await QueryAsync(connection, "SELECT COUNT(*) FROM cheap"));
            Assert.Equal(-1, await RunAsync(connection, "CREATE INDEX dup ON items (qty)"));
            await transaction.CommitAsync(Ct);
        }

        Assert.Equal([$"{items.Count(static i => i.Price < 10)}"], await QueryAsync(connection, "SELECT COUNT(*) FROM cheap"));
        Assert.Equal(items.Count, await CountIndexEntriesAsync(database, "items", "dup"));
        await AssertNoLeaksAsync(database);
        Assert.True((await database.VerifyAsync(Ct)).IsHealthy);
    }

    internal static async Task<(List<Item> Items, List<(int Id, string Name, string Region)> Owners)> FillAsync(TekiumDb database)
    {
        var random = new Random(17);
        var items = new List<Item>();
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            for (var i = 0; i < 400; i++)
            {
                var item = new Item
                {
                    Id = i,
                    Owner = $"owner{random.Next(8)}",
                    Code = random.Next(5) == 0 ? null : $"code-{i}",
                    Qty = random.Next(100),
                    Price = random.Next(10_000) / 100m,
                    At = random.Next(4) == 0 ? null : Epoch.AddHours(random.Next(200)),
                    Active = random.Next(2) == 0,
                };
                items.Add(item);
                await database.Table<Item>().InsertAsync(writer, item, Ct);
            }

            await writer.CommitAsync(Ct);
        }

        // Una tabla hecha con SQL, para unir con la que escribe la API tipada.
        await using var connection = new TkConnection(database);
        await RunAsync(connection, "CREATE TABLE owners (id INT PRIMARY KEY, name TEXT NOT NULL UNIQUE, region TEXT NOT NULL)");
        var owners = Enumerable.Range(0, 8).Select(static i => (Id: i, Name: $"owner{i}", Region: i % 2 == 0 ? "north" : "south")).ToList();
        foreach (var (id, name, region) in owners)
        {
            await RunAsync(connection, "INSERT INTO owners (id, name, region) VALUES (@id, @name, @region)", ("id", id), ("name", name), ("region", region));
        }

        return (items, owners);
    }

    internal static async Task<int> RunAsync(DbConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, sql, parameters);
        return await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Cada fila como texto, con sus valores separados por barras; un NULL es vacío.</summary>
    internal static async Task<List<string>> QueryAsync(DbConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? string.Empty : reader.GetValue(i) switch
            {
                DateTimeOffset moment => moment.ToString("O", CultureInfo.InvariantCulture),
                byte[] bytes => Convert.ToHexString(bytes),
                IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
                var value => value.ToString(),
            })));
        }

        return rows;
    }

    private static async Task<List<(string Plan, long? Estimated, long Actual)>> PlanAsync(DbConnection connection, string sql)
    {
        await using var command = Command(connection, "EXPLAIN " + sql, []);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.Equal(["plan", "estimated_rows", "actual_rows"], Enumerable.Range(0, 3).Select(reader.GetName));
        var lines = new List<(string, long?, long)>();
        while (await reader.ReadAsync(Ct))
        {
            lines.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetInt64(2)));
        }

        return lines;
    }

    internal static async Task<string> RefusedAsync<TException>(DbConnection connection, string sql, string mention)
        where TException : Exception
    {
        var failure = await Assert.ThrowsAsync<TException>(async () => await RunAsync(connection, sql));
        Assert.Contains(mention, failure.Message, StringComparison.Ordinal);
        return failure.Message;
    }

    private static DbCommand Command(DbConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return command;
    }
}

public sealed class SqlTests_File : SqlTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class SqlTests_Memory : SqlTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
