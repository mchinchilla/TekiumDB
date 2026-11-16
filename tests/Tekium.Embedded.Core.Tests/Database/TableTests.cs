// Copyright (c) Tekium. Licensed under the MIT License.

using System.Security.Cryptography;
using Tekium.Embedded.Tests.Storage;

namespace Tekium.Embedded.Tests.Database;

public abstract class TableTests : DatabaseTests
{
    [Fact]
    public async Task Rows_are_read_back_by_key_and_in_key_order_after_reopening()
    {
        var device = NewDevice();
        var people = Enumerable.Range(0, 500)
            .Select(i => new Person { Id = Guid.CreateVersion7(), Name = $"Person {i}", Age = i % 3 == 0 ? null : i, Email = i % 2 == 0 ? $"p{i}@example.com" : null })
            .ToList();
        await using (var database = await OpenAsync(device))
        {
            var table = database.Table<Person>();
            await using var writer = await database.BeginWriteAsync(Ct);
            foreach (var person in people)
            {
                await table.InsertAsync(writer, person, Ct);
            }

            await writer.CommitAsync(Ct);
        }

        await using var reopened = await OpenAsync(device);
        var reread = reopened.Table<Person>();
        Assert.Equal("people", reread.Name);
        Assert.Equal(people[137], await reread.GetAsync(people[137].Id, Ct));
        Assert.Null(await reread.GetAsync(Guid.CreateVersion7(), Ct));

        // El orden de la clave es el de los bytes del UUID en orden de red.
        var expected = people.OrderBy(person => person.Id.ToByteArray(bigEndian: true), ByteOrder).ToList();
        Assert.Equal(expected, await ToListAsync(reread.ScanAsync(Ct)));
        await AssertNoLeaksAsync(reopened);
    }

    [Fact]
    public async Task A_table_that_was_never_written_reads_as_empty_without_writing_anything()
    {
        var device = NewDevice();
        await using var database = await OpenAsync(device);
        var writes = device.WriteCount;

        Assert.Null(await database.Table<Counter>().GetAsync(1, Ct));
        Assert.Empty(await ToListAsync(database.Table<Counter>().ScanAsync(Ct)));
        await using (var read = database.BeginRead())
        {
            Assert.Null(await database.Table<Counter>().GetAsync(read, 1, Ct));
        }

        Assert.Equal(writes, device.WriteCount);
    }

    [Fact]
    public async Task Insert_update_upsert_and_delete_say_what_they_did()
    {
        await using var database = await OpenAsync(NewDevice());
        var counters = database.Table<Counter>();
        await using var writer = await database.BeginWriteAsync(Ct);

        await counters.InsertAsync(writer, new Counter { Id = 1, Value = 10 }, Ct);
        var duplicate = await Assert.ThrowsAsync<TkDuplicateKeyException>(
            async () => await counters.InsertAsync(writer, new Counter { Id = 1, Value = 11 }, Ct));
        Assert.Contains("Counter", duplicate.Message, StringComparison.Ordinal);
        Assert.Equal(10, (await counters.GetAsync(writer, 1, Ct))!.Value);

        Assert.True(await counters.UpdateAsync(writer, new Counter { Id = 1, Value = 20 }, Ct));
        Assert.False(await counters.UpdateAsync(writer, new Counter { Id = 2, Value = 20 }, Ct));
        Assert.Null(await counters.GetAsync(writer, 2, Ct));

        await counters.UpsertAsync(writer, new Counter { Id = 2, Value = 30 }, Ct);
        await counters.UpsertAsync(writer, new Counter { Id = 2, Value = 31 }, Ct);
        await counters.UpsertAsync(writer, new Counter { Id = -5, Value = 1 }, Ct);

        Assert.True(await counters.DeleteAsync(writer, 1, Ct));
        Assert.False(await counters.DeleteAsync(writer, 1, Ct));
        await writer.CommitAsync(Ct);

        // Los negativos van primero: el orden de la clave es el de los enteros.
        Assert.Equal(
            [new Counter { Id = -5, Value = 1 }, new Counter { Id = 2, Value = 31 }],
            await ToListAsync(counters.ScanAsync(Ct)));
    }

