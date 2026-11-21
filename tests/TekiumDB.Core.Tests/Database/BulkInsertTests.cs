// Copyright (c) Tekium. Licensed under the MIT License.

using TekiumDB.Tests.Storage;

namespace TekiumDB.Tests.Database;

/// <summary>
/// La carga masiva construye la tabla y sus índices de abajo hacia arriba. El resultado tiene que ser el
/// mismo que insertar las filas una por una: la referencia es una lista en memoria.
/// </summary>
public abstract class BulkInsertTests : DatabaseTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public enum Order
    {
        Sorted,
        Shuffled,
        SortedThenNot,
    }

    [Theory]
    [InlineData(Order.Sorted, 0, false)]
    [InlineData(Order.Shuffled, 0, false)]
    [InlineData(Order.SortedThenNot, 0, false)]
    [InlineData(Order.Sorted, 700, false)]
    [InlineData(Order.Shuffled, 700, false)]
    [InlineData(Order.Shuffled, 700, true)]
    [InlineData(Order.Sorted, 0, true)]
    public async Task A_bulk_load_leaves_the_table_and_its_indexes_as_inserting_row_by_row_would(Order order, int already, bool encrypted)
    {
        var device = NewDevice();
        var random = new Random(68);
        var all = Enumerable.Range(0, 5000).Select(i => NewTicket(random, i * 2)).ToList();
        var existing = all.Where((_, i) => i % 7 == 0).Take(already).ToList();
        var loaded = all.Except(existing).ToList();
        loaded = order switch
        {
            Order.Shuffled => [.. loaded.OrderBy(_ => random.Next())],
            Order.SortedThenNot => [.. loaded.Take(3000), .. loaded.Skip(3000).Reverse()],
            _ => loaded,
        };

        await using (var database = await OpenSmallAsync(device, encrypted ? "bulk" : null))
        {
            var tickets = database.Table<Ticket>();
            await using (var writer = await database.BeginWriteAsync(Ct))
            {
                // Lo que ya había se escribe en la misma transacción, antes: la carga lo encuentra a medio confirmar.
                foreach (var ticket in existing)
                {
                    await tickets.InsertAsync(writer, ticket, Ct);
                }

                var most = 0;
                IEnumerable<Ticket> Rows()
                {
                    foreach (var ticket in loaded)
                    {
                        most = Math.Max(most, writer.Inner.Dirty.Count);
                        yield return ticket;
                    }
                }

                Assert.Equal(loaded.Count, await tickets.BulkInsertAsync(writer, Rows(), Ct));

                // Lo que la transacción tiene en memoria no crece con las filas.
                Assert.True(most < 200, $"The load held {most} pages in memory.");
                await AssertTicketsAsync(database, writer, all);
                await writer.CommitAsync(Ct);
            }

            await AssertTicketsAsync(database, null, all);
            await AssertNoLeaksAsync(database);
        }

        // Después de cargada es una tabla como cualquiera: se reabre, se le escribe y se le borra.
        await using var reopened = await OpenSmallAsync(device, encrypted ? "bulk" : null);
        await using (var writer = await reopened.BeginWriteAsync(Ct))
        {
            for (var i = 0; i < 300; i++)
            {
                var ticket = NewTicket(random, random.Next(11_000));
                all.RemoveAll(other => other.Id == ticket.Id);
                if (i % 3 == 0)
                {
                    await reopened.Table<Ticket>().DeleteAsync(writer, ticket.Id, Ct);
                }
                else
                {
                    await reopened.Table<Ticket>().UpsertAsync(writer, ticket, Ct);
                    all.Add(ticket);
                }
            }

            await writer.CommitAsync(Ct);
        }

        await AssertTicketsAsync(reopened, null, all);
        await AssertNoLeaksAsync(reopened);
    }

    /// <summary>
    /// Una carga que cabe entera en memoria no escribe ninguna tanda, pero el árbol nuevo sí vuelca sus
    /// páginas mientras se lee el anterior: lo anterior tiene que estar ya en el archivo, no en memoria.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_load_that_fits_in_memory_still_merges_with_pages_the_transaction_had_not_written_yet(bool existingRows)
    {
        await using var database = await OpenAsync(NewDevice());
        database.BulkSpillPages = 8;
        database.Pager.ScrubSpilledPages = true;
        var counters = database.Table<Counter>();
        var all = Enumerable.Range(0, 6000).Select(static i => new Counter { Id = i, Value = i * 3L }).ToList();
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            if (existingRows)
            {
                // Lo que había lo escribió esta misma transacción, y sigue en memoria.
                foreach (var row in all.Where(static row => row.Id % 2 == 0))
                {
                    await counters.InsertAsync(writer, row, Ct);
                }

                await counters.BulkInsertAsync(writer, all.Where(static row => row.Id % 2 == 1), Ct);
            }
            else
            {
                // Las primeras llegan en orden y van directo al árbol; después una rompe el orden.
                await counters.BulkInsertAsync(writer, all.Take(3000).Concat(all.Skip(3000).Reverse()), Ct);
            }

            await writer.CommitAsync(Ct);
        }

        Assert.Equal(all, await ToListAsync(counters.ScanAsync(Ct)));
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task Rows_can_arrive_from_an_asynchronous_source_and_a_load_of_nothing_changes_nothing()
    {
        await using var database = await OpenSmallAsync(NewDevice(), null);
        var counters = database.Table<Counter>();
        var last = database.LastTransactionId;
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            Assert.Equal(0, await counters.BulkInsertAsync(writer, Array.Empty<Counter>(), Ct));
            await writer.CommitAsync(Ct);
        }

        Assert.Empty(await ToListAsync(counters.ScanAsync(Ct)));

        static async IAsyncEnumerable<Counter> SlowlyAsync()
        {
            for (var i = 0; i < 3000; i++)
            {
                if (i % 500 == 0)
                {
                    await Task.Yield();
                }

                yield return new Counter { Id = i, Value = i * 2L };
            }
        }

        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            Assert.Equal(3000, await counters.BulkInsertAsync(writer, SlowlyAsync(), Ct));
            await writer.CommitAsync(Ct);
        }

        Assert.True(database.LastTransactionId > last);
        Assert.Equal(Enumerable.Range(0, 3000).Select(static i => new Counter { Id = i, Value = i * 2L }), await ToListAsync(counters.ScanAsync(Ct)));
        Assert.Equal(new Counter { Id = 1234, Value = 2468 }, await counters.GetAsync(1234, Ct));
        await AssertNoLeaksAsync(database);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_repeated_key_is_refused_and_nothing_of_the_load_can_be_kept(bool sorted)
    {
        await using var database = await OpenSmallAsync(NewDevice(), null);
        var counters = database.Table<Counter>();
        await InTransactionAsync(database, writer => counters.InsertAsync(writer, new Counter { Id = 500, Value = -1 }, Ct));
        var rows = Enumerable.Range(0, 2000).Select(static i => new Counter { Id = i == 1500 ? 1499 : i, Value = i }).ToList();
        if (!sorted)
        {
            rows.Reverse();
        }

        // Dentro de la carga, sobre una tabla vacía en otra base; y contra lo que la tabla ya tiene.
        await using (var empty = await OpenSmallAsync(NewDevice(), null))
        await using (var writer = await empty.BeginWriteAsync(Ct))
        {
            var duplicate = await Assert.ThrowsAsync<TkDuplicateKeyException>(async () => await empty.Table<Counter>().BulkInsertAsync(writer, rows, Ct));
            Assert.Contains("key 1499", duplicate.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await writer.CommitAsync(Ct));
        }

        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            var duplicate = await Assert.ThrowsAsync<TkDuplicateKeyException>(
                async () => await counters.BulkInsertAsync(writer, rows.Where(static row => row.Id != 1499), Ct));
            Assert.Contains("key 500", duplicate.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await writer.CommitAsync(Ct));
        }

        Assert.Equal([new Counter { Id = 500, Value = -1 }], await ToListAsync(counters.ScanAsync(Ct)));
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task Constraints_hold_for_every_row_of_a_bulk_load()
    {
        await using var database = await OpenSmallAsync(NewDevice(), null);
        var shipments = database.Table<Shipment>();
        await InTransactionAsync(database, async writer =>
        {
            await database.Table<Author>().InsertAsync(writer, new Author { Handle = "dhl" }, Ct);
            await shipments.InsertAsync(writer, new Shipment { Id = -1, Carrier = "dhl", Tracking = "T-existing" }, Ct);
        });

        List<Shipment> Rows(Func<int, Shipment, Shipment> change) =>
            [.. Enumerable.Range(0, 1500).Select(i => change(i, new Shipment { Id = i, Carrier = "dhl", Tracking = i % 3 == 0 ? null : $"T-{i}" }))];

        async Task<TkConstraintException> LoadAsync(List<Shipment> rows)
        {
            await using var writer = await database.BeginWriteAsync(Ct);
            var violation = await Assert.ThrowsAsync<TkConstraintException>(async () => await shipments.BulkInsertAsync(writer, rows, Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await writer.CommitAsync(Ct));
            return violation;
        }

        var check = await LoadAsync(Rows(static (i, row) => i == 900 ? row with { Weight = 0 } : row));
        Assert.Equal(("WeightIsPositive", "900"), (check.Constraint, check.Key));

        var orphan = await LoadAsync(Rows(static (i, row) => i == 901 ? row with { Carrier = "nobody" } : row));
        Assert.Equal(("fk_Carrier", "901"), (orphan.Constraint, orphan.Key));

        // Único: entre dos filas de la carga, y contra una que la tabla ya tenía.
        var twice = await LoadAsync(Rows(static (i, row) => i == 1300 ? row with { Tracking = "T-100" } : row));
        Assert.Equal("Tracking", twice.Constraint);
        Assert.Contains(twice.Key, new[] { "100", "1300" });
        var taken = await LoadAsync(Rows(static (i, row) => i == 5 ? row with { Tracking = "T-existing" } : row));
        Assert.Equal("Tracking", taken.Constraint);
        Assert.Contains(taken.Key, new[] { "-1", "5" });

        // Sin violaciones entra entera; las filas sin valor en la columna única no chocan entre sí.
        await InTransactionAsync(database, async writer => Assert.Equal(1500, await shipments.BulkInsertAsync(writer, Rows(static (_, row) => row), Ct)));
        Assert.Equal(1501, (await ToListAsync(shipments.ScanAsync(Ct))).Count);
        Assert.Equal(500, (await ToListAsync(shipments.QueryAsync(Shipment.Fields.Tracking.IsNull(), Ct))).Count);
        Assert.Equal(new TkPlan(TkAccess.UniqueLookup, "Tracking"), await shipments.ExplainAsync(Shipment.Fields.Tracking.EqualTo("T-100"), Ct));
        Assert.Equal(100, Assert.Single(await ToListAsync(shipments.QueryAsync(Shipment.Fields.Tracking.EqualTo("T-100"), Ct))).Id);
        Assert.Equal("fk_Carrier", (await Assert.ThrowsAsync<TkConstraintException>(async () =>
        {
            await using var writer = await database.BeginWriteAsync(Ct);
            await database.Table<Author>().DeleteAsync(writer, "dhl", Ct);
        })).Constraint);
        await AssertNoLeaksAsync(database);

        // Una tabla que se refiere a sí misma no se carga así: una fila puede referirse a otra que aún no llegó.
        await using var books = await database.BeginWriteAsync(Ct);
        var refused = await Assert.ThrowsAsync<NotSupportedException>(async () => await database.Table<Book>().BulkInsertAsync(books, Array.Empty<Book>(), Ct));
        Assert.Contains("'books'", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bulk_load_that_rolls_back_leaves_nothing_and_one_that_commits_is_reported_by_the_change_feed()
    {
        var device = NewDevice();
        await using var database = await OpenAsync(device, changeRetention: 5);
        database.BulkSpillPages = 16;
        var counters = database.Table<Counter>();
        var rows = Enumerable.Range(0, 4000).Select(static i => new Counter { Id = i, Value = i }).ToList();
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await counters.BulkInsertAsync(writer, rows, Ct);
        }

        Assert.Empty(await ToListAsync(counters.ScanAsync(Ct)));
        await AssertNoLeaksAsync(database);

        var since = database.LastTransactionId;
        await InTransactionAsync(database, async writer => await counters.BulkInsertAsync(writer, rows, Ct));
        var changes = new List<TkChange<Counter>>();
        await foreach (var change in database.WatchAsync<Counter>(since, Ct))
        {
            changes.Add(change);
            if (changes.Count == rows.Count)
            {
                break;
            }
        }

        Assert.All(changes, static change => Assert.Equal(TkChangeKind.Insert, change.Kind));
        Assert.Equal(rows, changes.Select(static change => change.After!));
        await AssertNoLeaksAsync(database);
    }

    private static Ticket NewTicket(Random random, long id) => new()
    {
        Id = id,
        Owner = new[] { "ana", "luis", "eva", string.Empty, "an\0a" }[random.Next(5)],
        Priority = random.Next(-2, 6),
        Due = random.Next(4) == 0 ? null : Epoch.AddHours(random.Next(-200, 200)),
        State = (TicketState)new[] { 0, 0, 1, 9 }[random.Next(4)],
        Deleted = random.Next(3) == 0,
        Cost = random.Next(-300, 300) / 10m,
        Title = $"Title {(char)('A' + random.Next(26))}{random.Next(20)}",
        Body = random.Next(40) == 0 ? new string('x', 6000) : string.Empty,
    };

    /// <summary>Una base que vuelca y ordena en tandas chicas: con pocas filas pasa por todos los caminos.</summary>
    private static async Task<TekiumDb> OpenSmallAsync(TestDevice device, string? password)
    {
        var database = await OpenAsync(device, password);
        database.BulkSpillPages = 24;
        database.BulkSortBudget = 60_000;
        database.Pager.ScrubSpilledPages = true;
        return database;
    }

    private static async Task InTransactionAsync(TekiumDb database, Func<TkWriteTransaction, ValueTask> work)
    {
        await using var writer = await database.BeginWriteAsync(Ct);
        await work(writer);
        await writer.CommitAsync(Ct);
    }

    /// <summary>La tabla, por clave y por cada índice, dice lo mismo que el modelo; y ningún índice tiene entradas de más.</summary>
    private static async Task AssertTicketsAsync(TekiumDb database, TkTransaction? transaction, List<Ticket> model)
    {
        var tickets = database.Table<Ticket>();
        var expected = model.OrderBy(static ticket => ticket.Id).ToList();
        Assert.Equal(expected, transaction is null ? await ToListAsync(tickets.ScanAsync(Ct)) : await ToListAsync(tickets.ScanAsync(transaction, Ct)));

        var to = Epoch.AddHours(40);
        var queries = new (TkFilter<Ticket> Filter, Func<Ticket, bool> Expected)[]
        {
            (Ticket.Fields.Owner.EqualTo("ana"), static ticket => ticket.Owner == "ana"),
            (Ticket.Fields.Owner.EqualTo("an\0a").And(Ticket.Fields.Priority.AtLeast(3)), static ticket => ticket is { Owner: "an\0a", Priority: >= 3 }),
            (Ticket.Fields.Due.Between(Epoch, to), ticket => ticket.Due >= Epoch && ticket.Due <= to),
            (Ticket.Fields.Due.IsNull(), static ticket => ticket.Due is null),
            (Ticket.Fields.TitleLower.EqualTo("title a1"), static ticket => ticket.Title == "Title A1"),
            (Ticket.Fields.Deleted.EqualTo(false).And(Ticket.Fields.State.EqualTo(TicketState.Open)).And(Ticket.Fields.Priority.GreaterThan(2)), static ticket => ticket.IsOpen && ticket.Priority > 2),
            (Ticket.Fields.Id.Between(4000, 4100), static ticket => ticket.Id is >= 4000 and <= 4100),
        };
        foreach (var (filter, matches) in queries)
        {
            var found = transaction is null
                ? await ToListAsync(tickets.QueryAsync(filter, Ct))
                : await ToListAsync(tickets.QueryAsync(transaction, filter, Ct));
            Assert.Equal(expected.Where(matches), found.OrderBy(static ticket => ticket.Id));
        }

        if (transaction is null)
        {
            foreach (var index in new[] { "Owner", "Due", "TitleLower", "Owner_Priority" })
            {
                Assert.Equal(model.Count, await CountIndexEntriesAsync(database, "tickets", index));
            }

            Assert.Equal(model.Count(static ticket => ticket.IsOpen), await CountIndexEntriesAsync(database, "tickets", "open"));
            Assert.Equal(new TkPlan(TkAccess.IndexScan, "Due"), await tickets.ExplainAsync(Ticket.Fields.Due.IsNull(), Ct));
        }
    }
}

public sealed class BulkInsertTests_File : BulkInsertTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class BulkInsertTests_Memory : BulkInsertTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
