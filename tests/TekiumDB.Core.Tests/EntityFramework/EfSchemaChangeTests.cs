// Copyright (c) Tekium. Licensed under the MIT License.

using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using TekiumDB.EntityFrameworkCore;
using TekiumDB.Sql.Storage;
using TekiumDB.Tests.Database;
using TekiumDB.Tests.Storage;

namespace TekiumDB.Tests.EntityFramework;

/// <summary>
/// Migraciones cuyas operaciones salen del comparador de modelos de EF Core, como las escribe el andamiaje:
/// renombrar una columna, cambiar su tipo y si admite NULL, renombrar una tabla, quitar una columna y
/// renombrar la columna de una clave foránea. La misma cadena se aplica con MigrateAsync sobre TekiumDB y
/// sobre SQLite, y tiene que dejar las mismas columnas, los mismos datos y el mismo historial.
/// </summary>
public abstract class EfSchemaChangeTests : DatabaseTests
{
    private static readonly string[] All = [.. Enumerable.Range(1, Mall.Last).Select(Mall.MigrationId)];

    [Fact]
    public async Task Migrations_made_by_the_model_differ_rename_retype_and_drop_like_on_sqlite()
    {
        await using var database = await OpenAsync(NewDevice());
        await using var sqlite = new SqliteConnection("Data Source=:memory:");
        await sqlite.OpenAsync(Ct);
        var reference = Mall.Options(new DbContextOptionsBuilder<Mall>().UseSqlite(sqlite));
        var tekium = Mall.Options(new DbContextOptionsBuilder<Mall>().UseTekium(database));
        var last = new List<string>();

        async Task SameAsync(Func<Mall, Task<List<string>>> work)
        {
            List<string> expected;
            await using (var db = new Mall(reference))
            {
                expected = await work(db);
            }

            await using (var db = new Mall(tekium))
            {
                last = await work(db);
            }

            Assert.Equal(expected, last);
        }

        // Primera migración y unas filas.
        await SameAsync(static async db =>
        {
            await db.GetService<IMigrator>().MigrateAsync(Mall.MigrationId(1), Ct);
            await db.Database.ExecuteSqlRawAsync("""INSERT INTO "Aisles" ("Id", "Name") VALUES (1, 'front'), (2, 'back')""", Ct);
            await db.Database.ExecuteSqlRawAsync(
                """INSERT INTO "Items" ("Id", "Title", "Pages", "Isbn", "AisleId") VALUES (1, 'one', 10, NULL, 1), (2, 'two', 2000000000, 'i2', 1), (3, 'three', 30, 'i3', 2)""", Ct);
            return await ShowAsync(db, "Items", "Id", "Title", "Pages", "Isbn", "AisleId");
        });
        Assert.Equal(["Id|Title|Pages|Isbn|AisleId", "1|one|10||1", "2|two|2000000000|i2|1", "3|three|30|i3|2"], last);

        // Una columna renombrada.
        await SameAsync(static async db => await StepAsync(db, 2, "Items", "Id", "Name", "Pages", "Isbn", "AisleId"));
        Assert.Equal("1|one|10||1", last[1]);

        // INT a BIGINT y TEXT que admitía NULL a TEXT que no: el NULL toma el texto vacío, como en SQLite.
        await SameAsync(static async db =>
        {
            var rows = await StepAsync(db, 3, "Items", "Id", "Name", "Pages", "Isbn", "AisleId");
            await db.Database.ExecuteSqlRawAsync("""UPDATE "Items" SET "Pages" = 5000000000 WHERE "Id" = 3""", Ct);
            return [.. rows, .. await ShowAsync(db, "Items", "Pages")];
        });
        Assert.Contains("1|one|10||1", last);
        Assert.Contains("5000000000", last);

        // Una tabla renombrada, con su clave primaria, sus índices y su clave foránea.
        await SameAsync(static async db => await StepAsync(db, 4, "Products", "Id", "Name", "Pages", "Isbn", "AisleId"));

        // Una columna quitada, con su índice, y la columna de una clave foránea renombrada.
        await SameAsync(static async db => await StepAsync(db, 5, "Products", "Id", "Name", "Pages", "Aisle"));
        Assert.Equal(["Id|Name|Pages|Aisle", "1|one|10|1", "2|two|2000000000|1", "3|three|5000000000|2"], last[..4]);

        // El modelo final funciona encima, y la clave foránea sigue en la base.
        await SameAsync(static async db =>
        {
            db.Add(new Ware { Name = "four", Pages = 4, AisleId = 2 });
            await db.SaveChangesAsync(Ct);
            db.ChangeTracker.Clear();
            db.Add(new Ware { Name = "lost", AisleId = 99 });
            var refused = await FailsAsync(() => db.SaveChangesAsync(Ct));
            db.ChangeTracker.Clear();
            var wares = await db.Wares.Include(static w => w.Aisle).OrderBy(static w => w.Id).ToListAsync(Ct);
            return [$"{refused}", .. wares.Select(static w => $"{w.Id}|{w.Name}|{w.Pages}|{w.Aisle!.Name}")];
        });
        Assert.Equal(["True", "1|one|10|front", "2|two|2000000000|front", "3|three|5000000000|back", "4|four|4|back"], last);

        // Hacia atrás hasta la tercera, y otra vez hacia adelante. Volver a la cuarta devuelve el ISBN, que
        // no admite NULL y es único: con más de una fila, las dos bases rechazarían el ''. Queda una.
        await SameAsync(static async db =>
        {
            await db.Database.ExecuteSqlRawAsync("""DELETE FROM "Products" WHERE "Id" > 1""", Ct);
            await db.GetService<IMigrator>().MigrateAsync(Mall.MigrationId(3), Ct);
            var back = await ShowAsync(db, "Items", "Id", "Name", "Pages", "Isbn", "AisleId");
            await db.Database.MigrateAsync(Ct);
            return [.. back, .. await db.Database.GetAppliedMigrationsAsync(Ct), .. await ShowAsync(db, "Products", "Id", "Name", "Pages", "Aisle")];
        });
        Assert.Equal(["Id|Name|Pages|Isbn|AisleId", "1|one|10||1", .. All, "Id|Name|Pages|Aisle", "1|one|10|1"], last);

        Assert.True((await database.VerifyAsync(Ct)).IsHealthy);
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task A_migration_whose_change_does_not_fit_the_rows_fails_before_anything_changes()
    {
        await using var database = await OpenAsync(NewDevice());
        var tekium = Mall.Options(new DbContextOptionsBuilder<Mall>().UseTekium(database));
        await using var db = new Mall(tekium);
        await db.GetService<IMigrator>().MigrateAsync(Mall.MigrationId(2), Ct);

        // Dos filas sin ISBN: a NOT NULL, las dos tendrían '' y el índice es único.
        await db.Database.ExecuteSqlRawAsync("""INSERT INTO "Aisles" ("Id", "Name") VALUES (1, 'front')""", Ct);
        await db.Database.ExecuteSqlRawAsync("""INSERT INTO "Items" ("Id", "Name", "Pages", "Isbn", "AisleId") VALUES (1, 'one', 1, NULL, 1), (2, 'two', 2, NULL, 1)""", Ct);
        var failure = await Assert.ThrowsAsync<TekiumDB.Sql.TkSqlException>(() => db.Database.MigrateAsync(Ct));
        Assert.Contains("unique index 'IX_Items_Isbn'", failure.Message, StringComparison.Ordinal);
        Assert.Equal(All[..2], await db.Database.GetAppliedMigrationsAsync(Ct));
        Assert.Equal(["Id|Isbn", "1|", "2|"], await ShowAsync(db, "Items", "Id", "Isbn"));

        // Arreglados los datos, sigue.
        await db.Database.ExecuteSqlRawAsync("""UPDATE "Items" SET "Isbn" = 'x' WHERE "Id" = 2""", Ct);
        await db.Database.MigrateAsync(Ct);
        Assert.Equal(All, await db.Database.GetAppliedMigrationsAsync(Ct));
        Assert.True((await database.VerifyAsync(Ct)).IsHealthy);
        await AssertNoLeaksAsync(database);
    }

    /// <summary>Aplica una migración y devuelve las columnas de la tabla (nombre y si admite NULL) y sus filas.</summary>
    private static async Task<List<string>> StepAsync(Mall db, int migration, string table, params string[] columns)
    {
        await db.GetService<IMigrator>().MigrateAsync(Mall.MigrationId(migration), Ct);
        return [.. await ShowAsync(db, table, columns), .. await ColumnsAsync(db, table)];
    }

    private static async Task<bool> FailsAsync(Func<Task<int>> work)
    {
        try
        {
            await work();
            return false;
        }
        catch (DbUpdateException)
        {
            return true;
        }
    }

    private static async Task<List<string>> ShowAsync(DbContext db, string table, params string[] columns)
    {
        await db.Database.OpenConnectionAsync(Ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", columns.Select(static column => $"\"{column}\""))} FROM \"{table}\" ORDER BY \"{columns[0]}\"";
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var lines = new List<string> { string.Join('|', columns) };
        while (await reader.ReadAsync(Ct))
        {
            lines.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? string.Empty : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
        }