    [Fact]
    public async Task Nobody_else_sees_a_transaction_until_it_commits_and_a_reader_keeps_what_it_saw()
    {
        await using var database = await OpenAsync(NewDevice());
        var counters = database.Table<Counter>();
        await using (var first = await database.BeginWriteAsync(Ct))
        {
            await counters.InsertAsync(first, new Counter { Id = 1, Value = 1 }, Ct);
            await first.CommitAsync(Ct);
        }

        await using var before = database.BeginRead();
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await counters.UpsertAsync(writer, new Counter { Id = 1, Value = 2 }, Ct);
            await counters.InsertAsync(writer, new Counter { Id = 2, Value = 2 }, Ct);

            // El escritor ve lo suyo; los demás, el último commit.
            Assert.Equal(2, (await counters.GetAsync(writer, 1, Ct))!.Value);
            Assert.Equal(2, (await ToListAsync(counters.ScanAsync(writer, Ct))).Count);
            Assert.Equal(1, (await counters.GetAsync(1, Ct))!.Value);
            Assert.Null(await counters.GetAsync(2, Ct));
            await writer.CommitAsync(Ct);
        }

        Assert.Equal(2, (await counters.GetAsync(1, Ct))!.Value);
        Assert.Equal(1, (await counters.GetAsync(before, 1, Ct))!.Value);
        Assert.Single(await ToListAsync(counters.ScanAsync(before, Ct)));
    }

    [Fact]
    public async Task A_transaction_left_without_commit_leaves_no_rows_and_no_table()
    {
        var device = NewDevice();
        await using (var database = await OpenAsync(device))
        {
            await using (var abandoned = await database.BeginWriteAsync(Ct))
            {
                await database.Table<Counter>().InsertAsync(abandoned, new Counter { Id = 1, Value = 1 }, Ct);
                await database.Table<Person>().InsertAsync(abandoned, new Person { Id = Guid.CreateVersion7(), Name = "nobody" }, Ct);
            }

            Assert.Null(await database.Table<Counter>().GetAsync(1, Ct));
            await using (var writer = await database.BeginWriteAsync(Ct))
            {
                await database.Table<Counter>().InsertAsync(writer, new Counter { Id = 1, Value = 7 }, Ct);
                await writer.CommitAsync(Ct);
            }

            await AssertNoLeaksAsync(database);
        }

        await using var reopened = await OpenAsync(device);
        Assert.Equal(7, (await reopened.Table<Counter>().GetAsync(1, Ct))!.Value);
        Assert.Empty(await ToListAsync(reopened.Table<Person>().ScanAsync(Ct)));
    }

    [Fact]
    public async Task Several_tables_grow_and_shrink_in_one_file_without_losing_a_page()
    {
        var device = NewDevice();
        await using (var database = await OpenAsync(device))
        {
            var counters = database.Table<Counter>();
            var notes = database.Table<Note>();
            var orders = database.Table<Order>();
            var nested = database.Table<Outer.Inner>();
            for (var round = 0; round < 6; round++)
            {
                await using var writer = await database.BeginWriteAsync(Ct);
                for (var i = 0; i < 1500; i++)
                {
                    var id = (round * 1500) + i;
                    await counters.UpsertAsync(writer, new Counter { Id = id, Value = id * 2L }, Ct);
                    if (i % 3 == 0)
                    {
                        await notes.UpsertAsync(writer, new Note { Slug = $"note-{id:D6}", Body = new string('x', id % 200), Attachment = i % 6 == 0 ? [1, 2, 3] : null }, Ct);
                        await orders.UpsertAsync(writer, new Order { Id = id, Total = id / 100m, Priority = Priority.High, CreatedAt = DateTimeOffset.UnixEpoch.AddHours(id) }, Ct);
                        await nested.UpsertAsync(writer, new Outer.Inner { Id = (ulong)id, Text = round % 2 == 0 ? "even" : null }, Ct);
                    }

                    // Borra parte de lo que escribió la ronda anterior, para que los árboles también encojan.
                    if (round > 0 && i % 2 == 0)
                    {
                        Assert.True(await counters.DeleteAsync(writer, id - 1500, Ct));
                    }
                }

                await writer.CommitAsync(Ct);
                await AssertNoLeaksAsync(database);
            }
        }

        await using var reopened = await OpenAsync(device);
        Assert.Equal((5 * 750) + 1500, (await ToListAsync(reopened.Table<Counter>().ScanAsync(Ct))).Count);
        var allNotes = await ToListAsync(reopened.Table<Note>().ScanAsync(Ct));
        Assert.Equal(3000, allNotes.Count);
        Assert.Equal(allNotes.OrderBy(static note => note.Slug, StringComparer.Ordinal).Select(static note => note.Slug), allNotes.Select(static note => note.Slug));
        Assert.Equal("not stored", allNotes[0].Scratch);
        var order = await reopened.Table<Order>().GetAsync(4500L, Ct);
        Assert.Equal((45m, Priority.High, DateTimeOffset.UnixEpoch.AddHours(4500)), (order!.Total, order.Priority, order.CreatedAt));
        Assert.Null((await reopened.Table<Outer.Inner>().GetAsync(1503UL, Ct))!.Text);
        await AssertNoLeaksAsync(reopened);
    }

    [Fact]
    public async Task Misuse_is_refused_with_an_error_that_says_what_was_wrong()
    {
        await using var database = await OpenAsync(NewDevice());
        await using var other = await OpenAsync(NewDevice());
        var notes = database.Table<Note>();
        await using var writer = await database.BeginWriteAsync(Ct);

        // La clave de la tabla es un string, no un entero.
        var wrongKey = await Assert.ThrowsAsync<ArgumentException>(async () => await notes.GetAsync(5, Ct));
        Assert.Contains("Text", wrongKey.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(async () => await notes.DeleteAsync(writer, Guid.Empty, Ct));

        // Una columna no anulable sin valor: el error nombra la propiedad.
        var missing = await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await notes.InsertAsync(writer, new Note { Slug = "a", Body = null! }, Ct));
        Assert.Equal("Body", missing.ParamName);

        // Texto que no es Unicode válido no se guarda a medias.
        await Assert.ThrowsAnyAsync<ArgumentException>(
            async () => await notes.InsertAsync(writer, new Note { Slug = "bad", Body = "\ud800" }, Ct));

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await other.Table<Note>().InsertAsync(writer, new Note { Slug = "a", Body = "b" }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await other.Table<Note>().GetAsync(writer, "a", Ct));

        // Nada de lo anterior dejó la transacción inservible.
        await notes.InsertAsync(writer, new Note { Slug = "a", Body = "b" }, Ct);
        await writer.CommitAsync(Ct);
        Assert.Equal("b", (await notes.GetAsync("a", Ct))!.Body);
        await AssertNoLeaksAsync(database);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("large rows")]
    public async Task Rows_far_larger_than_a_page_are_stored_replaced_and_deleted_like_any_other(string? password)
    {
        var device = NewDevice();
        var body = string.Concat(Enumerable.Range(0, 60_000).Select(static i => (char)('a' + (i % 26)))) + " ñ \U0001F600";
        var attachment = new byte[700_000];
        new Random(8).NextBytes(attachment);
        await using (var database = await OpenAsync(device, password))
        {
            var notes = database.Table<Note>();
            await using (var writer = await database.BeginWriteAsync(Ct))
            {
                await notes.InsertAsync(writer, new Note { Slug = "small", Body = "s" }, Ct);
                await notes.InsertAsync(writer, new Note { Slug = "large", Body = body, Attachment = attachment }, Ct);
                await notes.InsertAsync(writer, new Note { Slug = "medium", Body = body[..3000] }, Ct);
                await Assert.ThrowsAsync<TkDuplicateKeyException>(
                    async () => await notes.InsertAsync(writer, new Note { Slug = "large", Body = "again" }, Ct));
                await writer.CommitAsync(Ct);
            }

            await AssertNoLeaksAsync(database);
        }

        await using var reopened = await OpenAsync(device, password);
        var reread = reopened.Table<Note>();
        var large = await reread.GetAsync("large", Ct);
        Assert.Equal(body, large!.Body);
        Assert.Equal(attachment, large.Attachment);
        var all = await ToListAsync(reread.ScanAsync(Ct));
        Assert.Equal(["large", "medium", "small"], all.Select(static note => note.Slug));
        Assert.Equal(body, all[0].Body);
        Assert.Equal(attachment, all[0].Attachment);

        var before = reopened.Pager.Current.PageCount;
        await using (var writer = await reopened.BeginWriteAsync(Ct))
        {
            Assert.True(await reread.UpdateAsync(writer, new Note { Slug = "large", Body = "now small" }, Ct));
            Assert.True(await reread.UpdateAsync(writer, new Note { Slug = "small", Body = body }, Ct));
            Assert.True(await reread.DeleteAsync(writer, "medium", Ct));
            await writer.CommitAsync(Ct);
        }

        Assert.Equal("now small", (await reread.GetAsync("large", Ct))!.Body);
        Assert.Equal(body, (await reread.GetAsync("small", Ct))!.Body);
        await AssertNoLeaksAsync(reopened);

        // Reescribir la fila grande una y otra vez deja de hacer crecer el archivo: copy-on-write necesita
        // sitio para la copia nueva mientras la anterior sigue reservada, pero lo liberado se reutiliza.
        uint settled = 0;
        for (var i = 0; i < 12; i++)
        {
            await using (var writer = await reopened.BeginWriteAsync(Ct))
            {
                await reread.UpsertAsync(writer, new Note { Slug = "large", Body = body, Attachment = attachment }, Ct);
                await writer.CommitAsync(Ct);
            }

            settled = i == 5 ? reopened.Pager.Current.PageCount : settled;
        }

        Assert.True(
            reopened.Pager.Current.PageCount <= settled + 8,
            $"Rewriting a large row kept growing the file: {before} pages, then {settled}, then {reopened.Pager.Current.PageCount}.");
        Assert.True(settled <= before * 4, $"A rewritten large row takes too much room: {before} pages became {settled}.");
        await AssertNoLeaksAsync(reopened);
    }

    [Fact]
    public async Task A_table_with_more_columns_than_fit_in_a_page_works()
    {
        var device = NewDevice();
        await using (var database = await OpenAsync(device))
        {
            await using var writer = await database.BeginWriteAsync(Ct);
            await database.Table<Wide>().InsertAsync(writer, new Wide { Id = 7, Last = 123 }, Ct);
            await writer.CommitAsync(Ct);
        }

        // El esquema solo ya pasa de una página de 4 KiB; al reabrir se lee del catálogo.
        await using var reopened = await OpenAsync(device);
        var row = await reopened.Table<Wide>().GetAsync(7, Ct);
        Assert.Equal((7, 123), (row!.Id, row.Last));
        await AssertNoLeaksAsync(reopened);
    }

    [Fact]
    public async Task An_encrypted_database_needs_its_password_and_can_change_it()
    {
        var device = NewDevice();
        await using (var database = await OpenAsync(device, "first password"))
        {
            Assert.True(database.IsEncrypted);
            await using (var writer = await database.BeginWriteAsync(Ct))
            {
                await database.Table<Note>().InsertAsync(writer, new Note { Slug = "secret", Body = "the body of the secret note" }, Ct);
                await writer.CommitAsync(Ct);
            }

            await database.ChangePasswordAsync("second password".AsMemory(), Ct);
        }

        var stored = await device.ReadAllAsync();
        Assert.True(stored.AsSpan().IndexOf("the body"u8) < 0, "The text of a row is readable in the file.");
        await Assert.ThrowsAsync<CryptographicException>(async () => await OpenAsync(device, "first password"));
        await Assert.ThrowsAsync<CryptographicException>(async () => await OpenAsync(device));
        await using var reopened = await OpenAsync(device, "second password");
        Assert.Equal("the body of the secret note", (await reopened.Table<Note>().GetAsync("secret", Ct))!.Body);
    }

    private static readonly Comparer<byte[]> ByteOrder =
        Comparer<byte[]>.Create(static (a, b) => a.AsSpan().SequenceCompareTo(b));
}

