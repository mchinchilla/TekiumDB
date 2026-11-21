// Copyright (c) Tekium. Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using TekiumDB.Sql;

namespace TekiumDB.Benchmarks;

/// <summary>
/// JSON kept as text frente a su forma binaria: leer un valor por una ruta, leer y comprobar la fila, y una
/// consulta SQL por una ruta con y sin índice. Se corre con <c>-- json [filas]</c>.
/// </summary>
internal static class JsonReport
{
    private const int Rounds = 5;

    public static async Task RunAsync(int rows)
    {
        var document = Document(7);
        var text = TkJson.Parse(document);
        var binary = text.ToBinary();
        var shallow = TkJsonPath.Parse("$.k03");
        var deep = TkJsonPath.Parse("$.items[18].tags[2]");
        await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Documento de {Encoding.UTF8.GetByteCount(document):N0} bytes de texto y {binary.GetBinary().Length:N0} en binario; mejor de {Rounds} corridas."));
        await Console.Out.WriteLineAsync("| Qué | json (texto) | jsonb (binario) |");
        await Console.Out.WriteLineAsync("| --- | ---: | ---: |");
        await LineAsync("Leer $.k03 (una clave del primer nivel)", Per(() => text.TryGet(shallow, out _)), Per(() => binary.TryGet(shallow, out _)), "ns");
        await LineAsync("Leer $.items[18].tags[2] (tres niveles, hacia el final)", Per(() => text.TryGet(deep, out _)), Per(() => binary.TryGet(deep, out _)), "ns");
        await LineAsync("Comprobar el documento leído de una fila", Per(() => TkJson.FromStored(text.Bytes, TkJsonFormat.Text)), Per(() => TkJson.FromStored(binary.Bytes, TkJsonFormat.Binary)), "ns");

        var timings = new List<(string What, double Text, double Binary)>();
        foreach (var kind in new[] { "JSON", "JSONB" })
        {
            var database = await TekiumDb.OpenAsync(new DbOptions { InMemory = true, CacheSizeMb = 512 });
            await using (database)
            {
                await using var connection = new TkConnection(database);
                await ExecuteAsync(connection, $"CREATE TABLE docs (id INT PRIMARY KEY, doc {kind} NOT NULL)");
                await using (var transaction = await connection.BeginTransactionAsync())
                {
                    await using var insert = connection.CreateCommand();
                    insert.CommandText = "INSERT INTO docs (id, doc) VALUES (@id, @doc)";
                    var id = new TkParameter("id", 0);
                    var doc = new TkParameter("doc", string.Empty);
                    insert.Parameters.Add(id);
                    insert.Parameters.Add(doc);
                    for (var i = 0; i < rows; i++)
                    {
                        id.Value = i;
                        doc.Value = Document(i);
                        await insert.ExecuteNonQueryAsync();
                    }

                    await transaction.CommitAsync();
                }

                var scan = await BestAsync(connection, "SELECT COUNT(*) FROM docs WHERE JSON_VALUE(doc, '$.k03') = 'v3-77'");
                await ExecuteAsync(connection, "CREATE INDEX by_k03 ON docs (JSON_VALUE(doc, '$.k03'))");
                var indexed = await BestAsync(connection, "SELECT COUNT(*) FROM docs WHERE JSON_VALUE(doc, '$.k03') = 'v3-77'");
                timings.Add(($"Consulta SQL por $.k03 sin índice ({rows:N0} filas, recorre todas)", scan, 0));
                timings.Add(("La misma consulta con un índice sobre la ruta", indexed, 0));
            }
        }

        for (var i = 0; i < 2; i++)
        {
            await LineAsync(timings[i].What, timings[i].Text * 1000, timings[i + 2].Text * 1000, "µs");
        }
    }

    private static string Document(int seed)
    {
        var text = new StringBuilder("{");
        for (var k = 0; k < 12; k++)
        {
            text.Append(CultureInfo.InvariantCulture, $"\"k{k:D2}\":\"v{k}-{seed % 100}\",");
        }

        text.Append("\"items\":[");
        for (var i = 0; i < 20; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"{(i > 0 ? "," : string.Empty)}{{\"n\":{i},\"price\":{i * 1.25},\"tags\":[\"a\",\"b\",\"c{i}\"]}}");
        }

        return text.Append("]}").ToString();
    }

    private static double Per(Action work)
    {
        const int Times = 200_000;
        var best = double.MaxValue;
        for (var round = 0; round < Rounds; round++)
        {
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < Times; i++)
            {
                work();
            }

            best = Math.Min(best, watch.Elapsed.TotalNanoseconds / Times);
        }

        return best;
    }

    private static async Task<double> BestAsync(TkConnection connection, string sql)
    {
        var best = double.MaxValue;
        for (var round = 0; round < Rounds; round++)
        {
            var watch = Stopwatch.StartNew();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            _ = await command.ExecuteScalarAsync();
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
        }

        return best;
    }

    private static Task LineAsync(string what, double text, double binary, string unit) =>
        Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"| {what} | {text:N0} {unit} | {binary:N0} {unit} |"));

    private static async Task ExecuteAsync(TkConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
