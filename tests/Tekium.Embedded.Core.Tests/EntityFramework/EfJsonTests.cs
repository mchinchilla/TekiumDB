// Copyright (c) Tekium. Licensed under the MIT License.

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Tekium.Embedded.EntityFrameworkCore;
using Tekium.Embedded.Tests.Database;
using Tekium.Embedded.Tests.Storage;

namespace Tekium.Embedded.Tests.EntityFramework;

/// <summary>
/// Columnas JSON desde EF Core: un documento guardado en una propiedad de texto se consulta por ruta igual
/// que en SQLite; una propiedad <see cref="TkJson"/> o <see cref="System.Text.Json.JsonElement"/> va y viene.
/// </summary>
public abstract class EfJsonTests : DatabaseTests
{
    private static readonly string[] Documents =
    [
        """{"sku":"a-1","price":12.5,"tags":["x","y"],"size":{"w":3}}""",
        """{"sku":"b-2","price":7,"tags":[]}""",
        """{"sku":"c-3","size":{"w":10}}""",
        """{"price":100}""",
        """ { "sku" : "d-4" , "price" : 0.25 } """,
    ];

    [Fact]
    public async Task Documents_in_a_text_property_are_read_by_path_as_on_sqlite()
    {
        await using var sqlite = new SqliteConnection("Data Source=:memory:");
        await sqlite.OpenAsync(Ct);
        await using var database = await OpenAsync(NewDevice());
        var reference = new DbContextOptionsBuilder<Stock>().UseSqlite(sqlite).Options;
        var tekium = new DbContextOptionsBuilder<Stock>().UseTekium(database).Options;
        foreach (var options in new[] { reference, tekium })
        {
            await using var db = new Stock(options);
            await db.Database.EnsureCreatedAsync(Ct);
            db.Items.AddRange(Documents.Select(static (document, i) => new StockItem { Id = i + 1, Doc = document }));
            await db.SaveChangesAsync(Ct);
        }

        Func<Stock, Task<List<string>>>[] queries =
        [
            static async db => [.. await db.Items.Where(static i => Json.Extract(i.Doc, "$.sku") == "b-2").Select(static i => i.Id.ToString()).ToListAsync(Ct)],
            static async db => [.. await db.Items.OrderBy(static i => i.Id).Select(static i => Json.Extract(i.Doc, "$.sku") ?? "-").ToListAsync(Ct)],
            static async db => [.. await db.Items.OrderBy(static i => i.Id).Select(static i => Json.Extract(i.Doc, "$.size") ?? "-").ToListAsync(Ct)],
            static async db => [.. await db.Items.OrderBy(static i => i.Id).Select(static i => Json.Extract(i.Doc, "$.tags[1]") ?? "-").ToListAsync(Ct)],
            static async db => [.. await db.Items.Where(static i => Json.Extract(i.Doc, "$.size.w") != null).OrderBy(static i => i.Id).Select(static i => i.Id.ToString()).ToListAsync(Ct)],
            static async db => [.. await db.Items.OrderBy(static i => Json.Extract(i.Doc, "$.sku")).ThenBy(static i => i.Id).Select(static i => i.Id.ToString()).ToListAsync(Ct)],
        ];
        foreach (var query in queries)
        {
            List<string> expected;
            await using (var db = new Stock(reference))
            {
                expected = await query(db);
            }

            await using (var db = new Stock(tekium))
            {
                Assert.Equal(expected, await query(db));
            }
        }

        // Lo que no es JSON no se guarda en una columna JSON.
        await using (var db = new Stock(tekium))
        {
            db.Items.Add(new StockItem { Id = 99, Doc = "{not json" });
            var refused = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("not valid JSON", refused.InnerException!.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_TkJson_or_JsonElement_property_is_stored_and_read_and_queried_with_its_functions()
    {
        await using var database = await OpenAsync(NewDevice());
        var options = new DbContextOptionsBuilder<Catalog>().UseTekium(database).Options;
        await using (var db = new Catalog(options))
        {
            await db.Database.EnsureCreatedAsync(Ct);
            for (var i = 0; i < Documents.Length; i++)
            {
                db.Entries.Add(new CatalogEntry
                {
                    Id = i + 1,
                    Attributes = TkJson.Parse(Documents[i]),
                    Raw = TkJson.Parse(Documents[i]),
                    Element = System.Text.Json.JsonDocument.Parse(Documents[i]).RootElement.Clone(),
                    Optional = i % 2 == 0 ? TkJson.Parse("[1]") : null,
                });
            }

            await db.SaveChangesAsync(Ct);
        }

        await using (var db = new Catalog(options))
        {
            var loaded = await db.Entries.OrderBy(static e => e.Id).ToListAsync(Ct);
            Assert.Equal(Documents.Select(static d => TkJson.Parse(d)), loaded.Select(static e => e.Attributes));
            Assert.Equal(TkJsonFormat.Binary, loaded[0].Attributes.Format);
            Assert.Equal(Documents[4], loaded[4].Raw.ToString());
            Assert.Equal("d-4", loaded[4].Element.GetProperty("sku").GetString());
            Assert.Equal([true, false, true, false, true], loaded.Select(static e => e.Optional is not null));

            // Las funciones del proveedor, contra lo mismo leído en memoria.
            var skus = await db.Entries.OrderBy(static e => e.Id).Select(static e => EF.Functions.JsonValue(e.Attributes, "$.sku")).ToListAsync(Ct);
            Assert.Equal(loaded.Select(static e => e.Attributes.TextAt("$.sku") is var sku && sku is ['{', ..] ? null : sku), skus);
            var cheap = await db.Entries.Where(static e => EF.Functions.JsonValue(e.Attributes, "$.sku") == "b-2").Select(static e => e.Id).ToListAsync(Ct);
            Assert.Equal([2], cheap);
            var sized = await db.Entries.Where(static e => EF.Functions.JsonExists(e.Raw, "$.size")).OrderBy(static e => e.Id).Select(static e => e.Id).ToListAsync(Ct);
            Assert.Equal(loaded.Where(static e => e.Raw.TryGet(TkJsonPath.Parse("$.size"), out _)).Select(static e => e.Id), sized);

            // Cambiar el documento es cambiar la propiedad entera: un TkJson es inmutable.
            var first = loaded[0];
            first.Attributes = TkJson.Parse("""{"sku":"changed"}""");
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = new Catalog(options))
        {
            Assert.Equal("changed", (await db.Entries.SingleAsync(static e => e.Id == 1, Ct)).Attributes.TextAt("$.sku"));
        }
    }
}

public sealed class EfJsonTests_File : EfJsonTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class EfJsonTests_Memory : EfJsonTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}

/// <summary>json_extract de SQLite y JSON_EXTRACT de TekiumDB, que dan lo mismo: la misma función desde LINQ.</summary>
public static class Json
{
    public static string? Extract(string document, string path) => throw new NotSupportedException("Only in a query.");
}

public sealed class StockItem
{
    public int Id { get; set; }

    public string Doc { get; set; } = "{}";
}

public sealed class Stock(DbContextOptions<Stock> options) : DbContext(options)
{
    public DbSet<StockItem> Items => Set<StockItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var sqlite = Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite";
        modelBuilder.Entity<StockItem>().Property(static i => i.Id).ValueGeneratedNever();
        modelBuilder.Entity<StockItem>().Property(static i => i.Doc).HasColumnType(sqlite ? "TEXT" : "JSON");
        modelBuilder.HasDbFunction(typeof(Json).GetMethod(nameof(Json.Extract))!).HasName(sqlite ? "json_extract" : "JSON_EXTRACT");
    }
}

public sealed class CatalogEntry
{
    public int Id { get; set; }

    public TkJson Attributes { get; set; }

    public TkJson Raw { get; set; }

    public System.Text.Json.JsonElement Element { get; set; }

    public TkJson? Optional { get; set; }
}

public sealed class Catalog(DbContextOptions<Catalog> options) : DbContext(options)
{
    public DbSet<CatalogEntry> Entries => Set<CatalogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CatalogEntry>().Property(static e => e.Id).ValueGeneratedNever();
        modelBuilder.Entity<CatalogEntry>().Property(static e => e.Raw).HasColumnType("JSON");
    }
}
