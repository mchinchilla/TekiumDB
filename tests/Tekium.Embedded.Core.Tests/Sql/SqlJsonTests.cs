// Copyright (c) Tekium. Licensed under the MIT License.

using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Tekium.Embedded.Sql;
using Tekium.Embedded.Tests.Database;
using Tekium.Embedded.Tests.Storage;
using static Tekium.Embedded.Tests.Sql.SqlTests;

namespace Tekium.Embedded.Tests.Sql;

/// <summary>
/// Columnas json y jsonb desde SQL: qué guarda cada una, y que leer por una ruta dé lo mismo que
/// <c>json_extract</c>, <c>-&gt;</c> y <c>-&gt;&gt;</c> de SQLite sobre los mismos documentos.
/// </summary>
public abstract class SqlJsonTests : DatabaseTests
{
    private static readonly string[] Documents =
    [
        """{"a":1}""", """{"a":null}""", """{"a":true}""", """{"a":false}""", """{"a":"x"}""", """{"a":1.50}""", """{"a":-0.25e2}""",
        """{"a":12345678901234567890}""", """{"a":[1,2,{"b":3}]}""", """{"a":{"b":"c","d":[true]}}""", """{}""", """[]""",
        """{"a b":5,"c.d":6}""", """[10,20,30]""", """ { "a" : [ 1 , { "b" : "y" } ] , "z" : 0 } """, """{"a":"é\n\"q\""}""", "7", "\"top\"", "null",
        """{"z":1,"a":{"y":2,"b":3}}""",
    ];

    private static readonly string[] Paths = ["$", "$.a", "$.a[1]", "$.a[2].b", "$.a.b", "$.a.d[0]", "$.missing", "$[1]", "$[#-1]", "$.\"a b\"", "$.\"c.d\"", "$.z", "$.a[#-1].b"];

    [Fact]
    public async Task A_json_column_keeps_its_text_and_a_jsonb_column_keeps_one_form_of_what_it_says()
    {
        await using var database = await OpenAsync(NewDevice());
        await using var connection = new TkConnection(database);
        await RunAsync(connection, "CREATE TABLE docs (id INT PRIMARY KEY, j JSON, b JSONB NOT NULL)");
        await RunAsync(connection, """INSERT INTO docs (id, j, b) VALUES (1, ' { "b" : 1, "a" : [ 1.50 ], "b": 2 } ', ' { "b" : 1, "a" : [ 1.50 ], "b": 2 } ')""");
        await RunAsync(connection, "INSERT INTO docs (id, j, b) VALUES (2, 'null', 'null'), (3, NULL, '{}')");
        await RunAsync(connection, "INSERT INTO docs (id, j, b) VALUES (@id, @j, @b)", ("id", 4), ("j", TkJson.Parse("[true]")), ("b", TkJson.Parse("""{"k":"v"}""").ToBinary()));

        Assert.Equal(
            [""" { "b" : 1, "a" : [ 1.50 ], "b": 2 } |{"a":[1.5],"b":2}""", "null|null", """|{}""", """[true]|{"k":"v"}"""],
            await QueryAsync(connection, "SELECT j, b FROM docs ORDER BY id"));

        // El documento null no es una columna sin valor.
        Assert.Equal(["3"], await QueryAsync(connection, "SELECT id FROM docs WHERE j IS NULL"));
        Assert.Equal(["2"], await QueryAsync(connection, "SELECT id FROM docs WHERE j = 'null'"));

        // Iguales los que dicen lo mismo, en cualquier forma.
        Assert.Equal(["1"], await QueryAsync(connection, """SELECT id FROM docs WHERE b = '{"b":2,"a":[1.5]}'"""));
        Assert.Equal(["1"], await QueryAsync(connection, """SELECT id FROM docs WHERE j = '{"a":[1.5],"b":2}'"""));
        Assert.Equal(["1|True"], await QueryAsync(connection, "SELECT id, j = b FROM docs WHERE id = 1"));

        // Lo que no es JSON no entra, y dice qué columna.
        var refused = await RefusedAsync<TkSqlException>(connection, "INSERT INTO docs (id, b) VALUES (9, '{a:1}')", "column 'b'");
        Assert.Contains("not valid JSON", refused, StringComparison.Ordinal);
        await RefusedAsync<TkSqlException>(connection, "INSERT INTO docs (id, b) VALUES (9, 12)", "column 'b'");

        // Un documento no es una clave ni se indexa entero: se indexa una ruta.
        await RefusedAsync<TkSqlException>(connection, "CREATE TABLE bad (doc JSONB PRIMARY KEY)", "cannot be the primary key");
        await RefusedAsync<TkSqlException>(connection, "CREATE INDEX whole ON docs (b)", "a JSON document has no order of its own");
        await RefusedAsync<TkSqlException>(connection, "CREATE TABLE bad (id INT PRIMARY KEY, doc JSON UNIQUE)", "a JSON document has no order of its own");

        // Se ordenan y agrupan por su texto canónico: un orden fijo, no uno con sentido.
        Assert.Equal(["4", "2", "1"], await QueryAsync(connection, "SELECT id FROM docs WHERE j IS NOT NULL ORDER BY j"));

        // CAST, JSON y JSONB convierten texto en documento; ALTER COLUMN cambia la forma si el documento no cambia.
        Assert.Equal(["""{"a":1,"b":2}|{"b":2,"a":1}"""], await QueryAsync(connection, """SELECT CAST('{"b":2,"a":1}' AS JSONB), JSON('{"b":2,"a":1}')"""));
        Assert.Equal(["""{"b":2}"""], await QueryAsync(connection, """SELECT JSONB(' {"b":2} ')"""));
        await RefusedAsync<TkSqlException>(connection, "SELECT JSON('{nope}')", "not valid JSON");
        await RunAsync(connection, "ALTER TABLE docs ALTER COLUMN j JSONB");
        Assert.Equal(["""{"a":[1.5],"b":2}""", "null", "[true]"], await QueryAsync(connection, "SELECT j FROM docs WHERE j IS NOT NULL ORDER BY id"));
        await RunAsync(connection, "ALTER TABLE docs ALTER COLUMN b TEXT NOT NULL");
        Assert.Equal(["""{"a":[1.5],"b":2}"""], await QueryAsync(connection, "SELECT b FROM docs WHERE id = 1"));
        await RunAsync(connection, "UPDATE docs SET b = 'not json' WHERE id = 3");
        await RefusedAsync<TkSqlException>(connection, "ALTER TABLE docs ALTER COLUMN b JSON NOT NULL", "the row with key 3");
        Assert.True((await database.VerifyAsync(Ct)).IsHealthy);
    }