        return lines;
    }

    /// <summary>Las columnas de una tabla, por nombre, y si admiten NULL: de la base, no del modelo.</summary>
    private static async Task<List<string>> ColumnsAsync(DbContext db, string table)
    {
        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            await db.Database.OpenConnectionAsync(Ct);
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"SELECT name, \"notnull\" FROM pragma_table_info('{table}') ORDER BY name";
            await using var reader = await command.ExecuteReaderAsync(Ct);
            var columns = new List<string>();
            while (await reader.ReadAsync(Ct))
            {
                columns.Add($"{reader.GetString(0)} {(reader.GetInt64(1) == 1 ? "NOT NULL" : "NULL")}");
            }

            return columns;
        }

        var database = ((TekiumDB.Sql.TkConnection)db.Database.GetDbConnection()).Db;
        await using var read = database.BeginRead();
        var found = (await SqlTable.OpenAsync(read, table, Ct))!;
        return [.. found.Columns.OrderBy(static column => column.Name, StringComparer.Ordinal).Select(static column => $"{column.Name} {(column.IsNullable ? "NULL" : "NOT NULL")}")];
    }
}

public sealed class EfSchemaChangeTests_File : EfSchemaChangeTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class EfSchemaChangeTests_Memory : EfSchemaChangeTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}

