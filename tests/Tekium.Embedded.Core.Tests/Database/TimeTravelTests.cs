// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Tests.Storage;

namespace Tekium.Embedded.Tests.Database;

/// <summary>
/// Viaje en el tiempo: leer la base como estaba en una transacción pasada. Criterio de la Fase 10.6:
/// <c>AsOfAsync</c> devuelve el estado de una transacción anterior, y el horizonte configurado libera las
/// raíces que lo exceden.
/// </summary>
public abstract class TimeTravelTests : DatabaseTests
{
    [Fact]
    public async Task Every_retained_transaction_reads_as_the_database_was_then()
    {
        await using var database = await OpenAsync(NewDevice(), changeRetention: 50);
        var tickets = database.Table<Ticket>();
        var random = new Random(8);
        var model = new Dictionary<long, Ticket>();
        var states = new List<(ulong Transaction, List<Ticket> Rows)> { (database.LastTransactionId, []) };
        for (var round = 0; round < 12; round++)
        {
            await using (var writer = await database.BeginWriteAsync(Ct))
            {
                for (var i = 0; i < 60; i++)
                {
                    var id = random.Next(150);
                    if (random.Next(4) == 0)
                    {
                        await tickets.DeleteAsync(writer, (long)id, Ct);
                        model.Remove(id);
                    }
                    else
                    {
                        model[id] = new Ticket { Id = id, Owner = $"owner{random.Next(9)}", Priority = round, Title = $"Ticket {id}", Body = new string('b', random.Next(10, 300)) };
                        await tickets.UpsertAsync(writer, model[id], Ct);
                    }
                }

                await writer.CommitAsync(Ct);
                Assert.Equal(writer.TransactionId, database.LastTransactionId);
                states.Add((writer.TransactionId, [.. model.Values.OrderBy(static t => t.Id)]));
            }

            // Una transacción que no confirma gasta un número, pero no deja estado.
            await using var abandoned = await database.BeginWriteAsync(Ct);
            await tickets.UpsertAsync(abandoned, new Ticket { Id = 9999, Owner = "nobody" }, Ct);
        }

        var byOwner = Ticket.Fields.Owner.EqualTo("owner3");
        foreach (var (transaction, rows) in states)
        {
            await using var past = await database.AsOfAsync(transaction, Ct);
            Assert.Equal(transaction, past.TransactionId);
            Assert.Equal(rows, await ToListAsync(tickets.ScanAsync(past, Ct)));
            Assert.Equal(
                rows.Where(static t => t.Owner == "owner3").Select(static t => t.Id),
                (await ToListAsync(tickets.QueryAsync(past, byOwner, Ct))).Select(static t => t.Id).Order());
            if (rows.Count > 0)
            {
                Assert.Equal(rows[0], await tickets.GetAsync(past, rows[0].Id, Ct));
            }

            // El número que gastó la transacción que no confirmó, justo después, da el mismo estado.
            if (transaction > states[0].Transaction && transaction < database.LastTransactionId)
            {
                await using var between = await database.AsOfAsync(transaction + 1, Ct);
                Assert.Equal(transaction, between.TransactionId);
                Assert.Equal(rows, await ToListAsync(tickets.ScanAsync(between, Ct)));
            }
        }

        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task What_is_past_the_horizon_or_has_not_happened_is_refused()
    {
        await using var database = await OpenAsync(NewDevice(), changeRetention: 3);
        var commits = new List<ulong>();
        for (var round = 0; round < 10; round++)
        {
            commits.Add(await WriteAsync(database, round));
        }

        // El estado actual y los tres anteriores: lo que el horizonte guarda.
        foreach (var kept in commits[^4..])
        {
            await using var past = await database.AsOfAsync(kept, Ct);
            Assert.Equal(commits.IndexOf(kept), (await database.Table<Counter>().GetAsync(past, 1, Ct))!.Value);
        }

        foreach (var gone in commits[..^4].Append(0ul))
        {
            var failure = await Assert.ThrowsAsync<TkStateUnavailableException>(async () => await database.AsOfAsync(gone, Ct));
            Assert.Contains("3", failure.Message, StringComparison.Ordinal);
        }

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await database.AsOfAsync(database.LastTransactionId + 1, Ct));
    }

    [Fact]
    public async Task Without_a_horizon_only_the_present_can_be_read()
    {
        await using var database = await OpenAsync(NewDevice());
        var first = await WriteAsync(database, 1);
        var second = await WriteAsync(database, 2);

        await using (var now = await database.AsOfAsync(second, Ct))
        {
            Assert.Equal(2, (await database.Table<Counter>().GetAsync(now, 1, Ct))!.Value);
        }

        await Assert.ThrowsAsync<TkStateUnavailableException>(async () => await database.AsOfAsync(first, Ct));
    }

    [Fact]
    public async Task Before_the_first_commit_the_database_was_empty()
    {
        await using var database = await OpenAsync(NewDevice(), changeRetention: 10);
        var created = database.LastTransactionId;
        await WriteAsync(database, 1);
        await WriteAsync(database, 2);

        await using var past = await database.AsOfAsync(created, Ct);

        Assert.Equal(created, past.TransactionId);
        Assert.Null(await database.Table<Counter>().GetAsync(past, 1, Ct));
        Assert.Empty(await ToListAsync(database.Table<Counter>().ScanAsync(past, Ct)));
    }

