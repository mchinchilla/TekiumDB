// Copyright (c) Tekium. Licensed under the MIT License.

using System.Security.Cryptography;
using Tekium.Embedded.Catalog;
using Tekium.Embedded.Storage;
using Tekium.Embedded.Tests.Database;
using Tekium.Embedded.Tests.Storage;

namespace Tekium.Embedded.Tests.Maintenance;

/// <summary>
/// Compactar y reparar escriben una base nueva con lo que leen de otra, sin tocar la original. Compactar
/// exige un archivo sano; reparar copia lo que se puede leer y dice qué dejó fuera.
/// </summary>
public abstract class CopyTests : DatabaseTests
{
    private const int Page = 4096;

    [Fact]
    public async Task Compacting_keeps_everything_and_gives_back_the_space_updates_left_behind()
    {
        var source = NewDevice();
        var (tickets, notes) = await FillAsync(source);
        var before = await source.ReadAllAsync();
        var target = NewDevice();

        var report = await TkMaintenance.CopyAsync(() => source, _ => target, null, salvage: false, Ct);

        Assert.Empty(report.Findings);
        Assert.Empty(report.Dropped);
        Assert.Equal(before.Length, report.SourceBytes);
        Assert.True(report.DestinationBytes * 2 < report.SourceBytes, $"{report.SourceBytes} bytes became {report.DestinationBytes}.");
        Assert.Equal(before, await source.ReadAllAsync());

        // Filas, índices (cada uno con una entrada por fila que le toca) y restricciones: nada falta.
        Assert.Equal((tickets.Count * 5) + tickets.Count(static t => t.IsOpen) + notes.Count, report.EntriesCopied);
        await using var database = await OpenAsync(target);
        await AssertSameAsync(database, tickets, notes);
        Assert.Equal(new TkPlan(TkAccess.IndexScan, "TitleLower"), await database.Table<Ticket>().ExplainAsync(Ticket.Fields.TitleLower.EqualTo("ticket 7"), Ct));
        Assert.Equal(0, database.Pager.FreePageCount);
        await AssertNoLeaksAsync(database);
        Assert.True((await TkInspector.InspectAsync(() => target, default, Ct)).IsHealthy);
    }

    [Fact]
    public async Task Compacting_refuses_a_damaged_database()
    {
        var source = NewDevice();
        await FillAsync(source);
        await source.CorruptAsync(((long)await RootOfAsync(source, "tickets") * Page) + 300);
        var target = NewDevice();

        var failure = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await TkMaintenance.CopyAsync(() => source, _ => target, null, salvage: false, Ct));

        Assert.Contains("table tickets", failure.Message, StringComparison.Ordinal);

