// Copyright (c) Tekium. Licensed under the MIT License.

using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Tekium.Embedded.EntityFrameworkCore;
using Tekium.Embedded.Tests.Database;
using Tekium.Embedded.Tests.Storage;

namespace Tekium.Embedded.Tests.EntityFramework;

/// <summary>
/// Migraciones y claves foráneas del proveedor de EF Core: la misma cadena de migraciones se aplica
/// sobre TekiumDB y sobre SQLite, y tiene que dejar el mismo esquema, los mismos datos y el mismo historial.
/// </summary>
public abstract class EfMigrationTests : DatabaseTests
{
    private static readonly string[] All =
    [
        "20260101000000_Initial", "20260201000000_AddColumns", "20260301000000_Readers", "20260401000000_Loans", "20260501000000_DropLoans",
    ];

    [Fact]
    public async Task A_chain_of_migrations_leaves_the_schema_the_data_and_the_history_it_leaves_on_sqlite()
    {
        await using var pair = await Both<Library>.CreateAsync(await OpenAsync(NewDevice()), static options => new Library(options));

        // Nada aplicado todavía.
        await pair.SameAsync(static async db => [.. await db.Database.GetPendingMigrationsAsync(Ct)]);
        Assert.Equal(All, pair.Last);
        await pair.SameAsync(static async db => [.. await db.Database.GetAppliedMigrationsAsync(Ct)]);
        Assert.Empty(pair.Last);

        // La primera: dos tablas, una clave foránea, un índice y una fila sembrada.
        await pair.SameAsync(static async db =>
        {
            await db.GetService<IMigrator>().MigrateAsync(All[0], Ct);
            await db.Database.ExecuteSqlRawAsync("""INSERT INTO "Books" ("Id", "Title", "ShelfId") VALUES (1, 'one', 1)""", Ct);
            await db.Database.ExecuteSqlRawAsync("""INSERT INTO "Books" ("Id", "Title", "ShelfId") VALUES (2, 'two', 1)""", Ct);
            return [.. await db.Database.GetAppliedMigrationsAsync(Ct), "-", .. await db.Database.GetPendingMigrationsAsync(Ct)];
        });
        Assert.Equal([All[0], "-", .. All[1..]], pair.Last);

        // La clave foránea está en la base: no entra un libro de un estante que no existe, ni sale un estante con libros.
        await pair.SameAsync(static async db =>
        [
            $"{await FailsAsync(() => db.Database.ExecuteSqlRawAsync("""INSERT INTO "Books" ("Id", "Title", "ShelfId") VALUES (3, 'three', 9)""", Ct))}",
            $"{await FailsAsync(() => db.Database.ExecuteSqlRawAsync("""DELETE FROM "Shelves" WHERE "Id" = 1""", Ct))}",
            .. await DumpAsync(db, """SELECT "Id", "Title", "ShelfId" FROM "Books" ORDER BY "Id" """),
        ]);
        Assert.Equal(["True", "True", "Id|Title|ShelfId", "1|one|1", "2|two|1"], pair.Last);

        // Todas las demás, de una vez; y aplicarlas otra vez no hace nada.
        await pair.SameAsync(static async db =>
        {
            await db.Database.MigrateAsync(Ct);
            await db.Database.MigrateAsync(Ct);
            return [.. await db.Database.GetAppliedMigrationsAsync(Ct), "-", .. await db.Database.GetPendingMigrationsAsync(Ct)];
        });
        Assert.Equal([.. All, "-"], pair.Last);

        // El esquema y los datos: las columnas nuevas, con su valor por omisión en las filas que ya estaban.
        await pair.SameAsync(static async db =>
        [
            .. await DumpAsync(db, """SELECT * FROM "Books" ORDER BY "Id" """),
            .. await DumpAsync(db, """SELECT * FROM "Shelves" ORDER BY "Id" """),
            .. await DumpAsync(db, """SELECT * FROM "Readers" ORDER BY "Id" """),
            .. await DumpAsync(db, """SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" """),
            $"{await FailsAsync(() => DumpAsync(db, """SELECT * FROM "Loans" """))}",
        ]);
        Assert.Equal(
            [
                "Id|Title|ShelfId|Pages|Isbn|ReaderId", "1|one|1|0||", "2|two|1|0||",
                "Id|Name|Room", "1|first|main",
                "Id|Name",
                "MigrationId", .. All,
                "True",
            ],
            pair.Last);

        // El modelo final funciona encima: altas, consultas con relaciones, y lo que las restricciones impiden.
        await pair.SameAsync(static async db =>
        {
            var shelf = new Shelf { Name = "second", Room = "attic" };
            shelf.Books.Add(new Book { Title = "three", Pages = 300, Isbn = "isbn-3", ReaderId = 77 });
            shelf.Books.Add(new Book { Title = "four", Pages = 40 });
            db.Add(shelf);
            db.Add(new Reader { Name = "ana" });
            return [$"{await db.SaveChangesAsync(Ct)}", .. await ShowAsync(db)];
        });
        Assert.Equal(["4", "1|first|main|1 one 0  ,2 two 0  ", "2|second|attic|3 three 300 isbn-3 77,4 four 40  ", "one", "two", "1|ana"], pair.Last);

        await pair.SameAsync(static async db =>
        {
            // Un ISBN repetido, un estante que no existe, y borrar un estante con libros sin haberlos cargado.
            var results = new List<string>();
            db.Add(new Book { Title = "dup", ShelfId = 1, Isbn = "isbn-3" });
            results.Add($"{await FailsAsync(() => db.SaveChangesAsync(Ct))}");
            db.ChangeTracker.Clear();
            db.Add(new Book { Title = "lost", ShelfId = 99 });
            results.Add($"{await FailsAsync(() => db.SaveChangesAsync(Ct))}");
            db.ChangeTracker.Clear();
            db.Remove(await db.Shelves.SingleAsync(static s => s.Name == "second", Ct));
            results.Add($"{await FailsAsync(() => db.SaveChangesAsync(Ct))}");
            db.ChangeTracker.Clear();

            // Borrando también sus libros, EF los manda primero y el estante sale.
            var second = await db.Shelves.Include(static s => s.Books).SingleAsync(static s => s.Name == "second", Ct);
            db.RemoveRange(second.Books);
            db.Remove(second);
            results.Add($"{await db.SaveChangesAsync(Ct)}");
            return [.. results, .. await ShowAsync(db)];
        });
        Assert.Equal(["True", "True", "True", "3"], pair.Last[..4]);

        // Hacia atrás una migración (vuelve la tabla, el índice y la clave foránea) y otra vez hacia adelante.
        await pair.SameAsync(static async db =>
        {
            await db.GetService<IMigrator>().MigrateAsync(All[3], Ct);
            var applied = await db.Database.GetAppliedMigrationsAsync(Ct);
            await db.Database.ExecuteSqlRawAsync("""INSERT INTO "Loans" ("Id", "BookId", "Days") VALUES (1, 1, 14)""", Ct);
            var orphan = await FailsAsync(() => db.Database.ExecuteSqlRawAsync("""INSERT INTO "Loans" ("Id", "BookId", "Days") VALUES (2, 50, 1)""", Ct));
            var unread = await FailsAsync(() => db.Database.ExecuteSqlRawAsync("""UPDATE "Books" SET "ReaderId" = 5 WHERE "Id" = 1""", Ct));
            var loans = await DumpAsync(db, """SELECT * FROM "Loans" """);
            await db.Database.MigrateAsync(Ct);
            return [.. applied, $"{orphan}", $"{unread}", .. loans, .. await db.Database.GetAppliedMigrationsAsync(Ct), .. await ShowAsync(db)];
        });
        Assert.Equal([.. All[..4], "True", "True", "Id|BookId|Days", "1|1|14", .. All, "1|first|main|1 one 0  ,2 two 0  ", "one", "two", "1|ana"], pair.Last);

        Assert.True((await pair.Database.VerifyAsync(Ct)).IsHealthy);
        await AssertNoLeaksAsync(pair.Database);

        static async Task<List<string>> ShowAsync(Library db) =>
        [
            .. (await db.Shelves.Include(static s => s.Books).OrderBy(static s => s.Id).ToListAsync(Ct)).Select(static s =>
                $"{s.Id}|{s.Name}|{s.Room}|{string.Join(',', s.Books.OrderBy(static b => b.Id).Select(static b => $"{b.Id} {b.Title} {b.Pages} {b.Isbn} {b.ReaderId}"))}"),
            .. (await db.Books.Where(static b => b.Shelf!.Room == "main").OrderBy(static b => b.Id).Select(static b => b.Title).ToListAsync(Ct)),
            .. (await db.Readers.OrderBy(static r => r.Id).ToListAsync(Ct)).Select(static r => $"{r.Id}|{r.Name}"),
        ];
    }

    [Fact]
    public async Task A_model_created_without_migrations_has_its_foreign_keys_and_is_deleted_whole()
    {
        await using var database = await OpenAsync(NewDevice());
        var options = new DbContextOptionsBuilder<Library>().UseTekium(database).ConfigureWarnings(static w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options;
        await using var db = new Library(options);
        Assert.True(await db.Database.EnsureCreatedAsync(Ct));

        // La clave foránea usa el índice que EF hace para ella: no hay un segundo índice sobre la misma columna.
        await using (var reader = database.BeginRead())
        {
            var books = (await Tekium.Embedded.Sql.Storage.SqlTable.OpenAsync(reader, "Books", Ct))!;
            Assert.Equal(["IX_Books_Isbn", "IX_Books_ShelfId"], books.Indexes.Select(static index => index.Name));
            Assert.Equal([("FK_Books_Shelves_ShelfId", "Shelves", "IX_Books_ShelfId")], books.ForeignKeys.Select(static key => (key.Info.Name, key.Info.Table, key.Info.Index)));
        }

        db.Add(new Book { Title = "lost", ShelfId = 99 });
        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        var violation = Assert.IsType<TkConstraintException>(refused.InnerException);
        Assert.Equal("FK_Books_Shelves_ShelfId", violation.Constraint);
        db.ChangeTracker.Clear();

        var shelf = new Shelf { Name = "only", Room = "main" };
        shelf.Books.Add(new Book { Title = "kept" });
        db.Add(shelf);
        Assert.Equal(2, await db.SaveChangesAsync(Ct));

        // Dentro de una transacción de la aplicación, una violación la aborta entera: es lo que hace el motor.
        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            db.Add(new Reader { Name = "gone" });
            await db.SaveChangesAsync(Ct);
            await Assert.ThrowsAsync<TkConstraintException>(() => db.Shelves.Where(static s => s.Name == "only").ExecuteDeleteAsync(Ct));
            await Assert.ThrowsAnyAsync<Exception>(() => transaction.CommitAsync(Ct));
        }

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.Readers.CountAsync(Ct));
        Assert.Equal(1, await db.Shelves.CountAsync(Ct));

        // Borrar el modelo quita las tablas en un orden que las claves foráneas permiten, y el historial.
        await db.Database.ExecuteSqlRawAsync(db.GetService<IHistoryRepository>().GetCreateIfNotExistsScript(), Ct);
        await db.Database.ExecuteSqlRawAsync(db.GetService<IHistoryRepository>().GetCreateIfNotExistsScript(), Ct);
        Assert.Empty(await db.Database.GetAppliedMigrationsAsync(Ct));
        Assert.True(await db.Database.EnsureDeletedAsync(Ct));
        await using (var reader = database.BeginRead())
        {
            foreach (var table in new[] { "Books", "Shelves", "Readers", "__EFMigrationsHistory" })
            {
                Assert.Null(await Tekium.Embedded.Sql.Storage.SqlTable.OpenAsync(reader, table, Ct));
            }
        }

        Assert.True(await db.Database.EnsureCreatedAsync(Ct));
        Assert.Equal(0, await db.Books.CountAsync(Ct));
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task What_a_migration_cannot_do_here_says_what_and_leaves_nothing_behind()
    {
        await using var database = await OpenAsync(NewDevice());
        var options = new DbContextOptionsBuilder<Library>().UseTekium(database).ConfigureWarnings(static w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options;
        await using var db = new Library(options);
        var generator = db.GetService<IMigrationsSqlGenerator>();

        // Lo que reescribiría filas, o lo que el motor no tiene, se rechaza por su nombre.
        (MigrationOperation Operation, string Mention)[] refused =
        [
            (new DropColumnOperation { Table = "Books", Name = "Title" }, "drop column 'Title' of table 'Books'"),
            (new RenameColumnOperation { Table = "Books", Name = "Title", NewName = "Name" }, "rename column 'Title' of table 'Books'"),
            (new AlterColumnOperation { Table = "Books", Name = "Title", ClrType = typeof(string), ColumnType = "TEXT" }, "change column 'Title' of table 'Books'"),
            (new RenameTableOperation { Name = "Books", NewName = "Tomes" }, "rename table 'Books'"),
            (new RenameIndexOperation { Table = "Books", Name = "IX_Books_Isbn", NewName = "IX" }, "rename index 'IX_Books_Isbn'"),
            (new AddPrimaryKeyOperation { Table = "Books", Name = "PK", Columns = ["Id"] }, "change the primary key of table 'Books'"),
            (new DropPrimaryKeyOperation { Table = "Books", Name = "PK" }, "change the primary key of table 'Books'"),
            (new AddCheckConstraintOperation { Table = "Books", Name = "CK", Sql = "Pages > 0" }, "CHECK constraint 'CK'"),
            (new DropCheckConstraintOperation { Table = "Books", Name = "CK" }, "CHECK constraint 'CK'"),
            (new CreateSequenceOperation { Name = "numbers", ClrType = typeof(long) }, "sequences"),
            (new EnsureSchemaOperation { Name = "audit" }, "schemas"),
            (new AddForeignKeyOperation { Table = "Books", Name = "FK", Columns = ["ShelfId", "ReaderId"], PrincipalTable = "Shelves", PrincipalColumns = ["Id", "Name"] }, "one column"),
            (new CreateIndexOperation { Table = "Books", Name = "IX", Columns = ["Pages"], Filter = "Pages > 0" }, "filter"),
            (new AddColumnOperation { Table = "Books", Name = "Stamp", ClrType = typeof(string), ColumnType = "TEXT", DefaultValueSql = "now()" }, "DefaultValueSql"),
            (new AddColumnOperation { Table = "Books", Name = "Twice", ClrType = typeof(int), ColumnType = "INT", ComputedColumnSql = "Pages * 2" }, "computed"),
            (new CreateIndexOperation { Table = "Books", Name = "IX", Columns = ["Pages"], IsDescending = [true] }, "descending"),
            (Table(new AddColumnOperation { Table = "T", Name = "Id", ClrType = typeof(int), ColumnType = "INT", DefaultValue = 7 }), "default or computed value"),
            (Table(new AddColumnOperation { Table = "T", Name = "Id", ClrType = typeof(int), ColumnType = "INT" }, new AddCheckConstraintOperation { Table = "T", Name = "CK_T", Sql = "Id > 0" }), "CHECK constraint 'CK_T'"),
        ];
        foreach (var (operation, mention) in refused)
        {
            var failure = Assert.Throws<NotSupportedException>(() => generator.Generate([operation], db.Model));
            Assert.Contains(mention, failure.Message, StringComparison.Ordinal);
        }

        // Lo que sí: cada operación es una sentencia que el SQL de TekiumDB entiende.
        var commands = generator.Generate(
            [
                new AddColumnOperation { Table = "Books", Name = "Stars", ClrType = typeof(int), ColumnType = "INT", IsNullable = false, DefaultValue = 4 },
                new AddColumnOperation { Table = "Books", Name = "Note", ClrType = typeof(string), ColumnType = "TEXT", IsNullable = true },
                new AddUniqueConstraintOperation { Table = "Books", Name = "AK_Books_Title", Columns = ["Title"] },
                new DropUniqueConstraintOperation { Table = "Books", Name = "AK_Books_Title" },
                new DropIndexOperation { Table = "Books", Name = "IX_Books_Isbn" },
                new DropForeignKeyOperation { Table = "Books", Name = "FK_Books_Shelves_ShelfId" },
            ],
            db.Model);
        Assert.Equal(
            [
                """ALTER TABLE "Books" ADD COLUMN "Stars" INT NOT NULL DEFAULT 4;""",
                """ALTER TABLE "Books" ADD COLUMN "Note" TEXT;""",
                """CREATE UNIQUE INDEX "AK_Books_Title" ON "Books" ("Title");""",
                """DROP INDEX "AK_Books_Title" ON "Books";""",
                """DROP INDEX "IX_Books_Isbn" ON "Books";""",
                """ALTER TABLE "Books" DROP CONSTRAINT "FK_Books_Shelves_ShelfId";""",
            ],
            commands.Select(static command => command.CommandText.Trim()));

        // Síncrono, no; y un script que decida solo qué aplicar, tampoco.
        Assert.Contains("MigrateAsync", Assert.ThrowsAny<NotSupportedException>(() => db.Database.Migrate()).Message, StringComparison.Ordinal);
        var migrator = db.GetService<IMigrator>();
        Assert.Contains("idempotent", Assert.Throws<NotSupportedException>(() => migrator.GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent)).Message, StringComparison.Ordinal);
        Assert.Contains("""ALTER TABLE "Books" ADD CONSTRAINT "FK_Books_Shelves_ShelfId" FOREIGN KEY ("ShelfId") REFERENCES "Shelves" ("Id")""", migrator.GenerateScript(), StringComparison.Ordinal);

        // Una cadena cuya segunda migración no se puede aplicar: no queda nada de la segunda.
        var broken = new DbContextOptionsBuilder<Annex>().UseTekium(database).ConfigureWarnings(static w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options;
        await using var annex = new Annex(broken);
        var failed = await Assert.ThrowsAsync<NotSupportedException>(() => annex.Database.MigrateAsync(Ct));
        Assert.Contains("drop column 'Label' of table 'Rooms'", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("20260102000000_Broken", await annex.Database.GetAppliedMigrationsAsync(Ct));
        await using (var reader = database.BeginRead())
        {
            Assert.Null(await Tekium.Embedded.Sql.Storage.SqlTable.OpenAsync(reader, "Halls", Ct));
        }

        Assert.True((await database.VerifyAsync(Ct)).IsHealthy);
        await AssertNoLeaksAsync(database);
    }

    private static CreateTableOperation Table(AddColumnOperation column, AddCheckConstraintOperation? check = null)
    {
        var table = new CreateTableOperation { Name = "T", PrimaryKey = new AddPrimaryKeyOperation { Table = "T", Name = "PK_T", Columns = ["Id"] } };
        table.Columns.Add(column);
        if (check is not null)
        {
            table.CheckConstraints.Add(check);
        }

        return table;
    }

    private static async Task<bool> FailsAsync<T>(Func<Task<T>> work)
    {
        try
        {
            await work();
            return false;
        }
        catch (Exception failure) when (failure is DbUpdateException or SqliteException or TkConstraintException or Tekium.Embedded.Sql.TkSqlException)
        {
            return true;
        }
    }

    /// <summary>Los nombres de las columnas y cada fila, como texto.</summary>
    private static async Task<List<string>> DumpAsync(DbContext db, string sql)
    {
        await db.Database.OpenConnectionAsync(Ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var lines = new List<string> { string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)) };
        while (await reader.ReadAsync(Ct))
        {
            lines.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? string.Empty : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
        }

        return lines;
    }

    /// <summary>El mismo contexto sobre las dos bases.</summary>
    private sealed class Both<TContext> : IAsyncDisposable
        where TContext : DbContext
    {
        private readonly SqliteConnection _sqlite = new("Data Source=:memory:");
        private Func<DbContextOptions<TContext>, TContext> _create = null!;
        private DbContextOptions<TContext> _reference = null!;
        private DbContextOptions<TContext> _tekium = null!;

        public TekiumDb Database { get; private init; } = null!;

        public List<string> Last { get; private set; } = [];

        public static async Task<Both<TContext>> CreateAsync(TekiumDb database, Func<DbContextOptions<TContext>, TContext> create)
        {
            var pair = new Both<TContext> { Database = database, _create = create };
            await pair._sqlite.OpenAsync(Ct);
            pair._reference = new DbContextOptionsBuilder<TContext>().UseSqlite(pair._sqlite).ConfigureWarnings(static w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options;
            pair._tekium = new DbContextOptionsBuilder<TContext>().UseTekium(database).ConfigureWarnings(static w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options;
            return pair;
        }

        public async Task SameAsync(Func<TContext, Task<List<string>>> work)
        {
            List<string> expected;
            await using (var reference = _create(_reference))
            {
                expected = await work(reference);
            }

            await using var tekium = _create(_tekium);
            Last = await work(tekium);
            Assert.Equal(expected, Last);
        }

        public async ValueTask DisposeAsync()
        {
            await _sqlite.DisposeAsync();
            await Database.DisposeAsync();
        }
    }
}

public sealed class Shelf
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Room { get; set; } = string.Empty;

    public List<Book> Books { get; } = [];
}

public sealed class Book
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public int ShelfId { get; set; }

    public Shelf? Shelf { get; set; }

    public int Pages { get; set; }

    public string? Isbn { get; set; }

    public int? ReaderId { get; set; }
}

public sealed class Reader
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>El modelo como lo deja la última migración.</summary>
public sealed class Library(DbContextOptions<Library> options) : DbContext(options)
{
    public DbSet<Shelf> Shelves => Set<Shelf>();

    public DbSet<Book> Books => Set<Book>();

    public DbSet<Reader> Readers => Set<Reader>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Book>().HasOne(static b => b.Shelf).WithMany(static s => s.Books).HasForeignKey(static b => b.ShelfId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Book>().HasIndex(static b => b.Isbn).IsUnique();
    }
}

[DbContext(typeof(Library))]
[Migration("20260101000000_Initial")]
public sealed class InitialMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Shelves",
            columns: static table => new
            {
                Id = table.Column<int>(nullable: false),
                Name = table.Column<string>(nullable: false),
            },
            constraints: static table => table.PrimaryKey("PK_Shelves", static x => x.Id));
        migrationBuilder.CreateTable(
            name: "Books",
            columns: static table => new
            {
                Id = table.Column<int>(nullable: false),
                Title = table.Column<string>(nullable: false),
                ShelfId = table.Column<int>(nullable: false),
            },
            constraints: static table =>
            {
                table.PrimaryKey("PK_Books", static x => x.Id);
                table.ForeignKey("FK_Books_Shelves_ShelfId", static x => x.ShelfId, "Shelves", "Id", onDelete: ReferentialAction.Restrict);
            });
        migrationBuilder.CreateIndex("IX_Books_ShelfId", "Books", "ShelfId");
        migrationBuilder.InsertData("Shelves", ["Id", "Name"], ["INTEGER", "TEXT"], [1, "first"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("Books");
        migrationBuilder.DropTable("Shelves");
    }
}

[DbContext(typeof(Library))]
[Migration("20260201000000_AddColumns")]
public sealed class AddColumnsMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>("Pages", "Books", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<string>("Isbn", "Books", nullable: true);
        migrationBuilder.AddColumn<string>("Room", "Shelves", nullable: false, defaultValue: "main");
        migrationBuilder.CreateIndex("IX_Books_Isbn", "Books", "Isbn", unique: true);
    }
}

[DbContext(typeof(Library))]
[Migration("20260301000000_Readers")]
public sealed class ReadersMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Readers",
            columns: static table => new
            {
                Id = table.Column<int>(nullable: false),
                Name = table.Column<string>(nullable: false),
            },
            constraints: static table => table.PrimaryKey("PK_Readers", static x => x.Id));
        if (SqliteBooks.IsActive(migrationBuilder))
        {
            // SQLite no agrega una clave foránea a una tabla hecha: sólo junto con su columna.
            migrationBuilder.Sql("""ALTER TABLE "Books" ADD COLUMN "ReaderId" INTEGER NULL CONSTRAINT "FK_Books_Readers_ReaderId" REFERENCES "Readers" ("Id") ON DELETE RESTRICT""");
        }
        else
        {
            migrationBuilder.AddColumn<int>("ReaderId", "Books", nullable: true);
            migrationBuilder.AddForeignKey("FK_Books_Readers_ReaderId", "Books", "ReaderId", "Readers", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        }

        migrationBuilder.CreateIndex("IX_Books_ReaderId", "Books", "ReaderId");
    }
}

/// <summary>
/// Lo que SQLite, la referencia, no hace con ALTER TABLE: quitar o poner una clave foránea en una tabla
/// hecha. Allí hay que escribir la tabla otra vez; en TekiumDB es una operación de la migración.
/// </summary>
internal static class SqliteBooks
{
    public static bool IsActive(MigrationBuilder migrationBuilder) => migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite";

    public static void Rebuild(MigrationBuilder migrationBuilder, bool readerKey)
    {
        var key = readerKey ? """, CONSTRAINT "FK_Books_Readers_ReaderId" FOREIGN KEY ("ReaderId") REFERENCES "Readers" ("Id") ON DELETE RESTRICT""" : string.Empty;
        migrationBuilder.Sql($"""
            CREATE TABLE "Books2" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Books" PRIMARY KEY, "Title" TEXT NOT NULL, "ShelfId" INTEGER NOT NULL,
                "Pages" INTEGER NOT NULL, "Isbn" TEXT NULL, "ReaderId" INTEGER NULL,
                CONSTRAINT "FK_Books_Shelves_ShelfId" FOREIGN KEY ("ShelfId") REFERENCES "Shelves" ("Id") ON DELETE RESTRICT{key})
            """);
        migrationBuilder.Sql("""INSERT INTO "Books2" SELECT "Id", "Title", "ShelfId", "Pages", "Isbn", "ReaderId" FROM "Books" """);
        migrationBuilder.Sql("""DROP TABLE "Books" """);
        migrationBuilder.Sql("""ALTER TABLE "Books2" RENAME TO "Books" """);
        migrationBuilder.CreateIndex("IX_Books_ShelfId", "Books", "ShelfId");
        migrationBuilder.CreateIndex("IX_Books_Isbn", "Books", "Isbn", unique: true);
    }
}

[DbContext(typeof(Library))]
[Migration("20260401000000_Loans")]
public sealed class LoansMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Loans",
            columns: static table => new
            {
                Id = table.Column<int>(nullable: false),
                BookId = table.Column<int>(nullable: false),
                Days = table.Column<int>(nullable: false),
            },
            constraints: static table =>
            {
                table.PrimaryKey("PK_Loans", static x => x.Id);
                table.ForeignKey("FK_Loans_Books_BookId", static x => x.BookId, "Books", "Id", onDelete: ReferentialAction.Restrict);
            });
        migrationBuilder.CreateIndex("IX_Loans_BookId", "Loans", "BookId");
    }
}

[DbContext(typeof(Library))]
[Migration("20260501000000_DropLoans")]
public sealed class DropLoansMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("Loans");
        if (SqliteBooks.IsActive(migrationBuilder))
        {
            SqliteBooks.Rebuild(migrationBuilder, readerKey: false);
        }
        else
        {
            migrationBuilder.DropForeignKey("FK_Books_Readers_ReaderId", "Books");
            migrationBuilder.DropIndex("IX_Books_ReaderId", "Books");
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (SqliteBooks.IsActive(migrationBuilder))
        {
            SqliteBooks.Rebuild(migrationBuilder, readerKey: true);
        }
        else
        {
            migrationBuilder.AddForeignKey("FK_Books_Readers_ReaderId", "Books", "ReaderId", "Readers", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        }

        migrationBuilder.CreateTable(
            name: "Loans",
            columns: static table => new
            {
                Id = table.Column<int>(nullable: false),
                BookId = table.Column<int>(nullable: false),
                Days = table.Column<int>(nullable: false),
            },
            constraints: static table =>
            {
                table.PrimaryKey("PK_Loans", static x => x.Id);
                table.ForeignKey("FK_Loans_Books_BookId", static x => x.BookId, "Books", "Id", onDelete: ReferentialAction.Restrict);
            });
        migrationBuilder.CreateIndex("IX_Loans_BookId", "Loans", "BookId");
        migrationBuilder.CreateIndex("IX_Books_ReaderId", "Books", "ReaderId");
    }
}

public sealed class Room
{
    public int Id { get; set; }

    public string Label { get; set; } = string.Empty;
}

/// <summary>Un contexto cuya segunda migración pide lo que TekiumDB no hace.</summary>
public sealed class Annex(DbContextOptions<Annex> options) : DbContext(options)
{
    public DbSet<Room> Rooms => Set<Room>();
}

[DbContext(typeof(Annex))]
[Migration("20260101000000_Rooms")]
public sealed class RoomsMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateTable(
            name: "Rooms",
            columns: static table => new
            {
                Id = table.Column<int>(nullable: false),
                Label = table.Column<string>(nullable: false),
            },
            constraints: static table => table.PrimaryKey("PK_Rooms", static x => x.Id));
}

[DbContext(typeof(Annex))]
[Migration("20260102000000_Broken")]
public sealed class BrokenMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Halls",
            columns: static table => new
            {
                Id = table.Column<int>(nullable: false),
            },
            constraints: static table => table.PrimaryKey("PK_Halls", static x => x.Id));
        migrationBuilder.DropColumn("Label", "Rooms");
    }
}

public sealed class EfMigrationTests_File : EfMigrationTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class EfMigrationTests_Memory : EfMigrationTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