    [Fact]
    public async Task A_reader_of_the_past_keeps_its_state_however_far_the_writer_goes_and_lets_go_when_it_is_done()
    {
        await using var database = await OpenAsync(NewDevice(), changeRetention: 2);
        await using var control = await OpenAsync(NewDevice(), changeRetention: 2);
        var tickets = database.Table<Ticket>();
        ulong old = 0;
        for (var round = 0; round < 3; round++)
        {
            old = round == 1 ? await RewriteAsync(database, round) : old;
            _ = round == 1 ? 0 : await RewriteAsync(database, round);
            await RewriteAsync(control, round);
        }

        // Dentro del horizonte al abrirlo; después el escritor lo deja muy atrás.
        var past = await database.AsOfAsync(old, Ct);
        for (var round = 3; round < 20; round++)
        {
            await RewriteAsync(database, round);
            await RewriteAsync(control, round);
        }

        var rows = await ToListAsync(tickets.ScanAsync(past, Ct));
        Assert.Equal(200, rows.Count);
        Assert.All(rows, static ticket => Assert.Equal(1, ticket.Priority));
        await Assert.ThrowsAsync<TkStateUnavailableException>(async () => await database.AsOfAsync(old, Ct));
        Assert.True(database.Pager.Current.PageCount > control.Pager.Current.PageCount, "Holding a past state cost no space: was it held?");

        // Soltado (dos veces no hace daño), ya no se puede leer por él, y su espacio vuelve a usarse.
        await past.DisposeAsync();
        await past.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await ToListAsync(tickets.ScanAsync(past, Ct)));
        var size = database.Pager.Current.PageCount;
        for (var round = 20; round < 40; round++)
        {
            await RewriteAsync(database, round);
        }

        Assert.Equal(size, database.Pager.Current.PageCount);
        await AssertNoLeaksAsync(database);
    }

    /// <summary>La segunda mitad del criterio: lo que queda fuera del horizonte se libera.</summary>
    [Fact]
    public async Task The_horizon_bounds_the_space_and_a_longer_one_costs_more()
    {
        await using var none = await OpenAsync(NewDevice());
        await using var three = await OpenAsync(NewDevice(), changeRetention: 3);
        await using var twelve = await OpenAsync(NewDevice(), changeRetention: 12);
        var after = new Dictionary<TekiumDb, uint>();
        foreach (var database in new[] { none, three, twelve })
        {
            for (var round = 0; round < 30; round++)
            {
                await RewriteAsync(database, round);
            }

            after[database] = database.Pager.Current.PageCount;
            for (var round = 30; round < 60; round++)
            {
                await RewriteAsync(database, round);
            }

            // Treinta reescrituras completas más y el archivo no creció: las raíces viejas se fueron soltando.
            Assert.Equal(after[database], database.Pager.Current.PageCount);
            await AssertNoLeaksAsync(database);
        }

        Assert.True(after[none] < after[three] && after[three] < after[twelve], $"{after[none]}, {after[three]} and {after[twelve]} pages.");
    }

    [Fact]
    public async Task The_past_is_still_there_after_closing_and_opening_the_database()
    {
        var device = NewDevice();
        var commits = new List<ulong>();
        await using (var database = await OpenAsync(device, changeRetention: 5))
        {
            for (var round = 0; round < 4; round++)
            {
                commits.Add(await WriteAsync(database, round));
            }
        }

        await using var reopened = await OpenAsync(device, changeRetention: 5);
        for (var round = 0; round < 4; round++)
        {
            await using var past = await reopened.AsOfAsync(commits[round], Ct);
            Assert.Equal(round, (await reopened.Table<Counter>().GetAsync(past, 1, Ct))!.Value);
        }
    }

    private static async Task<ulong> WriteAsync(TekiumDb database, int value)
    {
        await using var writer = await database.BeginWriteAsync(Ct);
        await database.Table<Counter>().UpsertAsync(writer, new Counter { Id = 1, Value = value }, Ct);
        await writer.CommitAsync(Ct);
        return writer.TransactionId;
    }

    /// <summary>Reescribe doscientas filas con la ronda en <c>Priority</c>. Devuelve la transacción.</summary>
    private static async Task<ulong> RewriteAsync(TekiumDb database, int round)
    {
        await using var writer = await database.BeginWriteAsync(Ct);
        for (var i = 0; i < 200; i++)
        {
            await database.Table<Ticket>().UpsertAsync(writer, new Ticket { Id = i, Owner = $"owner{i % 9}", Priority = round, Title = $"Ticket {i}", Body = new string('b', 120) }, Ct);
        }

        await writer.CommitAsync(Ct);
        return writer.TransactionId;
    }
}

public sealed class TimeTravelTests_File : TimeTravelTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class TimeTravelTests_Memory : TimeTravelTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
