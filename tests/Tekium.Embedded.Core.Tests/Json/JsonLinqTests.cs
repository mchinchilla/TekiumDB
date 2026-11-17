// Copyright (c) Tekium. Licensed under the MIT License.

using System.Linq.Expressions;
using Tekium.Embedded.Linq;
using Tekium.Embedded.Tests.Database;
using Tekium.Embedded.Tests.Storage;

namespace Tekium.Embedded.Tests.Json;

/// <summary>
/// Predicados de la LINQ propia sobre un valor en una ruta: se traducen al campo del índice que la declara,
/// y dan las filas que da el mismo lambda sobre objetos. Una ruta sin índice se rechaza, no se filtra en memoria.
/// </summary>
public abstract class JsonLinqTests : DatabaseTests
{
    [Fact]
    public async Task A_predicate_on_a_path_gives_the_rows_the_same_lambda_gives_over_objects()
    {
        await using var database = await OpenAsync(NewDevice());
        var goods = database.Table<Good>();
        var random = new Random(53);
        var rows = new List<Good>();
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            for (var i = 0; i < 300; i++)
            {
                var sku = random.Next(5) == 0 ? string.Empty : $"\"sku\":\"s{i:D3}\",";
                var price = random.Next(4) switch
                {
                    0 => "\"price\":null",
                    1 => "\"price\":\"free\"",
                    _ => $"\"price\":{random.Next(-100, 1000) / 8.0:0.###}",
                };
                var good = new Good { Id = i, Doc = TkJson.Parse("{" + sku + price + "}"), Name = $"g{i}" };
                rows.Add(good);
                await goods.InsertAsync(writer, good, Ct);
            }

            await writer.CommitAsync(Ct);
        }

        var cut = 40.5;
        var cases = new (Expression<Func<Good, bool>> Predicate, Func<Good, bool> Expected)[]
        {
            (g => g.Doc.TextAt("$.sku") == "s042", g => g.Doc.TextAt("$.sku") == "s042"),
            (g => g.Doc.TextAt("$.\"sku\"") == "s999", g => g.Doc.TextAt("$.sku") == "s999"),
            (g => g.Doc.TextAt("$.sku") == null, g => g.Doc.TextAt("$.sku") == null),
            (g => g.Doc.TextAt("$.sku") != null, g => g.Doc.TextAt("$.sku") != null),
            (g => g.Doc.NumberAt("$.price") > cut, g => g.Doc.NumberAt("$.price") > cut),
            (g => g.Doc.NumberAt("$.price") <= 0 && g.Id > 100, g => g.Doc.NumberAt("$.price") <= 0 && g.Id > 100),
            (g => g.Doc.NumberAt("$.price") >= 10 && g.Doc.NumberAt("$.price") < 20, g => g.Doc.NumberAt("$.price") >= 10 && g.Doc.NumberAt("$.price") < 20),
            (g => g.Doc.NumberAt("$.price") == null, g => g.Doc.NumberAt("$.price") == null),
        };
        foreach (var (predicate, expected) in cases)
        {
            var wanted = rows.Where(expected).Select(static g => g.Id).Order().ToList();
            var found = (await goods.Where(predicate).ToListAsync(Ct)).Select(static g => g.Id).Order().ToList();
            Assert.True(wanted.SequenceEqual(found), $"{predicate}: expected {wanted.Count} rows, found {found.Count}");
        }

        Assert.Equal(new TkPlan(TkAccess.UniqueLookup, "by_sku"), await goods.Where(g => g.Doc.TextAt("$.sku") == "s042").ExplainAsync(Ct));
        Assert.Equal(new TkPlan(TkAccess.IndexScan, "by_price"), await goods.Where(g => g.Doc.NumberAt("$.price") > 100).ExplainAsync(Ct));

        // Una ruta que ningún índice declara, o leída como lo que su índice no guarda, se rechaza diciendo qué.
        Refused(g => g.Doc.TextAt("$.name") == "x", "No [TkJsonIndex] of Good declares");
        Refused(g => g.Doc.NumberAt("$.sku") == 3, "No [TkJsonIndex] of Good declares");
        Refused(g => g.Doc.TextAt(g.Name) == "x", "the path depends on the row");
    }

    private static void Refused(Expression<Func<Good, bool>> predicate, string mention)
    {
        var failure = Assert.Throws<NotSupportedException>(() => TkLinq.ToFilter(predicate));
        Assert.Contains(mention, failure.Message, StringComparison.Ordinal);
    }
}

public sealed class JsonLinqTests_File : JsonLinqTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class JsonLinqTests_Memory : JsonLinqTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
