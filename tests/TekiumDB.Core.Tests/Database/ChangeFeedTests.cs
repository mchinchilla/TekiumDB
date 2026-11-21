// Copyright (c) Tekium. Licensed under the MIT License.

using TekiumDB.Tests.Storage;

namespace TekiumDB.Tests.Database;

/// <summary>
/// El change feed: lo que cada commit le hizo a las filas de una tabla, leído después del commit y fuera
/// del escritor. Criterio de la Fase 6.7: un consumidor reanuda desde un id de transacción sin perder cambios.
/// </summary>
public abstract class ChangeFeedTests : DatabaseTests
{
    [Fact]
    public async Task Each_commit_reports_what_it_did_to_each_row_in_commit_and_key_order()
    {
        await using var database = await OpenAsync(NewDevice(), changeRetention: 10);
        var counters = database.Table<Counter>();
        var first = await CommitAsync(database, async writer =>
        {
            await counters.InsertAsync(writer, new Counter { Id = 2, Value = 20 }, Ct);
            await counters.InsertAsync(writer, new Counter { Id = 1, Value = 10 }, Ct);
        });
        var second = await CommitAsync(database, async writer =>
        {
            await counters.UpdateAsync(writer, new Counter { Id = 1, Value = 11 }, Ct);
            await counters.DeleteAsync(writer, 2, Ct);
            await counters.InsertAsync(writer, new Counter { Id = 3, Value = 30 }, Ct);

            // Lo que una transacción hace y deshace no es un cambio: se informa el efecto neto.
            await counters.InsertAsync(writer, new Counter { Id = 4, Value = 40 }, Ct);
            await counters.DeleteAsync(writer, 4, Ct);
            await counters.UpdateAsync(writer, new Counter { Id = 3, Value = 31 }, Ct);
        });

        Assert.True(second > first);
        Assert.Equal(second, database.LastTransactionId);
        Assert.Equal(
            [
                new TkChange<Counter>(TkChangeKind.Insert, null, new Counter { Id = 1, Value = 10 }, first),
                new TkChange<Counter>(TkChangeKind.Insert, null, new Counter { Id = 2, Value = 20 }, first),
                new TkChange<Counter>(TkChangeKind.Update, new Counter { Id = 1, Value = 10 }, new Counter { Id = 1, Value = 11 }, second),
                new TkChange<Counter>(TkChangeKind.Delete, new Counter { Id = 2, Value = 20 }, null, second),
                new TkChange<Counter>(TkChangeKind.Insert, null, new Counter { Id = 3, Value = 31 }, second),
            ],
            await TakeAsync(database.WatchAsync<Counter>(0, Ct), 5));

        // Desde el primer commit sólo llega el segundo; y otra tabla sólo ve sus propios cambios.
        Assert.Equal(3, (await TakeAsync(database.WatchAsync<Counter>(first, Ct), 3)).Count(change => change.TransactionId == second));
        var third = await CommitAsync(database, writer => database.Table<Person>().InsertAsync(writer, new Person { Id = Guid.CreateVersion7(), Name = "Ana" }, Ct));
        Assert.Equal(third, Assert.Single(await TakeAsync(database.WatchAsync<Person>(0, Ct), 1)).TransactionId);
    }

