// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using TekiumDB.Catalog;
using TekiumDB.Indexing;
using TekiumDB.Storage;
using TekiumDB.Tests.Database;
using TekiumDB.Tests.Storage;

namespace TekiumDB.Tests.Maintenance;

/// <summary>
/// <c>VerifyAsync</c> comprueba una base abierta: cada página en uso, tal como está en el disco, la forma
/// de cada árbol y la lista de libres. Criterio de la Fase 10.4: una página alterada a mano se detecta y
/// se nombra.
/// </summary>
public abstract class VerifyTests : DatabaseTests
{
    private const int Page = 4096;

    [Fact]
    public async Task A_healthy_open_database_verifies_clean_and_every_page_is_accounted_for()
    {
        await using var database = await OpenAsync(NewDevice());
        await FillAsync(database);

        var verification = await database.VerifyAsync(Ct);

        Assert.Empty(verification.Findings);
        Assert.True(verification.IsHealthy);
        Assert.Equal(database.LastTransactionId, verification.TransactionId);
        Assert.Equal(database.Pager.FreePageCount, verification.PagesFree);
        Assert.Equal(
            database.Pager.Current.PageCount,
            2 + verification.PagesInUse + verification.PagesFree + database.Pager.FreeListPageCount);
        var tickets = verification.Trees.Single(static tree => tree.Name == "table tickets");
        Assert.Equal((300L, 2), (tickets.Entries, tickets.Depth));
        Assert.Equal(1, verification.Trees.Single(static tree => tree.Name == "catalog").Depth);
        Assert.All(verification.Trees, static tree => Assert.Equal(0, tree.DamagedPages));
    }

    /// <summary>
    /// El criterio de salida: con la base abierta, se altera un byte de cada página en uso, una por una.
    /// La verificación la nombra aunque la página esté en la caché y la aplicación siga leyéndola sin notar nada.
    /// </summary>
    [Fact]
    public async Task A_page_altered_by_hand_under_an_open_database_is_detected_and_named()
    {
        var device = NewDevice();
        await using var database = await OpenAsync(device);
        await FillAsync(database);
        var free = database.Pager.FreeList.FreePages.ToHashSet();
        var pageCount = database.Pager.Current.PageCount;

        // Todo queda en la caché: la aplicación no vuelve a leer del disco.
        _ = await ToListAsync(database.Table<Ticket>().ScanAsync(Ct));
        for (var page = 2u; page < pageCount; page++)
        {
            var offset = ((long)page * Page) + 100;
            await device.CorruptAsync(offset);
            var verification = await database.VerifyAsync(Ct);
            await device.CorruptAsync(offset);

            if (free.Contains(page))
            {
                Assert.True(verification.IsHealthy, $"Free page {page} was reported.");
                continue;
            }

            var finding = Assert.Single(verification.Findings);
            Assert.Equal((TkSeverity.Error, (long?)page), (finding.Severity, finding.Page));
            Assert.False(string.IsNullOrEmpty(finding.Where));
            Assert.Contains($"Page {page}", finding.Message, StringComparison.Ordinal);
        }

        // Restaurado el último byte, vuelve a estar sana; y la aplicación nunca dejó de poder leer.
        Assert.True((await database.VerifyAsync(Ct)).IsHealthy);
        Assert.Equal(300, (await ToListAsync(database.Table<Ticket>().ScanAsync(Ct))).Count);
    }

    [Fact]
    public async Task An_encrypted_page_altered_by_hand_fails_its_authentication()
    {
        var device = NewDevice();
        await using var database = await OpenAsync(device, "the password");
        await FillAsync(database);
        var root = await RootAsync(database, "tickets");

        await device.CorruptAsync(((long)root * Page) + 2000);
        var verification = await database.VerifyAsync(Ct);

        var finding = Assert.Single(verification.Findings);
        Assert.Equal((TkFindingKind.PageDamaged, (long?)root, "table tickets"), (finding.Kind, finding.Page, finding.Where));
    }

    [Fact]
    public async Task What_the_checksum_cannot_see_is_found_too_free_pages_in_use_and_empty_leaves()
    {
        var device = NewDevice();
        await using var database = await OpenAsync(device);
        await FillAsync(database);
        var root = await RootAsync(database, "tickets");
        var trunk = database.Pager.FreeList.MetadataPages.First();
        uint leaf;
        using (var page = await database.Pager.PinAsync(new PageNumber(root), Ct))
        {
            leaf = BTreeNode.Child(page.Span, 1).Value;
        }

        // La lista de libres ofrece una página en uso.
        var original = await RewriteAsync(device, trunk, page => BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(PageLayout.HeaderSize + 4), root));
        var finding = (await database.VerifyAsync(Ct)).Findings[0];
        Assert.Equal((TkSeverity.Error, TkFindingKind.FreePageInUse, (long?)root), (finding.Severity, finding.Kind, finding.Page));
        await device.OverwriteAsync((long)trunk * Page, original);

        // Una hoja vacía con hermanas: debió unirse a una de ellas.
        original = await RewriteAsync(device, leaf, static page => BTreeNode.Initialize(page.AsSpan(PageLayout.HeaderSize, page.Length - PageLayout.Overhead), leaf: true));
        finding = Assert.Single((await database.VerifyAsync(Ct)).Findings);
        Assert.Equal((TkFindingKind.TreeDamaged, (long?)leaf, "table tickets"), (finding.Kind, finding.Page, finding.Where));
        Assert.Contains("empty", finding.Message, StringComparison.Ordinal);
        await device.OverwriteAsync((long)leaf * Page, original);