public sealed class Aisle
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public List<Ware> Wares { get; } = [];
}

public sealed class Ware
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public long Pages { get; set; }

    public string? Isbn { get; set; }

    public int AisleId { get; set; }

    public Aisle? Aisle { get; set; }
}

/// <summary>
/// Un modelo en cinco versiones. Cada migración es la diferencia entre dos, calculada por EF Core para el
/// proveedor que la aplica, como la escribiría <c>dotnet ef migrations add</c>.
/// </summary>
public sealed class Mall(DbContextOptions<Mall> options, int version = Mall.Last) : DbContext(options)
{
    public const int Last = 5;

    public int Version => version;

    public DbSet<Aisle> Aisles => Set<Aisle>();

    public DbSet<Ware> Wares => Set<Ware>();

    public static string MigrationId(int version) => $"2026110{version}000000_Mall{version}";

    public static DbContextOptions<Mall> Options(DbContextOptionsBuilder<Mall> builder) =>
        builder.ReplaceService<IModelCacheKeyFactory, Keys>().ConfigureWarnings(static w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options;

    /// <summary>The model of a version as a provider maps it. Null before the first.</summary>
    public static IModel? ModelOf(string provider, int version)
    {
        if (version == 0)
        {
            return null;
        }

        var builder = new DbContextOptionsBuilder<Mall>();
        var options = Options(provider == "Microsoft.EntityFrameworkCore.Sqlite" ? builder.UseSqlite("Data Source=:memory:") : builder.UseTekium("Data Source=model-only.tkdb"));
        using var db = new Mall(options, version);
        return db.GetService<IDesignTimeModel>().Model;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var ware = modelBuilder.Entity<Ware>();
        ware.ToTable(version >= 4 ? "Products" : "Items");
        ware.HasOne(static w => w.Aisle).WithMany(static a => a.Wares).HasForeignKey(static w => w.AisleId);
        ware.Property(static w => w.Name).HasColumnName(version >= 2 ? "Name" : "Title");
        if (version >= 3)
        {
            ware.Property(static w => w.Isbn).IsRequired();
        }
        else
        {
            // Hasta la tercera versión la columna es de 32 bits.
            ware.Property(static w => w.Pages).HasConversion<int>();
        }

        if (version >= 5)
        {
            ware.Ignore(static w => w.Isbn);
            ware.Property(static w => w.AisleId).HasColumnName("Aisle");
        }
        else
        {
            ware.HasIndex(static w => w.Isbn).IsUnique();
        }
    }

    private sealed class Keys : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => (context.GetType(), ((Mall)context).Version, designTime);
    }
}

/// <summary>Una migración de <see cref="Mall"/>: lo que hay que hacer para pasar de la versión anterior a esta.</summary>
public abstract class MallMigration(int version) : Migration
{
    public override IModel TargetModel => Mall.ModelOf(ActiveProvider!, version)!;

    protected override void Up(MigrationBuilder migrationBuilder) => Differ(migrationBuilder, version - 1, version);

    protected override void Down(MigrationBuilder migrationBuilder) => Differ(migrationBuilder, version, version - 1);

    private static void Differ(MigrationBuilder migrationBuilder, int from, int to)
    {
        var provider = migrationBuilder.ActiveProvider!;
        using var db = new Mall(Mall.Options(provider == "Microsoft.EntityFrameworkCore.Sqlite"
            ? new DbContextOptionsBuilder<Mall>().UseSqlite("Data Source=:memory:")
            : new DbContextOptionsBuilder<Mall>().UseTekium("Data Source=model-only.tkdb")), Math.Max(from, to));
        var differ = db.GetService<IMigrationsModelDiffer>();
        migrationBuilder.Operations.AddRange(differ.GetDifferences(Mall.ModelOf(provider, from)?.GetRelationalModel(), Mall.ModelOf(provider, to)?.GetRelationalModel()));
    }
}

[DbContext(typeof(Mall))]
[Migration("20261101000000_Mall1")]
public sealed class Mall1() : MallMigration(1);

[DbContext(typeof(Mall))]
[Migration("20261102000000_Mall2")]
public sealed class Mall2() : MallMigration(2);

[DbContext(typeof(Mall))]
[Migration("20261103000000_Mall3")]
public sealed class Mall3() : MallMigration(3);

[DbContext(typeof(Mall))]
[Migration("20261104000000_Mall4")]
public sealed class Mall4() : MallMigration(4);

[DbContext(typeof(Mall))]
[Migration("20261105000000_Mall5")]
public sealed class Mall5() : MallMigration(5);