    /// <summary>
    /// Criterio de salida: un consumidor que anota el id de la última transacción que procesó retoma desde
    /// ahí, también después de cerrar y reabrir la base, sin perder ni repetir cambios.
    /// </summary>
    [Fact]
    public async Task A_consumer_resumes_from_a_transaction_id_without_losing_or_repeating_changes()
    {
        var device = NewDevice();
        var random = new Random(67);
        var model = new SortedDictionary<int, long>();
        var replica = new SortedDictionary<int, long>();
        var position = 0ul;
        var received = 0;

        for (var session = 0; session < 8; session++)
        {
            await using var database = await OpenAsync(device, changeRetention: 40);
            var counters = database.Table<Counter>();

            // Entre dos lecturas del consumidor pasan varios commits, algunos sin cambios y otros deshechos.
            var commits = random.Next(1, 30);
            for (var commit = 0; commit < commits; commit++)
            {
                await using var writer = await database.BeginWriteAsync(Ct);
                var changed = new SortedDictionary<int, long>(model);
                for (var i = random.Next(0, 40); i > 0; i--)
                {
                    var id = random.Next(300);
                    if (random.Next(3) == 0)
                    {
                        await counters.DeleteAsync(writer, id, Ct);
                        changed.Remove(id);
                    }
                    else
                    {
                        var value = random.Next(5);
                        await counters.UpsertAsync(writer, new Counter { Id = id, Value = value }, Ct);
                        changed[id] = value;
                    }
                }

                if (random.Next(5) != 0)
                {
                    await writer.CommitAsync(Ct);
                    model = changed;
                }
            }

            // El último commit de la sesión deja una marca, para que el consumidor sepa que ya leyó todo.
            await CommitAsync(database, writer => counters.UpsertAsync(writer, new Counter { Id = -1, Value = 1000 + session }, Ct));
            model[-1] = 1000 + session;

            // El consumidor se pone al día: lee hasta el último commit, y anota dónde quedó.
            var last = database.LastTransactionId;
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                await foreach (var change in database.WatchAsync<Counter>(position, timeout.Token))
                {
                    Assert.True(change.TransactionId <= last);
                    Assert.True(change.TransactionId > position, "A change of a transaction already handled came again.");
                    received++;
                    switch (change.Kind)
                    {
                        case TkChangeKind.Insert:
                            Assert.False(replica.ContainsKey(change.After!.Id));
                            replica.Add(change.After.Id, change.After.Value);
                            break;
                        case TkChangeKind.Update:
                            Assert.Equal(replica[change.Before!.Id], change.Before.Value);
                            Assert.NotEqual(change.Before.Value, change.After!.Value);
                            replica[change.After.Id] = change.After.Value;
                            break;
                        default:
                            Assert.Equal(replica[change.Before!.Id], change.Before.Value);
                            replica.Remove(change.Before.Id);
                            break;
                    }

                    if (change.After is { Id: -1 } mark && mark.Value == 1000 + session)
                    {
                        break;
                    }
                }

                position = last;
            }

            // Aplicar los cambios, en orden, reconstruye la tabla tal como está.
            Assert.Equal(model, replica);
            Assert.Equal(model.Select(static pair => new Counter { Id = pair.Key, Value = pair.Value }), await ToListAsync(counters.ScanAsync(Ct)));
            await AssertNoLeaksAsync(database);
        }