        // Lo que quedó en el destino no es una base con datos: nada se confirmó.
        await using var database = await OpenAsync(target);
        Assert.Empty(await ToListAsync(database.Table<Ticket>().ScanAsync(Ct)));
    }

    [Fact]
    public async Task The_copy_can_take_another_password_or_none()
    {
        var source = NewDevice();
        var (tickets, notes) = await FillAsync(source, "old password");

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await TkMaintenance.CopyAsync(() => source, _ => NewDevice(), null, salvage: false, Ct));

        var same = NewDevice();
        await TkMaintenance.CopyAsync(() => source, _ => same, new TkCopyOptions { Password = "old password".AsMemory() }, salvage: false, Ct);
        await using (var database = await OpenAsync(same, "old password"))
        {
            await AssertSameAsync(database, tickets, notes);
        }

        // Aun con la misma contraseña, la copia tiene su propia clave de archivo: las páginas no se parecen.
        Assert.NotEqual((await source.ReadAllAsync()).AsSpan(2 * Page, Page).ToArray(), (await same.ReadAllAsync()).AsSpan(2 * Page, Page).ToArray());

        var other = NewDevice();
        await TkMaintenance.CopyAsync(
            () => source, _ => other, new TkCopyOptions { Password = "old password".AsMemory(), NewPassword = "new password".AsMemory() }, salvage: false, Ct);
        await Assert.ThrowsAsync<CryptographicException>(async () => await OpenAsync(other, "old password"));
        await using (var database = await OpenAsync(other, "new password"))
        {
            await AssertSameAsync(database, tickets, notes);
        }

        var plain = NewDevice();
        await TkMaintenance.CopyAsync(
            () => source, _ => plain, new TkCopyOptions { Password = "old password".AsMemory(), NewPassword = ReadOnlyMemory<char>.Empty }, salvage: false, Ct);
        await using (var database = await OpenAsync(plain))
        {
            Assert.False(database.IsEncrypted);
            await AssertSameAsync(database, tickets, notes);
        }
    }

    [Fact]
    public async Task Repairing_keeps_the_rows_that_can_be_read_and_leaves_out_the_indexes_of_a_table_that_lost_some()
    {
        var source = NewDevice();
        var (tickets, notes) = await FillAsync(source);

        // Una hoja de la tabla de tickets: se pierden sus filas y nada más.
        var leaf = await FirstLeafAsync(source, "tickets");
        await source.CorruptAsync(((long)leaf * Page) + 300);
        var target = NewDevice();

        var report = await TkMaintenance.CopyAsync(() => source, _ => target, null, salvage: true, Ct);

        var finding = Assert.Single(report.Findings);
        Assert.Equal((TkFindingKind.PageDamaged, (long?)leaf, "table tickets"), (finding.Kind, finding.Page, finding.Where));
        Assert.Equal(
            ["index tickets.Due", "index tickets.Owner", "index tickets.Owner_Priority", "index tickets.TitleLower", "index tickets.open"],
            report.Dropped.Order(StringComparer.Ordinal));

        await using var database = await OpenAsync(target);
        var table = database.Table<Ticket>();
        var kept = await ToListAsync(table.ScanAsync(Ct));
        Assert.InRange(kept.Count, tickets.Count / 2, tickets.Count - 1);
        Assert.All(kept, ticket => Assert.Equal(tickets.Single(original => original.Id == ticket.Id), ticket));
        Assert.Equal(notes.OrderBy(static n => n.Slug, StringComparer.Ordinal).Select(static n => n.Slug), (await ToListAsync(database.Table<Note>().ScanAsync(Ct))).Select(static n => n.Slug));

        // Sin índices las consultas recorren la tabla, y aciertan; la primera escritura los vuelve a construir.
        var byOwner = Ticket.Fields.Owner.EqualTo("owner3");
        Assert.Equal(new TkPlan(TkAccess.TableScan, null), await table.ExplainAsync(byOwner, Ct));
        Assert.Equal(kept.Where(static t => t.Owner == "owner3").Select(static t => t.Id), (await ToListAsync(table.QueryAsync(byOwner, Ct))).Select(static t => t.Id));
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await table.InsertAsync(writer, new Ticket { Id = 9_000_000, Owner = "owner3", Title = "after the repair" }, Ct);
            await writer.CommitAsync(Ct);
        }

        Assert.Equal(TkAccess.IndexScan, (await table.ExplainAsync(byOwner, Ct)).Access);
        Assert.Equal(
            kept.Where(static t => t.Owner == "owner3").Select(static t => t.Id).Append(9_000_000),
            (await ToListAsync(table.QueryAsync(byOwner, Ct))).Select(static t => t.Id).Order());
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task Repairing_leaves_out_only_the_index_that_was_damaged()
    {
        var source = NewDevice();
        var (tickets, notes) = await FillAsync(source);
        uint root;
        await using (var original = await OpenAsync(source))
        {
            using var read = original.Pager.BeginRead();
            root = CatalogStore.IndexRoot(await CatalogStore.ReadAsync(original.Pager, read.Root, CatalogStore.IndexKey(CatalogStore.TableKey("tickets"), "TitleLower"), Ct)).Value;
        }

        await source.CorruptAsync(((long)root * Page) + 300);
        var target = NewDevice();

        var report = await TkMaintenance.CopyAsync(() => source, _ => target, null, salvage: true, Ct);

        Assert.Equal(["index tickets.TitleLower"], report.Dropped);
        Assert.Equal((tickets.Count * 4) + tickets.Count(static t => t.IsOpen) + notes.Count, report.EntriesCopied);
        await using var database = await OpenAsync(target);
        await AssertSameAsync(database, tickets, notes);
        var byTitle = Ticket.Fields.TitleLower.EqualTo("ticket 7");
        Assert.Equal(new TkPlan(TkAccess.TableScan, null), await database.Table<Ticket>().ExplainAsync(byTitle, Ct));
        Assert.Equal(7, Assert.Single(await ToListAsync(database.Table<Ticket>().QueryAsync(byTitle, Ct))).Id);
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task Repairing_a_healthy_database_is_compacting_it_and_one_that_cannot_be_reached_is_refused()
    {
        var source = NewDevice();
        var (tickets, notes) = await FillAsync(source);
        var target = NewDevice();

        var report = await TkMaintenance.CopyAsync(() => source, _ => target, null, salvage: true, Ct);

        Assert.Empty(report.Findings);
        Assert.Empty(report.Dropped);
        await using (var database = await OpenAsync(target))
        {
            await AssertSameAsync(database, tickets, notes);
        }

        var noise = NewDevice();
        await noise.OverwriteAsync(0, new byte[2 * Page]);
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await TkMaintenance.CopyAsync(() => noise, _ => NewDevice(), null, salvage: true, Ct));
    }

    [Fact]
    public async Task Constraints_go_along_and_are_still_enforced_in_the_copy()
    {
        var source = NewDevice();
        await using (var original = await OpenAsync(source))
        {
            await using var writer = await original.BeginWriteAsync(Ct);
            await original.Table<Author>().InsertAsync(writer, new Author { Handle = "ana", Email = "ana@example.com", Name = "Ana" }, Ct);
            await original.Table<Book>().InsertAsync(writer, new Book { Id = 1, Author = "ana", Title = "First", Isbn = "1", Price = 10 }, Ct);
            await writer.CommitAsync(Ct);
        }

        var target = NewDevice();
        await TkMaintenance.CopyAsync(() => source, _ => target, null, salvage: false, Ct);

        // Las entradas del catálogo son las mismas, byte por byte: restricciones y quién se refiere a quién.
        await using var first = await OpenAsync(source);
        await using var database = await OpenAsync(target);
        foreach (var key in new[]
        {
            CatalogStore.ConstraintsKey(CatalogStore.TableKey("books")),
            CatalogStore.ReferrersKey(CatalogStore.TableKey("authors")),
            CatalogStore.ReferrersKey(CatalogStore.TableKey("books")),
        })
        {
            var expected = await CatalogStore.ReadAsync(first.Pager, first.Pager.Current.Root, key, Ct);
            Assert.NotEmpty(expected);
            Assert.Equal(expected, await CatalogStore.ReadAsync(database.Pager, database.Pager.Current.Root, key, Ct));
        }

        // Y valen sin que nadie las vuelva a declarar: borrar un autor con libros se rechaza en la primera escritura.
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            var refused = await Assert.ThrowsAsync<TkConstraintException>(async () => await database.Table<Author>().DeleteAsync(writer, "ana", Ct));
            Assert.Equal("books", refused.Table);
        }

        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await Assert.ThrowsAsync<TkConstraintException>(
                async () => await database.Table<Book>().InsertAsync(writer, new Book { Id = 2, Author = "nobody", Title = "Second", Isbn = "2" }, Ct));
        }

        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await Assert.ThrowsAsync<TkConstraintException>(
                async () => await database.Table<Author>().InsertAsync(writer, new Author { Handle = "eva", Email = "ana@example.com" }, Ct));
        }
    }

    [Fact]
    public async Task A_catalog_entry_this_build_does_not_know_stops_a_compaction_and_is_left_out_of_a_repair()
    {
        var source = NewDevice();
        await using (var original = await OpenAsync(source))
        {
            await using var writer = await original.BeginWriteAsync(Ct);
            await original.Table<Counter>().InsertAsync(writer, new Counter { Id = 1, Value = 1 }, Ct);
            await original.Tree.PutAsync(writer.Inner, new byte[] { 99, 1, 2 }, new byte[] { 7, 7, 7, 7 }, Ct);
            await writer.CommitAsync(Ct);
        }

        var inspection = await TkInspector.InspectAsync(() => source, default, Ct);
        var finding = Assert.Single(inspection.Findings);
        Assert.Equal((TkSeverity.Warning, TkFindingKind.UnknownCatalogEntry), (finding.Severity, finding.Kind));

        var failure = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await TkMaintenance.CopyAsync(() => source, _ => NewDevice(), null, salvage: false, Ct));
        Assert.Contains("does not know", failure.Message, StringComparison.Ordinal);

        var target = NewDevice();
        var report = await TkMaintenance.CopyAsync(() => source, _ => target, null, salvage: true, Ct);
        Assert.Equal(TkFindingKind.UnknownCatalogEntry, Assert.Single(report.Findings).Kind);
        await using var database = await OpenAsync(target);
        Assert.Equal(1, (await database.Table<Counter>().GetAsync(1, Ct))!.Value);
        Assert.Empty(await CatalogStore.ReadAsync(database.Pager, database.Pager.Current.Root, new byte[] { 99, 1, 2 }, Ct));
    }

    [Fact]
    public async Task Rows_written_by_older_versions_of_a_type_and_compressed_pages_are_copied_as_they_are()
    {
        var source = NewDevice();
        var options = new DbOptions { InMemory = true, PageSize = Page, CacheSizeMb = 1, Compression = true };
        var old = Guid.CreateVersion7();
        var recent = Guid.CreateVersion7();
        await using (var original = await TekiumDb.OpenAsync(_ => source, options, Ct))
        {
            await using (var writer = await original.BeginWriteAsync(Ct))
            {
                await original.Table<ContactV1>().InsertAsync(writer, new ContactV1 { Id = old, Name = "Ana", Legacy = 7, Phone = "555" }, Ct);
                await writer.CommitAsync(Ct);
            }

            await using (var writer = await original.BeginWriteAsync(Ct))
            {
                await original.Table<ContactV2>().InsertAsync(writer, new ContactV2 { Id = recent, FullName = "Eva", Score = 3 }, Ct);
                await writer.CommitAsync(Ct);
            }
        }

        var target = NewDevice();
        await TkMaintenance.CopyAsync(() => source, _ => target, null, salvage: false, Ct);

        await using var database = await TekiumDb.OpenAsync(_ => target, options, Ct);
        Assert.True(database.IsCompressed);
        Assert.Equal(2, await CountSchemaVersionsAsync(database));
        var info = await CatalogStore.FindTableAsync(database.Pager, database.Pager.Current.Root, CatalogStore.TableKey("contacts"), Ct);
        Assert.Equal(2, info!.Value.SchemaVersion);
        var contacts = database.Table<ContactV2>();
        Assert.Equal(("Ana", "555"), ((await contacts.GetAsync(old, Ct))!.FullName, (await contacts.GetAsync(old, Ct))!.Phone));
        Assert.Equal(("Eva", 3), ((await contacts.GetAsync(recent, Ct))!.FullName, (await contacts.GetAsync(recent, Ct))!.Score));

        // La versión del esquema sigue siendo la segunda: escribir con el mismo tipo no agrega otra.
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await contacts.UpsertAsync(writer, new ContactV2 { Id = recent, FullName = "Eva", Score = 4 }, Ct);
            await writer.CommitAsync(Ct);
        }

        Assert.Equal(2, await CountSchemaVersionsAsync(database));
    }

    [Fact]
    public async Task The_history_of_changes_stays_behind_and_the_copy_starts_its_own()
    {
        var source = NewDevice();
        await using (var original = await OpenAsync(source, changeRetention: 8))
        {
            for (var i = 0; i < 5; i++)
            {
                await using var writer = await original.BeginWriteAsync(Ct);
                await original.Table<Counter>().UpsertAsync(writer, new Counter { Id = 1, Value = i }, Ct);
                await writer.CommitAsync(Ct);
            }
        }

        var target = NewDevice();
        await TkMaintenance.CopyAsync(() => source, _ => target, null, salvage: false, Ct);

        await using var database = await OpenAsync(target, changeRetention: 8);
        Assert.Equal(4, (await database.Table<Counter>().GetAsync(1, Ct))!.Value);

        // Ninguna entrada del historial viaja: apuntarían a páginas del archivo original.
        Assert.Empty((await HistoryStore.LoadAsync(database.Pager, database.Pager.Current.Root, Ct)).Transactions);
        var position = database.LastTransactionId;
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await database.Table<Counter>().UpsertAsync(writer, new Counter { Id = 1, Value = 99 }, Ct);
            await writer.CommitAsync(Ct);
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await foreach (var change in database.WatchAsync<Counter>(position, stop.Token))
        {
            Assert.Equal(99, change.After!.Value);
            break;
        }

        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task By_path_the_destination_must_not_exist_and_a_failed_copy_leaves_no_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tekium-copy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.tkdb");
            var options = new DbOptions { Path = source, PageSize = Page, CacheSizeMb = 1 };
            await using (var database = await TekiumDb.OpenAsync(options, Ct))
            {
                await using var writer = await database.BeginWriteAsync(Ct);
                for (var i = 0; i < 500; i++)
                {
                    await database.Table<Counter>().InsertAsync(writer, new Counter { Id = i, Value = i }, Ct);
                }

                await writer.CommitAsync(Ct);
            }

            var compacted = Path.Combine(directory, "compacted.tkdb");
            var report = await TkMaintenance.CompactAsync(source, compacted, cancellationToken: Ct);
            Assert.Equal(500, report.EntriesCopied);
            Assert.InRange(report.DestinationBytes, 3 * Page, new FileInfo(compacted).Length);
            await Assert.ThrowsAsync<IOException>(async () => await TkMaintenance.CompactAsync(source, compacted, cancellationToken: Ct));
            Assert.True((await TkInspector.InspectAsync(compacted, cancellationToken: Ct)).IsHealthy);

            // Dañada: compactar falla y no deja archivo; reparar sí lo escribe.
            var bytes = await File.ReadAllBytesAsync(source, Ct);
            bytes[(3 * Page) + 200] ^= 0xFF;
            await File.WriteAllBytesAsync(source, bytes, Ct);
            var failed = Path.Combine(directory, "failed.tkdb");
            await Assert.ThrowsAsync<InvalidDataException>(async () => await TkMaintenance.CompactAsync(source, failed, cancellationToken: Ct));
            Assert.False(File.Exists(failed));
            var repaired = Path.Combine(directory, "repaired.tkdb");
            Assert.NotEmpty((await TkMaintenance.RepairAsync(source, repaired, cancellationToken: Ct)).Findings);
            Assert.True((await TkInspector.InspectAsync(repaired, cancellationToken: Ct)).IsHealthy);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Tickets con sus cinco índices y notas con adjuntos grandes, reescritos varias veces: el archivo queda con mucho espacio libre.</summary>
    private static async Task<(List<Ticket> Tickets, List<Note> Notes)> FillAsync(TestDevice device, string? password = null)
    {
        await using var database = await OpenAsync(device, password);
        var tickets = new Dictionary<long, Ticket>();
        var notes = new List<Note>();
        var random = new Random(5);
        for (var round = 0; round < 6; round++)
        {
            await using var writer = await database.BeginWriteAsync(Ct);
            for (var i = 0; i < 300; i++)
            {
                var ticket = new Ticket
                {
                    Id = i,
                    Owner = $"owner{random.Next(17)}",
                    Priority = random.Next(5),
                    State = random.Next(2) == 0 ? TicketState.Open : TicketState.Closed,
                    Due = random.Next(3) == 0 ? null : new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddHours(random.Next(1000)),
                    Title = $"Ticket {i}",
                    Body = new string('b', random.Next(20, 400)),
                };
                tickets[i] = ticket;
                await database.Table<Ticket>().UpsertAsync(writer, ticket, Ct);
            }

            if (round == 0)
            {
                for (var i = 0; i < 30; i++)
                {
                    var note = new Note { Slug = $"note-{i:D2}", Body = "body", Attachment = i % 3 == 0 ? new byte[9000] : null };
                    notes.Add(note);
                    await database.Table<Note>().InsertAsync(writer, note, Ct);
                }
            }

            await writer.CommitAsync(Ct);
        }

        return ([.. tickets.Values], notes);
    }

    private static async Task AssertSameAsync(TekiumDb database, List<Ticket> tickets, List<Note> notes)
    {
        var table = database.Table<Ticket>();
        Assert.Equal(tickets.OrderBy(static t => t.Id), await ToListAsync(table.ScanAsync(Ct)));
        var stored = await ToListAsync(database.Table<Note>().ScanAsync(Ct));
        Assert.Equal(notes.Select(static n => (n.Slug, n.Attachment?.Length)), stored.Select(static n => (n.Slug, n.Attachment?.Length)));

        var byOwner = Ticket.Fields.Owner.EqualTo("owner3");
        Assert.Equal(TkAccess.IndexScan, (await table.ExplainAsync(byOwner, Ct)).Access);
        Assert.Equal(
            tickets.Where(static t => t.Owner == "owner3").Select(static t => t.Id).Order(),
            (await ToListAsync(table.QueryAsync(byOwner, Ct))).Select(static t => t.Id).Order());
        var open = Ticket.Fields.Deleted.EqualTo(false).And(Ticket.Fields.State.EqualTo(TicketState.Open));
        Assert.Equal(new TkPlan(TkAccess.IndexScan, "open"), await table.ExplainAsync(open, Ct));
        Assert.Equal(
            tickets.Where(static t => t.IsOpen).Select(static t => t.Id).Order(),
            (await ToListAsync(table.QueryAsync(open, Ct))).Select(static t => t.Id).Order());
    }

    private static async Task<uint> RootOfAsync(TestDevice device, string table)
    {
        await using var database = await OpenAsync(device);
        using var read = database.Pager.BeginRead();
        return (await CatalogStore.FindTableAsync(database.Pager, read.Root, CatalogStore.TableKey(table), Ct))!.Value.Root.Value;
    }

    /// <summary>La primera hoja del árbol de una tabla.</summary>
    private static async Task<uint> FirstLeafAsync(TestDevice device, string table)
    {
        var number = new PageNumber(await RootOfAsync(device, table));
        await using var database = await OpenAsync(device);
        while (true)
        {
            using var page = await database.Pager.PinAsync(number, Ct);
            if (page.Type == PageType.BTreeLeaf)
            {
                return number.Value;
            }

            number = Tekium.Embedded.Indexing.BTreeNode.Child(page.Span, 0);
        }
    }
}

public sealed class CopyTests_File : CopyTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class CopyTests_Memory : CopyTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