    [Fact]
    public async Task Reading_by_a_path_gives_what_sqlite_gives_on_the_same_documents()
    {
        await using var database = await OpenAsync(NewDevice());
        await using var connection = new TkConnection(database);
        await using var sqlite = new SqliteConnection("Data Source=:memory:");
        await sqlite.OpenAsync(Ct);
        await RunAsync(connection, "CREATE TABLE docs (id INT PRIMARY KEY, j JSON NOT NULL, b JSONB NOT NULL)");
        await RunAsync(sqlite, "CREATE TABLE docs (id INT PRIMARY KEY, j TEXT NOT NULL)");
        for (var i = 0; i < Documents.Length; i++)
        {
            await RunAsync(connection, "INSERT INTO docs (id, j, b) VALUES (@id, @j, @j)", ("id", i), ("j", Documents[i]));
            await RunAsync(sqlite, "INSERT INTO docs (id, j) VALUES (@id, @j)", ("id", i), ("j", Documents[i]));
        }

        var compared = 0;
        foreach (var path in Paths)
        {
            var expected = await RowsAsync(sqlite, "SELECT json_extract(j, @p), j ->> @p, j -> @p FROM docs ORDER BY id", path);
            var text = await RowsAsync(connection, "SELECT JSON_EXTRACT(j, @p), j ->> @p, j -> @p FROM docs ORDER BY id", path);
            var binary = await RowsAsync(connection, "SELECT JSON_EXTRACT(b, @p), b ->> @p, b -> @p FROM docs ORDER BY id", path);
            for (var row = 0; row < Documents.Length; row++)
            {
                for (var column = 0; column < 3; column++)
                {
                    // El texto de un json es el que tenía, sin espacios, como en SQLite; el de un jsonb, el de su forma.
                    Same(expected[row][column], text[row][column], exactText: true, $"{Documents[row]} {path} column {column} (json)");
                    if (!Documents[row].Contains("\"b\":1,\"b\"", StringComparison.Ordinal))
                    {
                        Same(expected[row][column], binary[row][column], exactText: false, $"{Documents[row]} {path} column {column} (jsonb)");
                    }

                    compared++;
                }
            }
        }

        Assert.True(compared > 500);

        // Una clave o una posición sin $ también es una ruta, como en SQLite.
        var shortExpected = await RowsAsync(sqlite, "SELECT j -> 'a', j ->> 'a', j -> 1, j ->> 2 FROM docs ORDER BY id", "$");
        var shortActual = await RowsAsync(connection, "SELECT j -> 'a', j ->> 'a', j -> 1, j ->> 2 FROM docs ORDER BY id", "$");
        for (var row = 0; row < Documents.Length; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                Same(shortExpected[row][column], shortActual[row][column], exactText: true, $"{Documents[row]} short form, column {column}");
            }
        }

        // Y se encadena: el resultado de -> es un documento.
        Assert.Equal(["3"], await QueryAsync(connection, "SELECT j -> 'a' -> 2 ->> 'b' FROM docs WHERE id = 8"));

