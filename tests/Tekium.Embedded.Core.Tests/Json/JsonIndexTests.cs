// Copyright (c) Tekium. Licensed under the MIT License.

using System.Globalization;
using Tekium.Embedded.Catalog;
using Tekium.Embedded.Sql;
using Tekium.Embedded.Sql.Storage;
using Tekium.Embedded.Tests.Database;
using Tekium.Embedded.Tests.Storage;
using static Tekium.Embedded.Tests.Sql.SqlTests;

namespace Tekium.Embedded.Tests.Json;

/// <summary>
/// Índices sobre una ruta de un documento: el mismo índice en el archivo lo declare un tipo de fila o SQL,
/// lo mantienen igual los dos lados, y una consulta por esa ruta lo usa y da lo mismo que sin él.
/// </summary>
public abstract class JsonIndexTests : DatabaseTests
{
    [Fact]
    public async Task An_index_on_a_path_is_the_same_from_a_row_type_and_from_sql_and_each_side_keeps_it()
    {
        await using var typed = await OpenAsync(NewDevice());
        await using (var writer = await typed.BeginWriteAsync(Ct))
        {
            await typed.Table<Good>().InsertAsync(writer, new Good { Id = 1, Doc = TkJson.Parse("""{"sku":"a-1","price":10}"""), Name = "one" }, Ct);
            await writer.CommitAsync(Ct);
        }

        await using var made = await OpenAsync(NewDevice());
        await using (var sql = new TkConnection(made))
        {
            await RunAsync(sql, "CREATE TABLE goods (Id INT PRIMARY KEY, Doc JSONB NOT NULL, Name TEXT NOT NULL)");
            await RunAsync(sql, "CREATE UNIQUE INDEX by_sku ON goods (JSON_VALUE(Doc, '$.\"sku\"'))");
            await RunAsync(sql, "CREATE INDEX by_price ON goods (JSON_VALUE(Doc, '$.price') AS DOUBLE)");
            await RunAsync(sql, """INSERT INTO goods (Id, Doc, Name) VALUES (1, '{"sku":"a-1","price":10}', 'one')""");
        }

        Assert.Equal(await DefinitionsAsync(typed), await DefinitionsAsync(made));

        foreach (var database in new[] { typed, made })
        {
            await using var connection = new TkConnection(database);

            // SQL escribe y mantiene el índice; la API tipada también, y cada uno respeta la unicidad.
            await RunAsync(connection, """INSERT INTO goods (Id, Doc, Name) VALUES (2, '{"sku":"b-2","price":2.5}', 'two'), (3, '{"price":"n/a"}', 'three')""");
            Assert.Equal("by_sku", (await Assert.ThrowsAsync<TkConstraintException>(async () => await RunAsync(connection, """INSERT INTO goods (Id, Doc, Name) VALUES (4, '{"sku":"a-1"}', 'dup')"""))).Constraint);
            await using (var writer = await database.BeginWriteAsync(Ct))
            {
                await database.Table<Good>().InsertAsync(writer, new Good { Id = 5, Doc = TkJson.Parse("""{"sku":"c-3","price":99}"""), Name = "five" }, Ct);
                await database.Table<Good>().InsertAsync(writer, new Good { Id = 6, Doc = TkJson.Parse("""{"sku":{"nested":true}}"""), Name = "six" }, Ct);
                await writer.CommitAsync(Ct);
            }

            await using (var writer = await database.BeginWriteAsync(Ct))
            {
                var duplicate = await Assert.ThrowsAsync<TkConstraintException>(async () =>
                    await database.Table<Good>().InsertAsync(writer, new Good { Id = 7, Doc = TkJson.Parse("""{"sku":"b-2"}"""), Name = "dup" }, Ct));
                Assert.Equal("by_sku", duplicate.Constraint);
            }

            Assert.Equal(1, await RunAsync(connection, """UPDATE goods SET Doc = '{"sku":"b-2b","price":3}' WHERE Id = 2"""));

            // Las consultas por la ruta usan el índice, y dan lo que dan sin él.
            Assert.Equal(["2"], await QueryAsync(connection, "SELECT Id FROM goods WHERE JSON_VALUE(Doc, '$.sku') = 'b-2b'"));
            Assert.Equal(["2"], await QueryAsync(connection, "SELECT Id FROM goods WHERE Doc ->> 'sku' = 'b-2b'"));
            Assert.Equal(["5"], await QueryAsync(connection, "SELECT Id FROM goods WHERE JSON_EXTRACT(Doc, '$.price') > 10 ORDER BY Id"));
            Assert.Equal(["1", "2"], await QueryAsync(connection, "SELECT Id FROM goods WHERE Doc ->> '$.price' BETWEEN 3 AND 10 ORDER BY Id"));
            Assert.Contains(await QueryAsync(connection, "EXPLAIN SELECT Id FROM goods WHERE Doc ->> 'sku' = 'b-2b'"), static line => line.Contains("Index by_sku of goods", StringComparison.Ordinal));
            Assert.Contains(await QueryAsync(connection, "EXPLAIN SELECT Id FROM goods WHERE JSON_VALUE(Doc, '$.price') >= 3"), static line => line.Contains("Index by_price of goods", StringComparison.Ordinal));
            Assert.Equal(["6"], await QueryAsync(connection, """SELECT Id FROM goods WHERE Doc ->> 'sku' = '{"nested":true}'"""));
            Assert.Empty(await QueryAsync(connection, """SELECT Id FROM goods WHERE JSON_VALUE(Doc, '$.sku') = '{"nested":true}'"""));

            // Cada fila tiene su entrada, como en una columna que admite NULL: la que no tiene la ruta, o la tiene de otra clase, la tiene con NULL.
            Assert.Equal(5, await EntriesAsync(database, "by_sku"));
            Assert.Equal(5, await EntriesAsync(database, "by_price"));
            Assert.Equal(["6"], await QueryAsync(connection, "SELECT Id FROM goods WHERE JSON_VALUE(Doc, '$.price') IS NULL ORDER BY Id"));
            Assert.True((await database.VerifyAsync(Ct)).IsHealthy);
            await AssertNoLeaksAsync(database);
        }

        static async Task<string> DefinitionsAsync(TekiumDb database)
        {
            await using var reader = database.BeginRead();
            var goods = (await SqlTable.OpenAsync(reader, "goods", Ct))!;
            Assert.Null(goods.ReadOnlyReason);
            return string.Join(',', goods.Indexes.Select(static index => $"{index.Name}={Convert.ToHexString(index.Definition)}"));
        }
    }

