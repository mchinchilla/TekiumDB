// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Catalog;
using Tekium.Embedded.Sql;
using Tekium.Embedded.Storage;
using Tekium.Embedded.Sql.Storage;
using Tekium.Embedded.Tests.Sql;
using Tekium.Embedded.Tests.Storage;
using static Tekium.Embedded.Tests.Sql.SqlTests;

namespace Tekium.Embedded.Tests.Database;

/// <summary>
/// Preparar una tabla con su tipo es lo que hace la primera escritura de ese tipo, sin la fila: la tabla
/// existe (vacía, o con la versión nueva de su esquema) y SQL la ve como la ve la API tipada.
/// </summary>
public abstract class EnsureTests : DatabaseTests
{
    private static readonly Guid Ana = Guid.Parse("01940000-0000-7000-8000-000000000001");
    private static readonly Guid Luis = Guid.Parse("01940000-0000-7000-8000-000000000002");

    [Fact]
    public async Task A_table_prepared_and_never_written_is_what_sql_makes_and_sql_sees_it()
    {
        var device = NewDevice();
        await using (var typed = await OpenAsync(device))
        {
            await using var writer = await typed.BeginWriteAsync(Ct);
            await typed.Table<Team>().EnsureAsync(writer, Ct);
            await typed.Table<Player>().EnsureAsync(writer, Ct);
            await writer.CommitAsync(Ct);
        }

        await using var made = await OpenAsync(NewDevice());
        await using (var sql = new TkConnection(made))
        {
            await RunAsync(sql, "CREATE TABLE teams (Id INT PRIMARY KEY, Name TEXT NOT NULL)");
            await RunAsync(sql, """
                CREATE TABLE players (
                    Id BIGINT PRIMARY KEY,
                    Team INT REFERENCES teams,
                    Captain BIGINT,
                    Name TEXT NOT NULL,
                    CONSTRAINT captain FOREIGN KEY (Captain) REFERENCES players (Id))
                """);
        }

        await using var database = await OpenAsync(device);
        foreach (var table in new[] { "teams", "players" })
        {
            Assert.Equal(await CatalogAsync(made, table), await CatalogAsync(database, table));
        }

        // Vacía, y SQL escribe en ella comprobando lo que el tipo declara.
        await using var connection = new TkConnection(database);
        Assert.Equal(["0"], await QueryAsync(connection, "SELECT COUNT(*) FROM players"));
        Assert.Equal(1, await RunAsync(connection, "INSERT INTO teams (Id, Name) VALUES (1, 'one')"));
        Assert.Equal(1, await RunAsync(connection, "INSERT INTO players (Id, Team, Name) VALUES (1, 1, 'ana')"));
        Assert.Equal("fk_Team", (await Assert.ThrowsAsync<TkConstraintException>(async () => await RunAsync(connection, "INSERT INTO players (Id, Team, Name) VALUES (2, 9, 'bea')"))).Constraint);
        Assert.Equal([new Player { Id = 1, Team = 1, Name = "ana" }], await ToListAsync(database.Table<Player>().ScanAsync(Ct)));
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task Sql_sees_the_new_columns_of_a_prepared_type_and_reads_the_old_rows_as_the_typed_api_does_without_rewriting_them()
    {
        var device = NewDevice();
        await using var database = await OpenAsync(device);
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await database.Table<ContactV1>().InsertAsync(writer, new ContactV1 { Id = Ana, Name = "Ana", Legacy = 1, Phone = "555-0101" }, Ct);
            await database.Table<ContactV1>().InsertAsync(writer, new ContactV1 { Id = Luis, Name = "Luis", Legacy = 2 }, Ct);
            await writer.CommitAsync(Ct);
        }

        var before = await TableAsync(database, "contacts");
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await database.Table<ContactV2>().EnsureAsync(writer, Ct);
            await writer.CommitAsync(Ct);
        }

        // Otra versión del esquema, y las filas donde estaban.
        var after = await TableAsync(database, "contacts");
        Assert.Equal(before.SchemaVersion + 1, after.SchemaVersion);
        Assert.Equal(before.Root, after.Root);