        Assert.True((await database.VerifyAsync(Ct)).IsHealthy);
    }

    [Fact]
    public async Task Verifying_while_a_writer_commits_without_pause_never_reports_anything_and_leaves_the_cache_alone()
    {
        var device = NewDevice();
        await using var database = await OpenAsync(device);
        await FillAsync(database);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var commits = 0;
        var writing = Task.Run(
            async () =>
            {
                for (var round = 0; !stop.IsCancellationRequested; round++)
                {
                    await using var writer = await database.BeginWriteAsync(Ct);
                    for (var i = 0; i < 300; i += 3)
                    {
                        await database.Table<Ticket>().UpsertAsync(writer, new Ticket { Id = i, Owner = $"owner{round % 9}", Title = $"Ticket {i}", Body = new string('c', 100 + (round % 200)) }, Ct);
                    }

                    await writer.CommitAsync(Ct);
                    Interlocked.Increment(ref commits);
                }
            },
            Ct);
        try
        {
            for (var i = 0; i < 20 || Volatile.Read(ref commits) < 10; i++)
            {
                var verification = await database.VerifyAsync(Ct);
                Assert.True(verification.IsHealthy, string.Join(" | ", verification.Findings.Select(static f => f.Message)));
                Assert.Equal(300, verification.Trees.Single(static tree => tree.Name == "table tickets").Entries);
            }
        }
        finally
        {
            await stop.CancelAsync();
            await writing;
        }

        // Y no pasa por la caché: lo que estaba caliente sigue caliente.
        _ = await ToListAsync(database.Table<Ticket>().QueryAsync(Ticket.Fields.Id.AtMost(20), Ct));
        var reads = device.ReadCount;
        await database.VerifyAsync(Ct);
        var verified = device.ReadCount;
        _ = await ToListAsync(database.Table<Ticket>().QueryAsync(Ticket.Fields.Id.AtMost(20), Ct));
        Assert.True(verified > reads);
        Assert.Equal(verified, device.ReadCount);
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task What_is_verified_is_one_commit_whatever_is_written_once_the_verification_has_started()
    {
        await using var database = await OpenAsync(NewDevice());
        await FillAsync(database);
        var before = database.LastTransactionId;

        // Con el estado ya fijado, se borra casi todo y se reescribe el resto varias veces: sus páginas no se reutilizan.
        var verification = await database.VerifyAsync(
            async () =>
            {
                for (var round = 0; round < 6; round++)
                {
                    await using var writer = await database.BeginWriteAsync(Ct);
                    for (var i = 0; i < 300; i++)
                    {
                        if (round == 0 && i >= 20)
                        {
                            await database.Table<Ticket>().DeleteAsync(writer, (long)i, Ct);
                        }
                        else if (i < 20)
                        {
                            await database.Table<Ticket>().UpsertAsync(writer, new Ticket { Id = i, Owner = "other", Title = $"Round {round}", Body = new string('z', 900) }, Ct);
                        }
                    }

                    await writer.CommitAsync(Ct);
                }
            },
            Ct);

        Assert.Empty(verification.Findings);
        Assert.Equal(before, verification.TransactionId);
        Assert.Equal(300, verification.Trees.Single(static tree => tree.Name == "table tickets").Entries);
        Assert.Equal(20, (await database.VerifyAsync(Ct)).Trees.Single(static tree => tree.Name == "table tickets").Entries);
        await AssertNoLeaksAsync(database);
    }

    private static async Task FillAsync(TekiumDb database)
    {
        for (var round = 0; round < 3; round++)
        {
            await using var writer = await database.BeginWriteAsync(Ct);
            for (var i = 0; i < 300; i++)
            {
                await database.Table<Ticket>().UpsertAsync(
                    writer, new Ticket { Id = i, Owner = $"owner{i % 17}", Priority = round, Title = $"Ticket {i}", Body = new string('b', 200) }, Ct);
            }

            for (var i = 0; round == 0 && i < 12; i++)
            {
                await database.Table<Note>().InsertAsync(writer, new Note { Slug = $"note-{i}", Body = "body", Attachment = i % 3 == 0 ? new byte[9000] : null }, Ct);
            }

            await writer.CommitAsync(Ct);
        }
    }

    private static async Task<uint> RootAsync(TekiumDb database, string table)
    {
        using var read = database.Pager.BeginRead();
        return (await CatalogStore.FindTableAsync(database.Pager, read.Root, CatalogStore.TableKey(table), Ct))!.Value.Root.Value;
    }

    /// <summary>Cambia una página en el disco y la vuelve a sellar. Devuelve cómo estaba.</summary>
    private static async Task<byte[]> RewriteAsync(TestDevice device, uint number, Action<byte[]> change)
    {
        var original = (await device.ReadAllAsync()).AsSpan((int)number * Page, Page).ToArray();
        var page = original.ToArray();
        change(page);
        PageLayout.Seal(page);
        await device.OverwriteAsync((long)number * Page, page);
        return original;
    }
}

public sealed class VerifyTests_File : VerifyTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class VerifyTests_Memory : VerifyTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
