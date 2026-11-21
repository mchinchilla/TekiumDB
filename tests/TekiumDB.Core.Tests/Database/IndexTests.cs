// Copyright (c) Tekium. Licensed under the MIT License.

using TekiumDB.Catalog;
using TekiumDB.Indexing;
using TekiumDB.Storage;
using TekiumDB.Tests.Indexing;
using TekiumDB.Tests.Storage;

namespace TekiumDB.Tests.Database;

/// <summary>
/// Índices secundarios: se mantienen con cada escritura, y una consulta por ellos devuelve lo mismo que
/// filtrar la tabla entera. El modelo de referencia es una lista en memoria.
/// </summary>
public abstract class IndexTests : DatabaseTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] Owners = ["ana", "luis", "eva", "ana maria", string.Empty, "ñu", "an\0a"];

    /// <summary>
    /// Criterio de salida de la Fase 6: un índice sobre expresión y uno parcial se mantienen al insertar
    /// (y al actualizar y borrar), igual que los demás.
    /// </summary>
    [Fact]
    public async Task Every_index_follows_the_table_through_inserts_updates_deletes_and_rollbacks()
    {
        var device = NewDevice();
        var random = new Random(6);
        var model = new SortedDictionary<long, Ticket>();
        await using (var database = await OpenAsync(device))
        {
            var tickets = database.Table<Ticket>();
            for (var round = 0; round < 12; round++)
            {
                var commit = round % 4 != 3;
                var changed = new SortedDictionary<long, Ticket>(model);
                await using (var writer = await database.BeginWriteAsync(Ct))
                {
                    for (var i = 0; i < 150; i++)
                    {
                        var ticket = NewTicket(random, random.Next(400));
                        switch (random.Next(5))
                        {
                            case 0:
                                Assert.Equal(changed.Remove(ticket.Id), await tickets.DeleteAsync(writer, ticket.Id, Ct));
                                break;
                            case 1:
                                Assert.Equal(changed.ContainsKey(ticket.Id), await tickets.UpdateAsync(writer, ticket, Ct));
                                if (changed.ContainsKey(ticket.Id))
                                {
                                    changed[ticket.Id] = ticket;
                                }

                                break;
                            default:
                                await tickets.UpsertAsync(writer, ticket, Ct);
                                changed[ticket.Id] = ticket;
                                break;
                        }
                    }

                    // Dentro de la transacción los índices ya van al día.
                    await AssertQueriesMatchAsync(tickets, writer, changed.Values);
                    if (commit)
                    {
                        await writer.CommitAsync(Ct);
                        model = changed;
                    }
                }

                await AssertQueriesMatchAsync(tickets, null, model.Values);
                await AssertIndexSizesAsync(database, model.Values);
                await AssertNoLeaksAsync(database);
            }
        }

        await using var reopened = await OpenAsync(device);
        await AssertQueriesMatchAsync(reopened.Table<Ticket>(), null, model.Values);
        await AssertIndexSizesAsync(reopened, model.Values);
    }

    [Fact]
    public async Task A_query_uses_the_index_that_fits_its_conditions()
    {
        await using var database = await OpenAsync(NewDevice());
        var tickets = database.Table<Ticket>();
        await FillAsync(database, 300);

        Assert.Equal(new TkPlan(TkAccess.KeyLookup, null), await tickets.ExplainAsync(Ticket.Fields.Id.EqualTo(7), Ct));
        Assert.Equal(new TkPlan(TkAccess.KeyRange, null), await tickets.ExplainAsync(Ticket.Fields.Id.AtLeast(7), Ct));
        Assert.Equal(new TkPlan(TkAccess.TableScan, null), await tickets.ExplainAsync(Ticket.Fields.Cost.AtLeast(7), Ct));
        Assert.Equal(new TkPlan(TkAccess.IndexScan, "Due"), await tickets.ExplainAsync(Ticket.Fields.Due.LessThan(Epoch), Ct));
        Assert.Equal(new TkPlan(TkAccess.IndexScan, "Due"), await tickets.ExplainAsync(Ticket.Fields.Due.IsNull(), Ct));
        Assert.Equal(new TkPlan(TkAccess.IndexScan, "TitleLower"), await tickets.ExplainAsync(Ticket.Fields.TitleLower.EqualTo("x"), Ct));

        // Con el dueño fijo y un rango de prioridad, el índice de las dos columnas acota más que el de una.
        Assert.Equal(
            new TkPlan(TkAccess.IndexScan, "Owner_Priority"),
            await tickets.ExplainAsync(Ticket.Fields.Owner.EqualTo("ana").And(Ticket.Fields.Priority.AtLeast(3)), Ct));

        // La prioridad sola no es el principio de ningún índice completo.
        Assert.Equal(new TkPlan(TkAccess.TableScan, null), await tickets.ExplainAsync(Ticket.Fields.Priority.EqualTo(3), Ct));

        // Una clave exacta gana a cualquier índice, y condiciones que se contradicen no leen nada.
        Assert.Equal(
            new TkPlan(TkAccess.KeyLookup, null),
            await tickets.ExplainAsync(Ticket.Fields.Owner.EqualTo("ana").And(Ticket.Fields.Id.EqualTo(3)), Ct));
        Assert.Equal(
            new TkPlan(TkAccess.Nothing, null),
            await tickets.ExplainAsync(Ticket.Fields.Priority.LessThan(3).And(Ticket.Fields.Priority.GreaterThan(3)), Ct));
        Assert.Empty(await ToListAsync(tickets.QueryAsync(Ticket.Fields.Priority.LessThan(3).And(Ticket.Fields.Priority.GreaterThan(3)), Ct)));
    }

    /// <summary>
    /// Criterio de salida de la Fase 6: el planificador sólo usa el índice parcial cuando puede probar que
    /// el predicado de la consulta lo implica. Si no, lo ignora: nunca devuelve resultados incompletos.
    /// </summary>
    [Fact]
    public async Task A_partial_index_is_used_only_when_the_query_implies_its_condition()
    {
        await using var database = await OpenAsync(NewDevice());
        var tickets = database.Table<Ticket>();
        var all = await FillAsync(database, 400);
        var open = Ticket.Fields.Deleted.EqualTo(false).And(Ticket.Fields.State.EqualTo(TicketState.Open));
        var partial = new TkPlan(TkAccess.IndexScan, "open");

        // Lo implica tal cual, en otro orden, con más condiciones, y con un rango dentro del suyo.
        Assert.Equal(partial, await tickets.ExplainAsync(open, Ct));
        Assert.Equal(partial, await tickets.ExplainAsync(open.And(Ticket.Fields.Priority.AtLeast(2)), Ct));
        Assert.Equal(
            partial,
            await tickets.ExplainAsync(
                Ticket.Fields.Priority.EqualTo(2).And(Ticket.Fields.State.EqualTo(TicketState.Open)).And(Ticket.Fields.Deleted.EqualTo(false)), Ct));
        Assert.Equal(
            partial,
            await tickets.ExplainAsync(
                Ticket.Fields.State.Between(TicketState.Open, TicketState.Open).And(Ticket.Fields.Deleted.Between(false, false)).And(Ticket.Fields.Cost.AtLeast(1)), Ct));

        // No lo implica: falta una mitad, o la condición es más ancha, o es otra.
        var notImplied = new[]
        {
            Ticket.Fields.Priority.EqualTo(2),
            Ticket.Fields.State.EqualTo(TicketState.Open).And(Ticket.Fields.Priority.EqualTo(2)),
            Ticket.Fields.Deleted.EqualTo(false).And(Ticket.Fields.Priority.EqualTo(2)),
            Ticket.Fields.Deleted.EqualTo(false).And(Ticket.Fields.State.AtMost(TicketState.Closed)).And(Ticket.Fields.Priority.EqualTo(2)),
            Ticket.Fields.Deleted.EqualTo(false).And(Ticket.Fields.State.EqualTo(TicketState.Closed)).And(Ticket.Fields.Priority.EqualTo(2)),
            Ticket.Fields.Deleted.EqualTo(true).And(Ticket.Fields.State.EqualTo(TicketState.Open)).And(Ticket.Fields.Priority.EqualTo(2)),
            Ticket.Fields.Deleted.AtMost(true).And(Ticket.Fields.State.EqualTo(TicketState.Open)).And(Ticket.Fields.Priority.EqualTo(2)),
        };
        foreach (var filter in notImplied)
        {
            Assert.NotEqual(partial, await tickets.ExplainAsync(filter, Ct));
        }

        // Con el índice o sin él, las filas son las mismas.
        Assert.Equal(
            all.Where(static ticket => ticket is { Deleted: false, State: TicketState.Open, Priority: >= 2 }).Select(static ticket => ticket.Id).Order(),
            (await ToListAsync(tickets.QueryAsync(open.And(Ticket.Fields.Priority.AtLeast(2)), Ct))).Select(static ticket => ticket.Id).Order());
        Assert.Equal(
            all.Where(static ticket => ticket is { State: TicketState.Open, Priority: 2 }).Select(static ticket => ticket.Id).Order(),
            (await ToListAsync(tickets.QueryAsync(notImplied[1], Ct))).Select(static ticket => ticket.Id).Order());

        // El índice parcial sólo guarda las filas que cumplen su condición.
        Assert.Equal(all.Count(static ticket => ticket.IsOpen), await CountIndexEntriesAsync(database, "tickets", "open"));
        Assert.True(all.Count(static ticket => ticket.IsOpen) < all.Count / 2);
    }

    [Fact]
    public async Task Rows_come_back_in_the_order_of_the_index()
    {
        await using var database = await OpenAsync(NewDevice());
        var tickets = database.Table<Ticket>();
        var all = await FillAsync(database, 300);

        var byDue = await ToListAsync(tickets.QueryAsync(Ticket.Fields.Due.IsNotNull(), Ct));
        Assert.Equal(
            all.Where(static ticket => ticket.Due is not null).OrderBy(static ticket => ticket.Due).ThenBy(static ticket => ticket.Id).Select(static ticket => ticket.Id),
            byDue.Select(static ticket => ticket.Id));

        var byPriority = await ToListAsync(tickets.QueryAsync(Ticket.Fields.Owner.EqualTo("ana"), Ct));
        Assert.Equal(
            all.Where(static ticket => ticket.Owner == "ana").OrderBy(static ticket => ticket.Priority).ThenBy(static ticket => ticket.Id).Select(static ticket => ticket.Id),
            byPriority.Select(static ticket => ticket.Id));
    }

    [Fact]
    public async Task A_key_of_text_can_be_filtered_by_range()
    {
        await using var database = await OpenAsync(NewDevice());
        var notes = database.Table<Note>();
        var slugs = new[] { "a", "a\0", "a\0b", "ab", "b", "c", "ñ", "z" };
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            foreach (var slug in slugs)
            {
                await notes.InsertAsync(writer, new Note { Slug = slug, Body = slug }, Ct);
            }

            await writer.CommitAsync(Ct);
        }

        Assert.Equal(new TkPlan(TkAccess.KeyRange, null), await notes.ExplainAsync(Note.Fields.Slug.Between("a\0", "b"), Ct));
        Assert.Equal(new TkPlan(TkAccess.KeyLookup, null), await notes.ExplainAsync(Note.Fields.Slug.EqualTo("a\0"), Ct));
        Assert.Equal(["a\0", "a\0b", "ab", "b"], await SlugsAsync(Note.Fields.Slug.Between("a\0", "b")));
        Assert.Equal(["a\0b", "ab"], await SlugsAsync(Note.Fields.Slug.GreaterThan("a\0").And(Note.Fields.Slug.LessThan("b"))));
        Assert.Equal(["a\0"], await SlugsAsync(Note.Fields.Slug.EqualTo("a\0")));
        Assert.Equal(["a"], await SlugsAsync(Note.Fields.Slug.AtMost("a")));
        Assert.Equal(["ñ"], await SlugsAsync(Note.Fields.Slug.GreaterThan("z")));
        Assert.Empty(await SlugsAsync(Note.Fields.Slug.EqualTo(string.Empty)));
        Assert.Empty(await SlugsAsync(Note.Fields.Slug.LessThan(string.Empty)));
        Assert.Empty(await SlugsAsync(Note.Fields.Slug.EqualTo("missing")));

        async Task<List<string>> SlugsAsync(TkFilter<Note> filter) =>
            (await ToListAsync(notes.QueryAsync(filter, Ct))).ConvertAll(static note => note.Slug);
    }

    [Fact]
    public async Task An_index_added_to_a_table_is_built_by_the_first_write_and_ignored_until_then()
    {
        var device = NewDevice();
        await using (var database = await OpenAsync(device))
        {
            await using var writer = await database.BeginWriteAsync(Ct);
            for (var i = 0; i < 500; i++)
            {
                await database.Table<GadgetPlain>().InsertAsync(writer, new GadgetPlain { Id = i, Name = $"g{i % 50}", Size = i }, Ct);
            }

            await writer.CommitAsync(Ct);
        }

        await using var reopened = await OpenAsync(device);
        var gadgets = reopened.Table<GadgetIndexed>();
        var writes = device.WriteCount;

        // El índice todavía no existe: la consulta recorre la tabla, y leer no lo construye.
        Assert.Equal(new TkPlan(TkAccess.TableScan, null), await gadgets.ExplainAsync(GadgetIndexed.Fields.Bucket.EqualTo(7), Ct));
        Assert.Equal(Enumerable.Range(70, 10), (await ToListAsync(gadgets.QueryAsync(GadgetIndexed.Fields.Bucket.EqualTo(7), Ct))).Select(static gadget => gadget.Id));
        Assert.Equal(writes, device.WriteCount);

        await using (var writer = await reopened.BeginWriteAsync(Ct))
        {
            await gadgets.InsertAsync(writer, new GadgetIndexed { Id = 1000, Name = "g7", Size = 75 }, Ct);

            // Ya dentro de la transacción que lo construyó, se usa.
            Assert.Equal(
                [.. Enumerable.Range(70, 10), 1000],
                (await ToListAsync(gadgets.QueryAsync(writer, GadgetIndexed.Fields.Bucket.EqualTo(7), Ct))).Select(static gadget => gadget.Id));
            await writer.CommitAsync(Ct);
        }

        Assert.Equal(new TkPlan(TkAccess.IndexScan, "Bucket"), await gadgets.ExplainAsync(GadgetIndexed.Fields.Bucket.EqualTo(7), Ct));
        Assert.Equal(new TkPlan(TkAccess.IndexScan, "Name"), await gadgets.ExplainAsync(GadgetIndexed.Fields.Name.EqualTo("g7"), Ct));
        Assert.Equal(501, await CountIndexEntriesAsync(reopened, "gadgets", "Bucket"));
        Assert.Equal(501, await CountIndexEntriesAsync(reopened, "gadgets", "Name"));
        Assert.Equal(11, (await ToListAsync(gadgets.QueryAsync(GadgetIndexed.Fields.Name.EqualTo("g7"), Ct))).Count);
        await AssertNoLeaksAsync(reopened);
    }

    [Fact]
    public async Task An_index_whose_expression_changed_is_rebuilt_and_one_no_longer_declared_is_dropped()
    {
        var device = NewDevice();
        await using var database = await OpenAsync(device);
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            for (var i = 0; i < 500; i++)
            {
                await database.Table<GadgetIndexed>().InsertAsync(writer, new GadgetIndexed { Id = i, Name = $"g{i % 50}", Size = i }, Ct);
            }

            await writer.CommitAsync(Ct);
        }

        // Quien declara otra expresión no puede usar el índice que hay: daría filas equivocadas.
        var reindexed = database.Table<GadgetReindexed>();
        Assert.Equal(new TkPlan(TkAccess.TableScan, null), await reindexed.ExplainAsync(GadgetReindexed.Fields.Bucket.EqualTo(3), Ct));
        Assert.Equal(new TkPlan(TkAccess.IndexScan, "Name"), await reindexed.ExplainAsync(GadgetReindexed.Fields.Name.EqualTo("g3"), Ct));
        Assert.Equal(100, (await ToListAsync(reindexed.QueryAsync(GadgetReindexed.Fields.Bucket.EqualTo(3), Ct))).Count);

        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            // Tampoco dentro de la transacción de escritura, mientras no lo haya reconstruido.
            Assert.Equal(100, (await ToListAsync(reindexed.QueryAsync(writer, GadgetReindexed.Fields.Bucket.EqualTo(3), Ct))).Count);
            Assert.True(await reindexed.DeleteAsync(writer, 399, Ct));
            await writer.CommitAsync(Ct);
        }

        Assert.Equal(new TkPlan(TkAccess.IndexScan, "Bucket"), await reindexed.ExplainAsync(GadgetReindexed.Fields.Bucket.EqualTo(3), Ct));
        Assert.Equal(Enumerable.Range(300, 99), (await ToListAsync(reindexed.QueryAsync(GadgetReindexed.Fields.Bucket.EqualTo(3), Ct))).Select(static gadget => gadget.Id));
        Assert.Equal(new TkPlan(TkAccess.TableScan, null), await database.Table<GadgetIndexed>().ExplainAsync(GadgetIndexed.Fields.Bucket.EqualTo(3), Ct));
        await AssertNoLeaksAsync(database);

        // Quien no declara índices los quita al escribir, y sus páginas vuelven a la free-list.
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await database.Table<GadgetPlain>().UpsertAsync(writer, new GadgetPlain { Id = 1, Name = "renamed", Size = 1 }, Ct);
            await writer.CommitAsync(Ct);
        }

        Assert.Equal(-1, await CountIndexEntriesAsync(database, "gadgets", "Name"));
        Assert.Equal(-1, await CountIndexEntriesAsync(database, "gadgets", "Bucket"));
        Assert.Equal(new TkPlan(TkAccess.TableScan, null), await reindexed.ExplainAsync(GadgetReindexed.Fields.Name.EqualTo("renamed"), Ct));
        Assert.Equal([1], (await ToListAsync(reindexed.QueryAsync(GadgetReindexed.Fields.Name.EqualTo("renamed"), Ct))).Select(static gadget => gadget.Id));
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task A_reader_keeps_the_index_of_its_snapshot_and_a_rollback_leaves_none()
    {
        await using var database = await OpenAsync(NewDevice());
        var tickets = database.Table<Ticket>();
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await tickets.InsertAsync(writer, new Ticket { Id = 1, Owner = "ana" }, Ct);
        }

        Assert.Equal(-1, await CountIndexEntriesAsync(database, "tickets", "Owner"));
        Assert.Empty(await ToListAsync(tickets.QueryAsync(Ticket.Fields.Owner.EqualTo("ana"), Ct)));

        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await tickets.InsertAsync(writer, new Ticket { Id = 1, Owner = "ana" }, Ct);
            await writer.CommitAsync(Ct);
        }

        await using var reader = database.BeginRead();
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await tickets.UpsertAsync(writer, new Ticket { Id = 1, Owner = "luis" }, Ct);
            await tickets.InsertAsync(writer, new Ticket { Id = 2, Owner = "ana" }, Ct);
            await writer.CommitAsync(Ct);
        }

        Assert.Equal([1L], (await ToListAsync(tickets.QueryAsync(reader, Ticket.Fields.Owner.EqualTo("ana"), Ct))).Select(static ticket => ticket.Id));
        Assert.Equal([2L], (await ToListAsync(tickets.QueryAsync(Ticket.Fields.Owner.EqualTo("ana"), Ct))).Select(static ticket => ticket.Id));
        Assert.Equal([1L], (await ToListAsync(tickets.QueryAsync(Ticket.Fields.Owner.EqualTo("luis"), Ct))).Select(static ticket => ticket.Id));
    }

    [Fact]
    public async Task A_row_whose_index_key_is_too_long_is_refused_and_changes_nothing()
    {
        await using var database = await OpenAsync(NewDevice());
        var tickets = database.Table<Ticket>();
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await tickets.InsertAsync(writer, new Ticket { Id = 1, Owner = "ana", Title = "short" }, Ct);

            var refused = await Assert.ThrowsAsync<TkIndexKeyTooLargeException>(
                async () => await tickets.UpsertAsync(writer, new Ticket { Id = 1, Owner = "luis", Title = new string('T', 300) }, Ct));
            Assert.Contains("'TitleLower'", refused.Message, StringComparison.Ordinal);
            Assert.Contains("'tickets'", refused.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<TkIndexKeyTooLargeException>(
                async () => await tickets.InsertAsync(writer, new Ticket { Id = 2, Owner = new string('o', 300) }, Ct));

            // La transacción sigue siendo buena, y la fila y sus índices están como antes.
            await writer.CommitAsync(Ct);
        }

        Assert.Equal("ana", (await tickets.GetAsync(1L, Ct))!.Owner);
        Assert.Single(await ToListAsync(tickets.QueryAsync(Ticket.Fields.Owner.EqualTo("ana"), Ct)));
        Assert.Empty(await ToListAsync(tickets.QueryAsync(Ticket.Fields.Owner.EqualTo("luis"), Ct)));
        Assert.Equal(1, await CountIndexEntriesAsync(database, "tickets", "TitleLower"));

        // Lo que no se indexa puede ser tan largo como se quiera.
        await using var second = await database.BeginWriteAsync(Ct);
        await tickets.InsertAsync(second, new Ticket { Id = 3, Owner = "eva", Body = new string('b', 50_000) }, Ct);
        await second.CommitAsync(Ct);
        Assert.Equal(50_000, (await ToListAsync(tickets.QueryAsync(Ticket.Fields.Owner.EqualTo("eva"), Ct))).Single().Body.Length);
    }

    /// <summary>
    /// Una fila y sus índices son varios árboles. Si la operación falla entre uno y otro, la transacción
    /// ya no puede confirmarse: sólo deshacerse.
    /// </summary>
    [Fact]
    public async Task An_operation_that_fails_halfway_leaves_a_transaction_that_can_only_be_rolled_back()
    {
        var device = NewDevice();
        await using (var database = await OpenAsync(device))
        {
            await FillAsync(database, 600);
        }

        var failures = 0;
        var poisoned = 0;
        for (var failAt = 1; failAt <= 40; failAt++)
        {
            await using var database = await OpenAsync(device);
            var tickets = database.Table<Ticket>();
            await using (var writer = await database.BeginWriteAsync(Ct))
            {
                device.FailOnReadNumber = device.ReadCount + failAt;
                try
                {
                    await tickets.UpsertAsync(writer, new Ticket { Id = 300, Owner = "changed", Priority = 77, Title = "Changed" }, Ct);
                    await tickets.DeleteAsync(writer, 301L, Ct);
                    device.FailOnReadNumber = null;
                    await writer.CommitAsync(Ct);
                }
                catch (IOException)
                {
                    device.FailOnReadNumber = null;
                    failures++;
                    try
                    {
                        // Si falló antes de tocar ningún árbol, la transacción sigue entera y puede confirmarse.
                        await writer.CommitAsync(Ct);
                    }
                    catch (InvalidOperationException refused)
                    {
                        poisoned++;
                        Assert.Contains("rolled back", refused.Message, StringComparison.Ordinal);
                    }
                }
            }

            // Sea cual sea el punto del fallo, la tabla y sus índices dicen lo mismo.
            var rows = await ToListAsync(tickets.ScanAsync(Ct));
            await AssertQueriesMatchAsync(tickets, null, rows);
            await AssertIndexSizesAsync(database, rows);
            await AssertNoLeaksAsync(database);
        }

        Assert.True(failures > 5, $"Only {failures} of the injected failures hit the operation.");
        Assert.True(poisoned > 2, $"Only {poisoned} of the injected failures hit an operation halfway.");
    }

    /// <summary>
    /// La fila y las entradas de sus índices entran al disco con el mismo cambio de raíz. Cortando la
    /// energía antes de cada operación del commit, lo que abre después es el estado anterior o el nuevo,
    /// y en cualquiera de los dos la tabla y sus índices dicen lo mismo.
    /// </summary>
    [Fact]
    public async Task Losing_power_at_any_point_leaves_the_table_and_its_indexes_in_agreement()
    {
        const int Rounds = 8;

        // Lo que la tabla tiene después de cada commit, calculado aparte.
        var states = new List<List<Ticket>>();
        var model = new SortedDictionary<long, Ticket>();
        var plan = new Random(9);
        for (var round = 0; round < Rounds; round++)
        {
            Change(plan, (id, ticket) => _ = ticket is null ? model.Remove(id) : model.TryAdd(id, ticket) || (model[id] = ticket) is not null);
            states.Add([.. model.Values]);
        }

        var complete = NewDevice();
        Assert.Equal(Rounds - 1, await RunAsync(complete, int.MaxValue));
        var first = await RunAsync(NewDevice(), 1, static device => device.OperationCount);
        var operations = complete.OperationCount - first;
        Assert.True(operations > 20, "The workload is too small to say anything about the commit.");

        var random = new Random(4);
        for (var at = 1; at <= operations; at++)
        {
            var device = NewDevice();
            var acknowledged = await RunAsync(device, int.MaxValue, lossAfterFirstRound: at);
            Assert.True(device.PowerLost);

            foreach (var seed in new[] { 0, 1, random.Next(), random.Next() })
            {
                var image = await device.PowerLossImageAsync(
                    (write, sector) => seed == 1 || (seed != 0 && HashCode.Combine(seed, write, sector) % 2 == 0));
                try
                {
                    await using var recovered = await OpenAsync(image);
                    var tickets = recovered.Table<Ticket>();
                    var rows = await ToListAsync(tickets.ScanAsync(Ct));
                    Assert.True(
                        rows.SequenceEqual(states[acknowledged]) || (acknowledged + 1 < Rounds && rows.SequenceEqual(states[acknowledged + 1])),
                        $"Power lost at operation {at} of {operations} (image {seed}): the table is in neither the acknowledged state nor the next.");
                    await AssertQueriesMatchAsync(tickets, null, rows);
                    await AssertIndexSizesAsync(recovered, rows);
                    await AssertNoLeaksAsync(recovered);
                }
                finally
                {
                    await image.DestroyAsync();
                }
            }
        }

        // Corre la carga y devuelve el último commit confirmado. La energía se corta, si se pide, contando
        // operaciones desde que el primer commit ya está en el disco.
        async Task<int> RunAsync(TestDevice device, int rounds, Func<TestDevice, int>? result = null, int? lossAfterFirstRound = null)
        {
            var acknowledged = -1;
            var changes = new Random(9);
            try
            {
                await using var database = await OpenAsync(device);
                var tickets = database.Table<Ticket>();
                for (var round = 0; round < Math.Min(rounds, Rounds); round++)
                {
                    await using var writer = await database.BeginWriteAsync(Ct);
                    var pending = new List<(long Id, Ticket? Ticket)>();
                    Change(changes, (id, ticket) => pending.Add((id, ticket)));
                    foreach (var (id, ticket) in pending)
                    {
                        if (ticket is null)
                        {
                            await tickets.DeleteAsync(writer, id, Ct);
                        }
                        else
                        {
                            await tickets.UpsertAsync(writer, ticket, Ct);
                        }
                    }

                    await writer.CommitAsync(Ct);
                    acknowledged = round;
                    if (round == 0 && lossAfterFirstRound is { } at)
                    {
                        device.TrackUnflushedWrites = true;
                        device.PowerLossAtOperation = device.OperationCount + at;
                    }
                }
            }
            catch (IOException) when (device.PowerLost)
            {
            }

            return result?.Invoke(device) ?? acknowledged;
        }

        static void Change(Random random, Action<long, Ticket?> apply)
        {
            for (var i = 0; i < 60; i++)
            {
                var ticket = NewTicket(random, random.Next(80));
                apply(ticket.Id, random.Next(4) == 0 ? null : ticket);
            }
        }
    }

    [Fact]
    public async Task A_filter_is_required_and_belongs_to_the_database_of_its_transaction()
    {
        await using var database = await OpenAsync(NewDevice());
        await using var other = await OpenAsync(NewDevice());
        var tickets = database.Table<Ticket>();
        await using var foreign = other.BeginRead();

        Assert.Throws<ArgumentNullException>(() => tickets.QueryAsync(null!, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await tickets.ExplainAsync(null!, Ct));
        Assert.Throws<ArgumentException>(() => tickets.QueryAsync(foreign, Ticket.Fields.Id.EqualTo(1), Ct));
        Assert.Throws<ArgumentNullException>(() => Ticket.Fields.Owner.EqualTo(null!));
        Assert.Throws<ArgumentNullException>(() => Ticket.Fields.Owner.EqualTo("a").And(null!));

        // Una tabla sin escribir se consulta como vacía.
        Assert.Empty(await ToListAsync(tickets.QueryAsync(Ticket.Fields.Owner.EqualTo("ana"), Ct)));
    }

    private static Ticket NewTicket(Random random, long id) => new()
    {
        Id = id,
        Owner = Owners[random.Next(Owners.Length)],
        Priority = random.Next(-2, 6),
        Due = random.Next(4) == 0 ? null : Epoch.AddHours(random.Next(-200, 200)).ToOffset(TimeSpan.FromHours(random.Next(-5, 6))),
        State = (TicketState)new[] { 0, 0, 1, 9 }[random.Next(4)],
        Deleted = random.Next(3) == 0,
        Cost = random.Next(-300, 300) / 10m,
        Title = $"Title {(char)('A' + random.Next(26))}{random.Next(20)}",
        Body = random.Next(50) == 0 ? new string('x', 6000) : string.Empty,
    };

    private static async Task<List<Ticket>> FillAsync(TekiumDb database, int count)
    {
        var random = new Random(count);
        var all = new List<Ticket>();
        await using var writer = await database.BeginWriteAsync(Ct);
        for (var i = 0; i < count; i++)
        {
            all.Add(NewTicket(random, i));
            await database.Table<Ticket>().InsertAsync(writer, all[^1], Ct);
        }

        await writer.CommitAsync(Ct);
        return all;
    }

    /// <summary>Cada consulta, por el camino que el planificador elija, devuelve lo que filtrar el modelo.</summary>
    private static async Task AssertQueriesMatchAsync(Table<Ticket> tickets, TkTransaction? transaction, IEnumerable<Ticket> model)
    {
        var rows = model.ToList();
        var from = Epoch.AddHours(-50);
        var to = Epoch.AddHours(80);
        var queries = new (TkFilter<Ticket> Filter, Func<Ticket, bool> Expected)[]
        {
            (Ticket.Fields.Owner.EqualTo("ana"), static ticket => ticket.Owner == "ana"),
            (Ticket.Fields.Owner.EqualTo(string.Empty), static ticket => ticket.Owner.Length == 0),
            (Ticket.Fields.Owner.EqualTo("an\0a"), static ticket => ticket.Owner == "an\0a"),
            (Ticket.Fields.Owner.GreaterThan("ana"), static ticket => string.CompareOrdinal(ticket.Owner, "ana") > 0),
            (Ticket.Fields.Owner.EqualTo("eva").And(Ticket.Fields.Priority.Between(0, 3)), static ticket => ticket is { Owner: "eva", Priority: >= 0 and <= 3 }),
            (Ticket.Fields.Owner.EqualTo("eva").And(Ticket.Fields.Priority.LessThan(0)), static ticket => ticket is { Owner: "eva", Priority: < 0 }),
            (Ticket.Fields.Owner.EqualTo("luis").And(Ticket.Fields.Priority.EqualTo(5)).And(Ticket.Fields.Cost.GreaterThan(0)), static ticket => ticket is { Owner: "luis", Priority: 5, Cost: > 0 }),
            (Ticket.Fields.Due.Between(from, to), ticket => ticket.Due >= from && ticket.Due <= to),
            (Ticket.Fields.Due.LessThan(from), ticket => ticket.Due < from),
            (Ticket.Fields.Due.GreaterThan(to), ticket => ticket.Due > to),
            (Ticket.Fields.Due.AtLeast(to).And(Ticket.Fields.Due.AtMost(to)), ticket => ticket.Due == to),
            (Ticket.Fields.Due.IsNull(), static ticket => ticket.Due is null),
            (Ticket.Fields.Due.IsNotNull().And(Ticket.Fields.Deleted.EqualTo(true)), static ticket => ticket.Due is not null && ticket.Deleted),
            (Ticket.Fields.TitleLower.EqualTo("title a1"), static ticket => ticket.Title == "Title A1"),
            (Ticket.Fields.TitleLower.Between("title b", "title c"), static ticket => string.CompareOrdinal(ticket.TitleLower, "title b") >= 0 && string.CompareOrdinal(ticket.TitleLower, "title c") <= 0),
            (Ticket.Fields.Deleted.EqualTo(false).And(Ticket.Fields.State.EqualTo(TicketState.Open)), static ticket => ticket.IsOpen),
            (Ticket.Fields.Deleted.EqualTo(false).And(Ticket.Fields.State.EqualTo(TicketState.Open)).And(Ticket.Fields.Priority.GreaterThan(3)), static ticket => ticket.IsOpen && ticket.Priority > 3),
            (Ticket.Fields.State.EqualTo(TicketState.Archived), static ticket => ticket.State == TicketState.Archived),
            (Ticket.Fields.Cost.Between(-1.5m, 2.50m), static ticket => ticket.Cost is >= -1.5m and <= 2.5m),
            (Ticket.Fields.Id.Between(100, 120), static ticket => ticket.Id is >= 100 and <= 120),
            (Ticket.Fields.Id.GreaterThan(380).And(Ticket.Fields.Owner.EqualTo("ana")), static ticket => ticket.Id > 380 && ticket.Owner == "ana"),
            (Ticket.Fields.Id.EqualTo(42), static ticket => ticket.Id == 42),
            (Ticket.Fields.Id.EqualTo(42).And(Ticket.Fields.Deleted.EqualTo(true)), static ticket => ticket.Id == 42 && ticket.Deleted),
        };

        foreach (var (filter, expected) in queries)
        {
            var found = transaction is null
                ? await ToListAsync(tickets.QueryAsync(filter, Ct))
                : await ToListAsync(tickets.QueryAsync(transaction, filter, Ct));
            Assert.Equal(rows.Where(expected).OrderBy(static ticket => ticket.Id), found.OrderBy(static ticket => ticket.Id));
        }
    }

    /// <summary>Ni entradas de más ni de menos: cada índice tiene una por fila, o por fila que cumple su condición.</summary>
    private static async Task AssertIndexSizesAsync(TekiumDb database, IEnumerable<Ticket> model)
    {
        var rows = model.ToList();
        foreach (var index in new[] { "Owner", "Due", "TitleLower", "Owner_Priority" })
        {
            Assert.Equal(rows.Count, await CountIndexEntriesAsync(database, "tickets", index));
        }

        Assert.Equal(rows.Count(static ticket => ticket.IsOpen), await CountIndexEntriesAsync(database, "tickets", "open"));
    }
}

public sealed class IndexTests_File : IndexTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class IndexTests_Memory : IndexTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