public sealed class TableTests_File : TableTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class TableTests_Memory : TableTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}

/// <summary>Abrir por opciones, como lo hace quien usa la librería: un archivo de verdad, o memoria.</summary>
public sealed class DatabaseOpenTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_database_in_a_file_is_created_on_first_open_and_found_on_the_next()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tekium-open-{Guid.NewGuid():N}.tkdb");
        try
        {
            var options = new DbOptions { Path = path, PageSize = 16384, CacheSizeMb = 4 };
            await using (var database = await TekiumDb.OpenAsync(options, Ct))
            {
                Assert.False(database.IsEncrypted);
                await using var writer = await database.BeginWriteAsync(Ct);
                await database.Table<Counter>().InsertAsync(writer, new Counter { Id = 1, Value = 99 }, Ct);
                await writer.CommitAsync(Ct);
            }

            // El tamaño de página pedido al reabrir se ignora: el archivo conoce el suyo.
            await using var reopened = await TekiumDb.OpenAsync(new DbOptions { Path = path, PageSize = 4096 }, Ct);
            Assert.Equal(99, (await reopened.Table<Counter>().GetAsync(1, Ct))!.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_compressed_database_is_used_exactly_like_any_other(bool encrypted)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tekium-compressed-{Guid.NewGuid():N}.tkdb");
        var password = (encrypted ? "compressed password" : string.Empty).AsMemory();
        var body = string.Concat(Enumerable.Repeat("the same sentence again and again. ", 400));
        try
        {
            await using (var database = await TekiumDb.OpenAsync(
                new DbOptions { Path = path, Compression = true, CompressionLevel = 9, Password = password, KeyDerivation = new(64, 1, 1) }, Ct))
            {
                Assert.True(database.IsCompressed);
                await using var writer = await database.BeginWriteAsync(Ct);
                for (var i = 0; i < 300; i++)
                {
                    await database.Table<Note>().InsertAsync(writer, new Note { Slug = $"note-{i:D4}", Body = body[..(i * 40)] }, Ct);
                }

                await writer.CommitAsync(Ct);
            }

            // Al reabrir no se pide compresión: el archivo ya dice que la usa.
            await using var reopened = await TekiumDb.OpenAsync(new DbOptions { Path = path, Password = password }, Ct);
            Assert.True(reopened.IsCompressed);
            Assert.Equal(encrypted, reopened.IsEncrypted);
            var count = 0;
            await foreach (var note in reopened.Table<Note>().ScanAsync(Ct))
            {
                Assert.Equal(body[..(count * 40)], note.Body);
                count++;
            }

            Assert.Equal(300, count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_database_in_memory_works_like_one_in_a_file_and_is_gone_when_disposed()
    {
        await using var database = await TekiumDb.OpenAsync(new DbOptions { InMemory = true }, Ct);
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await database.Table<Counter>().InsertAsync(writer, new Counter { Id = 1, Value = 5 }, Ct);
            await writer.CommitAsync(Ct);
        }

        Assert.Equal(5, (await database.Table<Counter>().GetAsync(1, Ct))!.Value);
        Assert.Same(database.Table<Counter>(), database.Table<Counter>());
    }

    [Fact]
    public async Task Options_that_make_no_sense_are_refused_before_touching_anything()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () => await TekiumDb.OpenAsync(new DbOptions(), Ct));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await TekiumDb.OpenAsync(new DbOptions { Path = "x.tkdb", InMemory = true }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await TekiumDb.OpenAsync(new DbOptions { InMemory = true, PageSize = 5000 }, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await TekiumDb.OpenAsync(new DbOptions { InMemory = true, CacheSizeMb = 0 }, Ct));
        Assert.False(File.Exists("x.tkdb"));
    }
}