        Assert.True(received > 500, $"Only {received} changes went through the feed.");
    }

    /// <summary>Los estados que el historial nombra siguen enteros al reabrir, desde la primera escritura.</summary>
    [Fact]
    public async Task Retained_commits_survive_reopening_and_whatever_is_written_next()
    {
        var device = NewDevice();
        var people = static (TekiumDb database) => database.Table<Person>();
        var ids = Enumerable.Range(0, 300).Select(static _ => Guid.CreateVersion7()).ToList();
        await using (var database = await OpenAsync(device, changeRetention: 50))
        {
            for (var round = 0; round < 6; round++)
            {
                await CommitAsync(database, async writer =>
                {
                    foreach (var id in ids)
                    {
                        await people(database).UpsertAsync(writer, new Person { Id = id, Name = $"Round {round}", Age = round }, Ct);
                    }
                });
            }
        }

        // La primera transacción tras reabrir escribe mucho: si pudiera, reusaría las páginas de esos estados.
        await using var reopened = await OpenAsync(device, changeRetention: 50);
        await CommitAsync(reopened, async writer =>
        {
            foreach (var id in ids)
            {
                await people(reopened).UpsertAsync(writer, new Person { Id = id, Name = "After reopening", Age = 6 }, Ct);
            }
        });

        var changes = await TakeAsync(reopened.WatchAsync<Person>(0, Ct), 7 * ids.Count);
        Assert.Equal(ids.Count, changes.Count(static change => change.Kind == TkChangeKind.Insert));
        Assert.All(changes.Skip(ids.Count), static change => Assert.Equal(change.Before!.Age + 1, change.After!.Age));
        Assert.All(changes.Skip(ids.Count), static change => Assert.Equal($"Round {change.Before!.Age}", change.Before.Name));
        Assert.Equal("After reopening", changes[^1].After!.Name);
        await AssertNoLeaksAsync(reopened);
    }

    [Fact]
    public async Task A_consumer_that_has_caught_up_waits_for_the_next_commit_and_stops_when_cancelled()
    {
        await using var database = await OpenAsync(NewDevice(), changeRetention: 5);
        var counters = database.Table<Counter>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var seen = new List<long>();
        var caughtUp = new SemaphoreSlim(0);
        var watcher = Task.Run(
            async () =>
            {
                await foreach (var change in database.WatchAsync<Counter>(0, cancellation.Token))
                {
                    lock (seen)
                    {
                        seen.Add(change.After!.Value);
                    }

                    caughtUp.Release();
                }
            },
            Ct);

        for (var value = 1; value <= 20; value++)
        {
            await CommitAsync(database, writer => counters.UpsertAsync(writer, new Counter { Id = 1, Value = value }, Ct));

            // Espera a que le llegue: el consumidor va al paso del escritor, no al revés.
            Assert.True(await caughtUp.WaitAsync(TimeSpan.FromSeconds(30), Ct), $"The change of commit {value} never arrived.");
        }

        Assert.Equal(Enumerable.Range(1, 20).Select(static value => (long)value), seen);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watcher);
    }

    [Fact]
    public async Task A_slow_consumer_does_not_hold_the_writer_back_and_is_cut_off_when_it_falls_too_far_behind()
    {
        await using var database = await OpenAsync(NewDevice(), changeRetention: 4);
        var counters = database.Table<Counter>();
        var commits = new List<ulong>();
        for (var value = 1; value <= 3; value++)
        {
            commits.Add(await CommitAsync(database, writer => counters.UpsertAsync(writer, new Counter { Id = 1, Value = value }, Ct)));
        }

        // Toma el primer cambio y se queda ahí, sin pedir el siguiente.
        var slow = database.WatchAsync<Counter>(0, Ct).GetAsyncEnumerator(Ct);
        Assert.True(await slow.MoveNextAsync());
        Assert.Equal(1, slow.Current.After!.Value);

        for (var value = 4; value <= 30; value++)
        {
            commits.Add(await CommitAsync(database, writer => counters.UpsertAsync(writer, new Counter { Id = 1, Value = value }, Ct)));
        }

        // Cuando vuelve, lo que le tocaba ya no se conserva: se le dice, en vez de saltarse cambios.
        var lost = await Assert.ThrowsAsync<TkChangeFeedException>(async () => await slow.MoveNextAsync());
        Assert.Contains($"after transaction {commits[0]}", lost.Message, StringComparison.Ordinal);
        Assert.Contains("last 4 commits", lost.Message, StringComparison.Ordinal);
        await slow.DisposeAsync();

        // Se conservan los últimos cuatro commits: desde el quinto contando desde el final, no.
        Assert.Equal([27L, 28L, 29L, 30L], (await TakeAsync(database.WatchAsync<Counter>(commits[^5], Ct), 4)).Select(static change => change.After!.Value));
        await Assert.ThrowsAsync<TkChangeFeedException>(async () => await TakeAsync(database.WatchAsync<Counter>(commits[^6], Ct), 1));
        await Assert.ThrowsAsync<TkChangeFeedException>(async () => await TakeAsync(database.WatchAsync<Counter>(0, Ct), 1));
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task What_was_never_committed_is_never_reported()
    {
        await using var database = await OpenAsync(NewDevice(), changeRetention: 10);
        var books = database.Table<Book>();
        var created = database.LastTransactionId;
        await CommitAsync(database, writer => database.Table<Author>().InsertAsync(writer, new Author { Handle = "ana" }, Ct));
        var before = database.LastTransactionId;

        // Una transacción deshecha, una abortada por una restricción y una que no cambió nada.
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await books.InsertAsync(writer, new Book { Id = 1, Author = "ana", Title = "Rolled back" }, Ct);
        }

        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await books.InsertAsync(writer, new Book { Id = 2, Author = "ana", Title = "Aborted" }, Ct);
            await Assert.ThrowsAsync<TkConstraintException>(async () => await books.InsertAsync(writer, new Book { Id = 3, Author = "nobody" }, Ct));
        }

        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            Assert.False(await books.DeleteAsync(writer, 99, Ct));
            await writer.CommitAsync(Ct);
        }

        Assert.Equal(before, database.LastTransactionId);
        var committed = await CommitAsync(database, writer => books.InsertAsync(writer, new Book { Id = 4, Author = "ana", Title = "Kept" }, Ct));
        var change = Assert.Single(await TakeAsync(database.WatchAsync<Book>(created, Ct), 1));
        Assert.Equal((TkChangeKind.Insert, 4, committed), (change.Kind, change.After!.Id, change.TransactionId));
    }

    [Fact]
    public async Task Rows_written_with_an_older_version_of_the_type_are_reported_with_the_current_one()
    {
        var device = NewDevice();
        var id = Guid.CreateVersion7();
        await using (var database = await OpenAsync(device, changeRetention: 10))
        {
            await CommitAsync(database, writer => database.Table<ContactV1>().InsertAsync(writer, new ContactV1 { Id = id, Name = "Ana", Phone = "555" }, Ct));
        }

        await using var reopened = await OpenAsync(device, changeRetention: 10);
        await CommitAsync(reopened, async writer => await reopened.Table<ContactV2>().UpdateAsync(writer, new ContactV2 { Id = id, FullName = "Ana María", Phone = "555", Score = 3 }, Ct));
        var changes = await TakeAsync(reopened.WatchAsync<ContactV2>(0, Ct), 2);

        Assert.Equal(new ContactV2 { Id = id, FullName = "Ana", Phone = "555" }, changes[0].After);
        Assert.Equal(new ContactV2 { Id = id, FullName = "Ana", Phone = "555" }, changes[1].Before);
        Assert.Equal(new ContactV2 { Id = id, FullName = "Ana María", Phone = "555", Score = 3 }, changes[1].After);
    }

    [Fact]
    public async Task The_feed_is_off_unless_asked_for_and_a_history_with_a_hole_is_never_reported_as_complete()
    {
        var device = NewDevice();
        var counters = static (TekiumDb database) => database.Table<Counter>();
        ulong kept;
        await using (var database = await OpenAsync(device))
        {
            var refused = Assert.Throws<InvalidOperationException>(() => database.WatchAsync<Counter>(0, Ct));
            Assert.Contains("ChangeRetention", refused.Message, StringComparison.Ordinal);
            await CommitAsync(database, writer => counters(database).InsertAsync(writer, new Counter { Id = 1, Value = 1 }, Ct));
        }

        // Empieza a conservarse cuando ya había datos: desde el principio no hay historia, desde ahora sí.
        await using (var database = await OpenAsync(device, changeRetention: 10))
        {
            kept = database.LastTransactionId;
            await CommitAsync(database, writer => counters(database).InsertAsync(writer, new Counter { Id = 2, Value = 2 }, Ct));
            await Assert.ThrowsAsync<TkChangeFeedException>(async () => await TakeAsync(database.WatchAsync<Counter>(0, Ct), 1));
            Assert.Equal(2, Assert.Single(await TakeAsync(database.WatchAsync<Counter>(kept, Ct), 1)).After!.Id);
        }

        // Una sesión que no la conserva escribe sin anotar: lo anterior deja de servir, y se borra.
        await using (var database = await OpenAsync(device))
        {
            await CommitAsync(database, writer => counters(database).InsertAsync(writer, new Counter { Id = 3, Value = 3 }, Ct));
            await AssertNoLeaksAsync(database);
        }

        await using var reopened = await OpenAsync(device, changeRetention: 10);
        var resumed = reopened.LastTransactionId;
        await CommitAsync(reopened, writer => counters(reopened).InsertAsync(writer, new Counter { Id = 4, Value = 4 }, Ct));
        await Assert.ThrowsAsync<TkChangeFeedException>(async () => await TakeAsync(reopened.WatchAsync<Counter>(kept, Ct), 1));
        Assert.Equal(4, Assert.Single(await TakeAsync(reopened.WatchAsync<Counter>(resumed, Ct), 1)).After!.Id);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await TekiumDb.OpenAsync(new DbOptions { InMemory = true, ChangeRetention = -1 }, Ct));
    }

    /// <summary>Lo que se conserva es lo que se pidió: pasado ese horizonte las páginas vuelven a usarse.</summary>
    [Fact]
    public async Task Retained_commits_cost_space_only_up_to_the_horizon()
    {
        var device = NewDevice();
        await using var database = await OpenAsync(device, changeRetention: 8);
        var people = database.Table<Person>();
        var ids = Enumerable.Range(0, 200).Select(static _ => Guid.CreateVersion7()).ToList();
        var sizes = new List<long>();
        var commits = new List<ulong>();
        for (var round = 0; round < 300; round++)
        {
            commits.Add(await CommitAsync(database, async writer =>
            {
                foreach (var id in ids)
                {
                    await people.UpsertAsync(writer, new Person { Id = id, Name = $"Round {round}", Age = round }, Ct);
                }
            }));
            sizes.Add(device.Length);
        }

        Assert.True(sizes[^1] <= sizes[60] * 1.2, $"The file kept growing: {sizes[60]} bytes after 60 commits, {sizes[^1]} after 300.");
        await AssertNoLeaksAsync(database);

        // Y lo conservado sigue ahí, entero: los ocho últimos commits, con lo que cada fila era y es.
        var changes = await TakeAsync(database.WatchAsync<Person>(commits[^9], Ct), 8 * ids.Count);
        Assert.All(changes, static change => Assert.Equal(change.Before!.Age + 1, change.After!.Age));
        Assert.Equal(Enumerable.Range(292, 8).Cast<int?>(), changes.Select(static change => change.After!.Age).Distinct());
        await Assert.ThrowsAsync<TkChangeFeedException>(async () => await TakeAsync(database.WatchAsync<Person>(commits[^10], Ct), 1));
    }

    private static async Task<ulong> CommitAsync(TekiumDb database, Func<TkWriteTransaction, ValueTask> work)
    {
        await using var writer = await database.BeginWriteAsync(Ct);
        await work(writer);
        await writer.CommitAsync(Ct);
        return writer.TransactionId;
    }

    /// <summary>Los primeros cambios de un feed, que por sí solo no termina.</summary>
    private static async Task<List<TkChange<T>>> TakeAsync<T>(IAsyncEnumerable<TkChange<T>> feed, int count)
        where T : class, ITkRow<T>
    {
        var changes = new List<TkChange<T>>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await foreach (var change in feed.WithCancellation(timeout.Token))
        {
            changes.Add(change);
            if (changes.Count == count)
            {
                break;
            }
        }

        return changes;
    }
}

public sealed class ChangeFeedTests_File : ChangeFeedTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class ChangeFeedTests_Memory : ChangeFeedTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