        await using var connection = new TkConnection(database);
        var typed = await ToListAsync(database.Table<ContactV2>().ScanAsync(Ct));
        Assert.Equal(
            typed.Select(static contact => $"{contact.FullName}|{contact.Phone}|{contact.Email}|{contact.Score}|{contact.Since}|{contact.Notes}"),
            await QueryAsync(connection, "SELECT Name, Phone, Email, Score, Since, Notes FROM contacts ORDER BY Name"));
        await RefusedAsync<TkSqlException>(connection, "SELECT Legacy FROM contacts", "Legacy");
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task Preparing_a_table_that_is_already_as_its_type_declares_spends_no_version_and_writes_nothing()
    {
        await using var database = await OpenAsync(NewDevice());
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await database.Table<Book>().EnsureAsync(writer, Ct);
            await writer.CommitAsync(Ct);
        }

        var catalog = await CatalogRootAsync(database);
        for (var round = 0; round < 2; round++)
        {
            await using var writer = await database.BeginWriteAsync(Ct);
            await database.Table<Book>().EnsureAsync(writer, Ct);
            await database.Table<Book>().EnsureAsync(writer, Ct);
            await writer.CommitAsync(Ct);
            Assert.Equal(catalog, await CatalogRootAsync(database));
        }

        // Preparada y escrita en la misma transacción: la escritura encuentra la tabla como la dejó la preparación.
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await database.Table<Author>().EnsureAsync(writer, Ct);
            await database.Table<Author>().InsertAsync(writer, new Author { Handle = "ana", Name = "Ana" }, Ct);
            await database.Table<Book>().InsertAsync(writer, new Book { Id = 1, Author = "ana", Title = "One", Isbn = "1" }, Ct);
            await writer.CommitAsync(Ct);
        }

        Assert.Equal(1, (await TableAsync(database, "books")).SchemaVersion);
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task A_new_constraint_the_rows_break_aborts_the_transaction_and_a_new_index_is_built_from_the_rows()
    {
        await using var database = await OpenAsync(NewDevice());
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await database.Table<Author>().InsertAsync(writer, new Author { Handle = "ana", Name = "Ana" }, Ct);
            await database.Table<BookLoose>().InsertAsync(writer, new BookLoose { Id = 1, Author = "ana", Title = "One", Isbn = "1" }, Ct);
            await database.Table<BookLoose>().InsertAsync(writer, new BookLoose { Id = 2, Author = "ana", Title = "Two", Isbn = "2", Pages = 0 }, Ct);
            await writer.CommitAsync(Ct);
        }

        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            var broken = await Assert.ThrowsAsync<TkConstraintException>(async () => await database.Table<Book>().EnsureAsync(writer, Ct));
            Assert.Equal("HasPages", broken.Constraint);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(async () => await writer.CommitAsync(Ct));
        }

        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await database.Table<BookLoose>().UpdateAsync(writer, new BookLoose { Id = 2, Author = "ana", Title = "Two", Isbn = "2", Pages = 5 }, Ct);
            await database.Table<Book>().EnsureAsync(writer, Ct);
            await writer.CommitAsync(Ct);
        }

        // Los índices que el tipo declara ya están, con una entrada por fila.
        await using var connection = new TkConnection(database);
        Assert.Contains(await QueryAsync(connection, "EXPLAIN SELECT Id FROM books WHERE Author = 'ana' AND Title = 'Two'"), static line => line.Contains("one title per author", StringComparison.Ordinal));
        Assert.Equal(["2"], await QueryAsync(connection, "SELECT Id FROM books WHERE Author = 'ana' AND Title = 'Two'"));
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            var duplicate = await Assert.ThrowsAsync<TkConstraintException>(async () =>
                await database.Table<Book>().InsertAsync(writer, new Book { Id = 3, Author = "ana", Title = "One", Isbn = "3" }, Ct));
            Assert.Equal("one title per author", duplicate.Constraint);
        }

        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task The_tools_take_a_prepared_table_that_has_no_rows()
    {
        var device = NewDevice();
        await using (var database = await OpenAsync(device))
        {
            await using (var writer = await database.BeginWriteAsync(Ct))
            {
                await database.Table<Team>().EnsureAsync(writer, Ct);
                await database.Table<Player>().EnsureAsync(writer, Ct);
                await writer.CommitAsync(Ct);
            }

            Assert.True((await database.VerifyAsync(Ct)).IsHealthy);
            var backup = NewDevice();
            await database.BackupAsync(_ => backup, null, null, Ct);
            await using var restored = await OpenAsync(backup);
            await using var connection = new TkConnection(restored);
            Assert.Equal(["0"], await QueryAsync(connection, "SELECT COUNT(*) FROM players"));
            Assert.Equal(await CatalogAsync(database, "players"), await CatalogAsync(restored, "players"));
        }

        Assert.True((await TkInspector.InspectAsync(() => device, default, Ct)).IsHealthy);
        var compacted = NewDevice();
        await TkMaintenance.CopyAsync(() => device, _ => compacted, null, salvage: false, Ct);
        await using var copy = await OpenAsync(compacted);
        await using var copied = new TkConnection(copy);
        Assert.Equal(1, await RunAsync(copied, "INSERT INTO teams (Id, Name) VALUES (1, 'one')"));
        Assert.Equal(["1"], await QueryAsync(copied, "SELECT COUNT(*) FROM teams"));
    }

    [Fact]
    public async Task A_table_prepared_before_the_one_it_refers_to_is_the_same_as_prepared_after()
    {
        var device = NewDevice();
        await using var first = await OpenAsync(device);
        await using (var writer = await first.BeginWriteAsync(Ct))
        {
            await first.Table<Player>().EnsureAsync(writer, Ct);
            await first.Table<Team>().EnsureAsync(writer, Ct);
            await writer.CommitAsync(Ct);
        }

        await using var second = await OpenAsync(NewDevice());
        await using (var writer = await second.BeginWriteAsync(Ct))
        {
            await second.Table<Team>().EnsureAsync(writer, Ct);
            await second.Table<Player>().EnsureAsync(writer, Ct);
            await writer.CommitAsync(Ct);
        }

        foreach (var table in new[] { "teams", "players" })
        {
            Assert.Equal(await CatalogAsync(second, table), await CatalogAsync(first, table));
        }

        await using var connection = new TkConnection(first);
        Assert.Equal("fk_Team", (await Assert.ThrowsAsync<TkConstraintException>(async () => await RunAsync(connection, "INSERT INTO players (Id, Team, Name) VALUES (1, 9, 'ana')"))).Constraint);
    }

    [Fact]
    public async Task Preparing_checks_its_transaction()
    {
        await using var database = await OpenAsync(NewDevice());
        await using var other = await OpenAsync(NewDevice());
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await database.Table<Team>().EnsureAsync(null!, Ct));
        await using var writer = await other.BeginWriteAsync(Ct);
        await Assert.ThrowsAsync<ArgumentException>(async () => await database.Table<Team>().EnsureAsync(writer, Ct));
    }

    private static async Task<PageNumber> CatalogRootAsync(TekiumDb database)
    {
        await using var reader = database.BeginRead();
        return reader.CatalogRoot;
    }

    private static async Task<TableInfo> TableAsync(TekiumDb database, string table)
    {
        await using var reader = database.BeginRead();
        return (await CatalogStore.FindTableAsync(reader.Source, reader.CatalogRoot, CatalogStore.TableKey(table), Ct))!.Value;
    }

    private static async Task<string> CatalogAsync(TekiumDb database, string table)
    {
        await using var reader = database.BeginRead();
        var key = CatalogStore.TableKey(table);
        var constraints = await CatalogStore.ReadAsync(reader.Source, reader.CatalogRoot, CatalogStore.ConstraintsKey(key), Ct);
        var referrers = await CatalogStore.ReadAsync(reader.Source, reader.CatalogRoot, CatalogStore.ReferrersKey(key), Ct);
        var opened = (await SqlTable.OpenAsync(reader, table, Ct))!;
        Assert.Null(opened.ReadOnlyReason);
        var columns = string.Join(',', opened.Columns.Select(static column => $"{column.Name}:{column.Type}:{column.IsNullable}"));
        var indexes = string.Join(',', opened.Indexes.Select(static index => $"{index.Name}={Convert.ToHexString(index.Definition)}"));
        return $"{columns} key {opened.KeyColumn} v{opened.Version} {Convert.ToHexString(constraints)} {Convert.ToHexString(referrers)} {indexes}";
    }
}

public sealed class EnsureTests_File : EnsureTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class EnsureTests_Memory : EnsureTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