    [Fact]
    public async Task A_query_by_a_path_through_its_index_gives_what_a_model_of_the_documents_gives()
    {
        await using var database = await OpenAsync(NewDevice());
        await using var connection = new TkConnection(database);
        await RunAsync(connection, "CREATE TABLE items (id INT PRIMARY KEY, doc JSON NOT NULL)");
        await RunAsync(connection, "CREATE INDEX by_kind ON items (JSON_VALUE(doc, '$.kind'))");
        await RunAsync(connection, "CREATE INDEX by_weight ON items (doc ->> '$.w' AS DOUBLE)");
        var random = new Random(41);
        var model = new Dictionary<int, (string? Kind, double? Weight, bool KindIsText)>();
        for (var step = 0; step < 700; step++)
        {
            var id = random.Next(1, 120);
            var kindValue = random.Next(6) switch
            {
                0 => null,
                1 => "null",
                2 => "[1,2]",
                _ => $"\"k{random.Next(5)}\"",
            };
            var weightValue = random.Next(6) switch
            {
                0 => null,
                1 => "null",
                2 => "{\"x\":1}",
                3 => "12345678901234567891",
                _ => (random.Next(-500, 500) / 4.0).ToString(CultureInfo.InvariantCulture),
            };
            var members = new List<string>();
            if (kindValue is not null)
            {
                members.Add($"\"kind\":{kindValue}");
            }

            if (weightValue is not null)
            {
                members.Add($"\"w\":{weightValue}");
            }

            var document = "{" + string.Join(",", members) + "}";
            if (random.Next(5) == 0 && model.ContainsKey(id))
            {
                await RunAsync(connection, "DELETE FROM items WHERE id = @id", ("id", id));
                model.Remove(id);
                continue;
            }

            await RunAsync(connection, model.ContainsKey(id) ? "UPDATE items SET doc = @doc WHERE id = @id" : "INSERT INTO items (id, doc) VALUES (@id, @doc)", ("id", id), ("doc", document));
            var kind = kindValue is ['"', ..] ? kindValue[1..^1] : kindValue == "[1,2]" ? "[1,2]" : null;
            double? weight = weightValue is null or "null" or "{\"x\":1}" ? null : double.Parse(weightValue, CultureInfo.InvariantCulture);
            model[id] = (kind, weight, kindValue is ['"', ..]);
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var probe = 0; probe < 60; probe++)
        {
            var kind = $"k{random.Next(6)}";
            var low = random.Next(-130, 130);
            var high = low + random.Next(0, 80);
            await SameAsync("SELECT id FROM items WHERE doc ->> 'kind' = @v ORDER BY id", model.Where(entry => entry.Value.Kind == kind).Select(static entry => entry.Key), ("v", kind));
            await SameAsync("SELECT id FROM items WHERE JSON_VALUE(doc, '$.kind') = @v ORDER BY id", model.Where(entry => entry.Value.KindIsText && entry.Value.Kind == kind).Select(static entry => entry.Key), ("v", kind));
            await SameAsync("SELECT id FROM items WHERE doc ->> 'w' > @v ORDER BY id", model.Where(entry => entry.Value.Weight > low).Select(static entry => entry.Key), ("v", low));
            await SameAsync("SELECT id FROM items WHERE JSON_EXTRACT(doc, '$.w') BETWEEN @a AND @b ORDER BY id", model.Where(entry => entry.Value.Weight >= low && entry.Value.Weight <= high).Select(static entry => entry.Key), ("a", low), ("b", high));
            await SameAsync("SELECT id FROM items WHERE doc ->> 'w' <= @v ORDER BY id", model.Where(entry => entry.Value.Weight <= low).Select(static entry => entry.Key), ("v", low));
        }

        // Un número que un DOUBLE no guarda exacto: el índice da de más y la condición entera descarta.
        await SameAsync("SELECT id FROM items WHERE doc ->> 'w' = 12345678901234567891 ORDER BY id", model.Where(static entry => entry.Value.Weight == 12345678901234567891d).Select(static entry => entry.Key));

        // Las dos rutas se buscaron por su índice al menos una vez.
        Assert.Contains(used, static plan => plan.Contains("by_kind", StringComparison.Ordinal));
        Assert.Contains(used, static plan => plan.Contains("by_weight", StringComparison.Ordinal));
        Assert.True((await database.VerifyAsync(Ct)).IsHealthy);
        await AssertNoLeaksAsync(database);

        async Task SameAsync(string sql, IEnumerable<int> expected, params (string Name, object? Value)[] parameters)
        {
            Assert.Equal(expected.Order().Select(static id => id.ToString(CultureInfo.InvariantCulture)), await QueryAsync(connection, sql, parameters));
            used.Add(string.Join(" / ", await QueryAsync(connection, "EXPLAIN " + sql, parameters)));
        }
    }

