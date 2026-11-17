// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Sql;
using Tekium.Embedded.Sql.Storage;
using Tekium.Embedded.Tests.Database;
using Tekium.Embedded.Tests.Storage;

namespace Tekium.Embedded.Tests.Sql;

/// <summary>
/// El acceso a tablas sin su tipo de fila, que es lo que usa SQL: lo que escribe por ahí es, byte por
/// byte, lo que escribiría la API tipada (filas e índices), y lo lee igual.
/// </summary>
public abstract class SqlTableTests : DatabaseTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Rows_written_without_the_row_type_are_the_rows_the_typed_table_reads_and_its_indexes_find_them()
    {
        await using var database = await OpenAsync(NewDevice());
        var items = database.Table<Item>();
        var random = new Random(3);
        var model = new SortedDictionary<long, Item>();

        // La primera escritura es tipada: crea la tabla con sus índices declarados.
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            model[0] = NewItem(random, 0);
            await items.InsertAsync(writer, model[0], Ct);
            await writer.CommitAsync(Ct);
        }

        for (var round = 0; round < 6; round++)
        {
            await using var writer = await database.BeginWriteAsync(Ct);
            var table = (await SqlTable.OpenAsync(writer, "ITEMS", Ct))!;
            Assert.Null(table.ReadOnlyReason);
            for (var i = 0; i < 150; i++)
            {
                var id = random.Next(400);
                var existing = model.GetValueOrDefault(id);
                switch (random.Next(4))
                {
                    case 0 when existing is not null:
                        await table.DeleteAsync(Values(existing), Ct);
                        model.Remove(id);
                        break;
                    case 1 when existing is not null:
                        var changed = NewItem(random, id);
                        await table.UpdateAsync(Values(existing), Values(changed), Ct);
                        model[id] = changed;
                        break;
                    case >= 2 when existing is null:
                        model[id] = NewItem(random, id);
                        await table.InsertAsync(Values(model[id]), Ct);
                        break;
                    default:
                        break;
                }
            }

            await writer.CommitAsync(Ct);

            // Leído por el tipo generado: las mismas filas, y sus índices las encuentran.
            Assert.Equal(model.Values, await ToListAsync(items.ScanAsync(Ct)));
            foreach (var owner in new[] { "owner0", "owner3", "owner7" })
            {
                var byOwner = Item.Fields.Owner.EqualTo(owner);
                Assert.Equal(TkAccess.IndexScan, (await items.ExplainAsync(byOwner, Ct)).Access);
                Assert.Equal(
                    model.Values.Where(item => item.Owner == owner).Select(static item => item.Id),
                    (await ToListAsync(items.QueryAsync(byOwner, Ct))).Select(static item => item.Id).Order());
            }

            var sample = model.Values.First(static item => item.Code is not null);
            Assert.Equal(sample.Id, Assert.Single(await ToListAsync(items.QueryAsync(Item.Fields.Code.EqualTo(sample.Code!), Ct))).Id);
            foreach (var index in new[] { "Owner", "Code", "Owner_Qty" })
            {
                Assert.Equal(model.Count, await CountIndexEntriesAsync(database, "items", index));
            }

            await AssertNoLeaksAsync(database);
        }

        // Y al revés: lo que escribe el tipo, se lee sin él, por clave y por índice.
        await using var read = database.BeginRead();
        var plain = (await SqlTable.OpenAsync(read, "items", Ct))!;
        var rows = new List<object?[]>();
        await foreach (var row in plain.ScanAsync(default, default, Ct))
        {
            rows.Add(row);
        }

        Assert.Equal(model.Values.Select(Values), rows);
        var first = model.Values.First();
        Assert.Equal(Values(first), await plain.GetAsync(plain.Key(first.Id), Ct));
        var owners = plain.Indexes.Single(static index => index.Name == "Owner");
        var prefix = SqlTable.IndexPrefix(owners, ["owner3"], 1);
        var found = new List<long>();
        await foreach (var row in plain.IndexAsync(owners, prefix, SqlTable.After(prefix), Ct))
        {
            found.Add((long)row[0]!);
        }

        Assert.Equal(model.Values.Where(static item => item.Owner == "owner3").Select(static item => item.Id), found.Order());
        Assert.True((await database.VerifyAsync(Ct)).IsHealthy);
    }

    [Fact]
    public async Task What_a_write_would_break_is_refused_before_anything_changes()
    {
        await using var database = await OpenAsync(NewDevice());
        await using (var first = await database.BeginWriteAsync(Ct))
        {
            await database.Table<Item>().InsertAsync(first, new Item { Id = 1, Owner = "a", Code = "X" }, Ct);
            await first.CommitAsync(Ct);
        }

        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            var table = (await SqlTable.OpenAsync(writer, "items", Ct))!;
            await Assert.ThrowsAsync<TkDuplicateKeyException>(async () => await table.InsertAsync(Values(new Item { Id = 1, Owner = "b" }), Ct));
            await Assert.ThrowsAsync<TkSqlException>(async () => await table.InsertAsync([2L, null, null, 0, 0m, null, false], Ct));

            // Dos filas sin código no chocan en el índice único; dos con el mismo, sí, y eso aborta la transacción.
            await table.InsertAsync(Values(new Item { Id = 2, Owner = "b" }), Ct);
            await table.InsertAsync(Values(new Item { Id = 3, Owner = "b" }), Ct);
            var violation = await Assert.ThrowsAsync<TkConstraintException>(async () => await table.InsertAsync(Values(new Item { Id = 4, Owner = "b", Code = "X" }), Ct));
            Assert.Equal(("items", "Code"), (violation.Table, violation.Constraint));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await writer.CommitAsync(Ct));
        }

        Assert.Equal(1, Assert.Single(await ToListAsync(database.Table<Item>().ScanAsync(Ct))).Id);
    }

    [Fact]
    public async Task A_table_with_what_only_its_row_type_can_keep_is_read_but_not_written()
    {
        await using var database = await OpenAsync(NewDevice());
        await using (var first = await database.BeginWriteAsync(Ct))
        {
            await database.Table<Ticket>().InsertAsync(first, new Ticket { Id = 1, Owner = "a", Title = "One" }, Ct);
            await database.Table<Author>().InsertAsync(first, new Author { Handle = "ana", Name = "Ana" }, Ct);
            await database.Table<Book>().InsertAsync(first, new Book { Id = 1, Author = "ana", Title = "First", Isbn = "1" }, Ct);
            await first.CommitAsync(Ct);
        }

        await using var writer = await database.BeginWriteAsync(Ct);
        var tickets = (await SqlTable.OpenAsync(writer, "tickets", Ct))!;
        Assert.Contains("expression", tickets.ReadOnlyReason, StringComparison.Ordinal);
        Assert.Equal("One", (await tickets.GetAsync(tickets.Key(1L), Ct))![tickets.Column("title")]);
        var refused = await Assert.ThrowsAsync<TkSqlException>(async () => await tickets.DeleteAsync((await tickets.GetAsync(tickets.Key(1L), Ct))!, Ct));
        Assert.Contains("through its row type", refused.Message, StringComparison.Ordinal);

        Assert.Contains("CHECK constraints", (await SqlTable.OpenAsync(writer, "books", Ct))!.ReadOnlyReason, StringComparison.Ordinal);
        Assert.Null(await SqlTable.OpenAsync(writer, "nothing", Ct));

        // Una tabla a la que otra se refiere sí se escribe: SQL sabe a quién preguntar antes de borrar una fila.
        var authors = (await SqlTable.OpenAsync(writer, "authors", Ct))!;
        Assert.Null(authors.ReadOnlyReason);
        await authors.InsertAsync(["bea", null, "Bea"], Ct);
        await authors.DeleteAsync((await authors.GetAsync(authors.Key("bea"), Ct))!, Ct);
        var referred = await Assert.ThrowsAsync<TkConstraintException>(async () => await authors.DeleteAsync((await authors.GetAsync(authors.Key("ana"), Ct))!, Ct));
        Assert.Equal("fk_Author", referred.Constraint);
        Assert.Equal("books", referred.Table);
    }

    [Fact]
    public async Task The_estimate_of_a_range_comes_from_the_shape_of_the_tree_and_is_close()
    {
        await using var database = await OpenAsync(NewDevice());
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            for (var i = 0; i < 20_000; i++)
            {
                await database.Table<Item>().InsertAsync(writer, new Item { Id = i, Owner = $"owner{i % 50}", Qty = i }, Ct);
            }

            await writer.CommitAsync(Ct);
        }

        await using var read = database.BeginRead();
        var table = (await SqlTable.OpenAsync(read, "items", Ct))!;
        Assert.InRange(await table.EstimateAsync(table.Root, default, default, Ct), 10_000, 40_000);
        Assert.InRange(await table.EstimateAsync(table.Root, table.Key(5000L), table.Key(15_000L), Ct), 5_000, 20_000);
        Assert.Equal(3, await table.EstimateAsync(table.Root, table.Key(100L), table.Key(103L), Ct));
        Assert.Equal(0, await table.EstimateAsync(table.Root, table.Key(50_000L), table.Key(50_010L), Ct));
        var owners = table.Indexes.Single(static index => index.Name == "Owner");
        var prefix = SqlTable.IndexPrefix(owners, ["owner7"], 1);
        Assert.InRange(await table.EstimateAsync(owners.CurrentRoot, prefix, SqlTable.After(prefix), Ct), 200, 800);
    }

    private static Item NewItem(Random random, long id) => new()
    {
        Id = id,
        Owner = $"owner{random.Next(9)}",
        Code = random.Next(3) == 0 ? null : $"code-{id}-{random.Next(1000)}",
        Qty = random.Next(100),
        Price = random.Next(10_000) / 100m,
        At = random.Next(4) == 0 ? null : Epoch.AddMinutes(random.Next(100_000)),
        Active = random.Next(2) == 0,
    };

    private static object?[] Values(Item item) => [item.Id, item.Owner, item.Code, item.Qty, item.Price, item.At, item.Active];
}

public sealed class SqlTableTests_File : SqlTableTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class SqlTableTests_Memory : SqlTableTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