        // Las rutas mal escritas dicen dónde.
        await RefusedAsync<TkSqlException>(connection, "SELECT JSON_EXTRACT(j, '$..a') FROM docs", "is not a JSON path");
        await RefusedAsync<TkSqlException>(connection, "SELECT JSON_EXTRACT(j, 'a') FROM docs", "must start with $");
    }

    [Fact]
    public async Task Json_value_query_exists_and_type_say_what_a_path_holds()
    {
        await using var database = await OpenAsync(NewDevice());
        await using var connection = new TkConnection(database);
        await using var sqlite = new SqliteConnection("Data Source=:memory:");
        await sqlite.OpenAsync(Ct);
        await RunAsync(connection, "CREATE TABLE docs (id INT PRIMARY KEY, j JSONB NOT NULL)");
        await RunAsync(connection, """INSERT INTO docs (id, j) VALUES (1, '{"sku":"a-1","price":12.50,"tags":["x","y"],"stock":{"here":3},"gone":null,"ok":true}')""");
        await RunAsync(connection, """INSERT INTO docs (id, j) VALUES (2, '{"sku":"b-2","price":7,"tags":[],"stock":{"here":0},"ok":false}')""");

        Assert.Equal(["a-1|12.5|||3", "b-2|7|||0"], await QueryAsync(connection, "SELECT JSON_VALUE(j, '$.sku'), JSON_VALUE(j, '$.price'), JSON_VALUE(j, '$.tags'), JSON_VALUE(j, '$.gone'), JSON_VALUE(j, '$.stock.here') FROM docs ORDER BY id"));
        Assert.Equal(["""|["x","y"]|{"here":3}""", """|[]|{"here":0}"""], await QueryAsync(connection, "SELECT JSON_QUERY(j, '$.sku'), JSON_QUERY(j, '$.tags'), JSON_QUERY(j, '$.stock') FROM docs ORDER BY id"));
        Assert.Equal(["True|True|False", "False|True|False"], await QueryAsync(connection, "SELECT JSON_EXISTS(j, '$.gone'), JSON_EXISTS(j, '$.tags'), JSON_EXISTS(j, '$.nothing') FROM docs ORDER BY id"));

        // json_type, igual que en SQLite.
        foreach (var path in new[] { "$", "$.sku", "$.price", "$.tags", "$.stock", "$.gone", "$.ok", "$.nothing" })
        {
            var document = """{"sku":"a-1","price":12.50,"tags":["x","y"],"stock":{"here":3},"gone":null,"ok":true}""";
            Assert.Equal(await QueryAsync(sqlite, "SELECT json_type(@d, @p)", ("d", document), ("p", path)), await QueryAsync(connection, "SELECT JSON_TYPE(j, @p) FROM docs WHERE id = 1", ("p", path)));
        }

        Assert.Equal(["real", "integer"], await QueryAsync(connection, "SELECT JSON_TYPE(j, '$.price') FROM docs ORDER BY id"));

        // En WHERE, ORDER BY y con agregados, como cualquier otro valor.
        Assert.Equal(["2", "1"], await QueryAsync(connection, "SELECT id FROM docs ORDER BY j ->> '$.price'"));
        Assert.Equal(["1"], await QueryAsync(connection, "SELECT id FROM docs WHERE j ->> '$.ok' AND j -> 'stock' ->> 'here' > 2"));
        Assert.Equal(["19.5"], await QueryAsync(connection, "SELECT SUM(JSON_VALUE(j, '$.price')) FROM docs"));
    }

    private static void Same(object? expected, object? actual, bool exactText, string where)
    {
        switch (expected)
        {
            case null:
                Assert.True(actual is null, $"{where}: expected NULL, got {actual}");
                break;
            case long whole when actual is bool flag:
                Assert.True(whole == (flag ? 1 : 0), $"{where}: expected {whole}, got {flag}");
                break;
            case long or double when actual is long or double or decimal:
                var x = Convert.ToDouble(expected, CultureInfo.InvariantCulture);
                var y = Convert.ToDouble(actual, CultureInfo.InvariantCulture);
                Assert.True(x == y || Math.Abs(x - y) <= Math.Abs(x) * 1e-15, $"{where}: expected {x}, got {y}");
                break;
            case string text when exactText:
                Assert.Equal(text, actual as string ?? actual?.ToString());
                break;
            case string text:
                // Un jsonb escribe sus claves en orden: se compara lo que dicen.
                var mine = actual as string ?? actual?.ToString();
                Assert.True(
                    TkJson.TryParse(text, out var a) && TkJson.TryParse(mine ?? string.Empty, out var b) ? a == b : text == mine,
                    $"{where}: expected {text}, got {mine}");
                break;
            default:
                Assert.Fail($"{where}: SQLite gave {expected} ({expected.GetType().Name})");
                break;
        }
    }

    private static async Task<List<object?[]>> RowsAsync(DbConnection connection, string sql, string path)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "p";
        parameter.Value = path;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i))]);
        }

        return rows;
    }
}

public sealed class SqlJsonTests_File : SqlJsonTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class SqlJsonTests_Memory : SqlJsonTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