    [Fact]
    public async Task The_planner_gives_through_a_path_index_what_the_same_table_without_it_gives()
    {
        // Las mismas filas en dos tablas, una con los índices por ruta y otra sin ellos: cada consulta da lo mismo en las dos.
        await using var database = await OpenAsync(NewDevice());
        await using var connection = new TkConnection(database);
        foreach (var table in new[] { "indexed", "plain" })
        {
            await RunAsync(connection, $"CREATE TABLE {table} (id INT PRIMARY KEY, doc JSONB NOT NULL)");
            await RunAsync(connection, $$"""
                INSERT INTO {{table}} (id, doc) VALUES
                (1, '{"w":1.00000000000000000001,"code":5}'),
                (2, '{"w":1,"code":7}'),
                (3, '{"w":0.99999999999999999999,"code":5}'),
                (4, '{"w":2,"code":5.0}'),
                (5, '{"w":-3}')
                """);
        }

        await RunAsync(connection, "CREATE INDEX by_w ON indexed (JSON_VALUE(doc, '$.w') AS DOUBLE)");
        await RunAsync(connection, "CREATE INDEX by_code ON indexed (JSON_VALUE(doc, '$.code'))");
        foreach (var where in new[]
        {
            // Un DOUBLE redondea: 1.00000000000000000001 y 0.99999999999999999999 son 1 en el índice, y el WHERE decide.
            "JSON_VALUE(doc, '$.w') > 1",
            "JSON_VALUE(doc, '$.w') < 1",
            "JSON_VALUE(doc, '$.w') >= 1",
            "JSON_VALUE(doc, '$.w') <= 1",

            // La ruta del otro lado de la comparación.
            "1 < JSON_VALUE(doc, '$.w')",
            "2 >= JSON_VALUE(doc, '$.w')",
            "0 > JSON_VALUE(doc, '$.w')",

            // Una constante numérica contra un índice de texto, que no guarda números: no se busca "5" en él como si fuera texto.
            "JSON_VALUE(doc, '$.code') = 5",
            "5 = JSON_VALUE(doc, '$.code')",
        })
        {
            List<string> plainRows, indexedRows;
            try
            {
                plainRows = [.. await QueryAsync(connection, $"SELECT id FROM plain WHERE {where} ORDER BY id")];
            }
            catch (Exception failed)
            {
                throw new InvalidOperationException($"plain: {where}", failed);
            }

            try
            {
                indexedRows = [.. await QueryAsync(connection, $"SELECT id FROM indexed WHERE {where} ORDER BY id")];
            }
            catch (Exception failed)
            {
                throw new InvalidOperationException($"indexed: {where}", failed);
            }

            Assert.Equal(plainRows, indexedRows);
        }

        Assert.Contains(await QueryAsync(connection, "EXPLAIN SELECT id FROM indexed WHERE 1 < JSON_VALUE(doc, '$.w')"), static line => line.Contains("by_w", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_document_goes_into_a_column_in_its_form_and_two_that_say_the_same_are_equal()
    {
        await using var database = await OpenAsync(NewDevice());
        await using var connection = new TkConnection(database);
        await RunAsync(connection, "CREATE TABLE docs (id INT PRIMARY KEY, kept JSON NOT NULL, normal JSONB NOT NULL)");
        var text = TkJson.Parse("""{ "b": 1, "a": 1.50 }""");
        await RunAsync(connection, "INSERT INTO docs (id, kept, normal) VALUES (1, @kept, @normal)", ("kept", text), ("normal", text));
        Assert.Equal(["""{ "b": 1, "a": 1.50 }|{"a":1.5,"b":1}"""], await QueryAsync(connection, "SELECT kept, normal FROM docs"));

        // Iguales por lo que dicen, no por cómo están escritos.
        Assert.Equal(["1"], await QueryAsync(connection, """SELECT id FROM docs WHERE kept = JSON('{"a":1.5,"b":1}')"""));
        Assert.Equal(["1"], await QueryAsync(connection, "SELECT id FROM docs WHERE kept = normal"));
        Assert.Empty(await QueryAsync(connection, """SELECT id FROM docs WHERE kept = JSON('{"a":1.5,"b":2}')"""));

        // Lo que devuelve RETURNING es lo que la columna guarda.
        Assert.Equal(["""{ "b": 1, "a": 1.50 }|{"a":1.5,"b":1}"""], await QueryAsync(connection, "INSERT INTO docs (id, kept, normal) VALUES (2, @kept, @normal) RETURNING kept, normal", ("kept", text), ("normal", text)));
    }

    [Fact]
    public void A_stored_path_that_is_not_canonical_is_not_used()
    {
        TkColumn[] columns = [new("id", TkColumnType.I64, false), new("doc", TkColumnType.Json, false)];
        var good = StoredIndex.Create("by_a", columns, [1], unique: false, [(TkJsonPath.Parse("$.a"), TkColumnType.Text)]);
        Assert.Null(good.Unmaintainable);
        Assert.Equal("$.a", good.Paths[0]?.Text);

        // La misma definición con la ruta escrita "$.\"a\"": dice lo mismo, pero no es como se guarda, así que no se usa.
        var definition = good.Definition;
        var at = definition.AsSpan().IndexOf("$.a"u8);
        Assert.Equal(3, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(definition.AsSpan(at - 4)));
        var quoted = "$.\"a\""u8.ToArray();
        byte[] changed = [.. definition[..(at - 4)], .. BitConverter.GetBytes(quoted.Length), .. quoted, .. definition[(at + 3)..]];
        var decoded = StoredIndex.Decode("by_a", changed, columns);
        Assert.NotNull(decoded.Unmaintainable);
    }

    [Fact]
    public async Task Changing_the_column_of_a_path_index_keeps_the_index_or_says_why_not()
    {
        await using var database = await OpenAsync(NewDevice());
        await using var connection = new TkConnection(database);
        await RunAsync(connection, "CREATE TABLE notes (id INT PRIMARY KEY, body JSON NOT NULL, other INT)");
        await RunAsync(connection, "CREATE INDEX by_tag ON notes (JSON_VALUE(body, '$.tag'))");
        await RunAsync(connection, """INSERT INTO notes (id, body) VALUES (1, '{"tag":"x"}'), (2, '{"tag":"y"}')""");

        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE notes DROP COLUMN body", "index 'by_tag'");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE notes ALTER COLUMN body TEXT NOT NULL", "cannot stop being a JSON column: index 'by_tag'");
        await RunAsync(connection, "ALTER TABLE notes RENAME COLUMN body TO doc");
        await RunAsync(connection, "ALTER TABLE notes ALTER COLUMN doc JSONB");
        Assert.Equal(["2"], await QueryAsync(connection, "SELECT id FROM notes WHERE doc ->> 'tag' = 'y'"));
        Assert.Contains(await QueryAsync(connection, "EXPLAIN SELECT id FROM notes WHERE doc ->> 'tag' = 'y'"), static line => line.Contains("Index by_tag", StringComparison.Ordinal));
        Assert.Equal(1, await RunAsync(connection, """INSERT INTO notes (id, doc) VALUES (3, '{"tag":"y"}')"""));
        Assert.Equal(["2", "3"], await QueryAsync(connection, "SELECT id FROM notes WHERE doc ->> 'tag' = 'y' ORDER BY id"));

        // Lo que no se puede indexar dice por qué.
        await RefusedAsync<TkSqlException>(connection, "CREATE INDEX bad ON notes (JSON_VALUE(other, '$.a'))", "is not a JSON column");
        await RefusedAsync<TkSqlException>(connection, "CREATE INDEX bad ON notes (JSON_VALUE(doc, '$.a') AS INT)", "TEXT or DOUBLE");
        await RefusedAsync<TkSqlException>(connection, "CREATE INDEX bad ON notes (JSON_VALUE(doc, 'a'))", "must start with $");
        await RefusedAsync<TkSqlException>(connection, "CREATE INDEX bad ON notes (UPPER(doc))", "a column or JSON_VALUE");

        // Un índice creado con SQL entre dos escrituras tipadas de la misma transacción lo mantiene la segunda.
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await database.Table<Good>().InsertAsync(writer, new Good { Id = 1, Doc = TkJson.Parse("""{"sku":"s1","price":1}"""), Name = "a" }, Ct);
            await writer.CommitAsync(Ct);
        }

        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await database.Table<Good>().InsertAsync(writer, new Good { Id = 2, Doc = TkJson.Parse("""{"sku":"s2","price":2}"""), Name = "b" }, Ct);
            await new TkContext(database, writer).ExecuteAsync("CREATE INDEX by_name ON goods (Name)", cancellationToken: Ct);
            await database.Table<Good>().InsertAsync(writer, new Good { Id = 3, Doc = TkJson.Parse("""{"sku":"s3","price":3}"""), Name = "c" }, Ct);
            await writer.CommitAsync(Ct);
        }

        Assert.True((await database.VerifyAsync(Ct)).IsHealthy);
    }

    private static async Task<long> EntriesAsync(TekiumDb database, string index)
    {
        await using var reader = database.BeginRead();
        var goods = (await SqlTable.OpenAsync(reader, "goods", Ct))!;
        var found = goods.Indexes.Single(candidate => candidate.Name == index);
        var count = 0L;
        await foreach (var entry in Tekium.Embedded.Indexing.BTree.RangeAsync(reader.Source, found.CurrentRoot, default, default, Ct))
        {
            _ = entry;
            count++;
        }

        return count;
    }
}

public sealed class JsonIndexTests_File : JsonIndexTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class JsonIndexTests_Memory : JsonIndexTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
