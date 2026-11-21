// Copyright (c) Tekium. Licensed under the MIT License.

using TekiumDB.Storage;
using TekiumDB.Tests.Database;
using TekiumDB.Tests.Storage;

namespace TekiumDB.Tests.Maintenance;

/// <summary>
/// El inspector lee un archivo sin abrirlo y dice qué tiene mal. Criterio de la Fase 10: un archivo
/// corrupto a mano se diagnostica sin abrir la base.
/// </summary>
public abstract class InspectorTests : DatabaseTests
{
    private const int Page = 4096;

    [Fact]
    public async Task A_healthy_database_has_no_findings_and_every_page_is_accounted_for()
    {
        var device = NewDevice();
        var (pageCount, free, freeListPages) = await FillAsync(device);

        var inspection = await TkInspector.InspectAsync(() => device, default, Ct);

        Assert.Empty(inspection.Findings);
        Assert.True(inspection.IsHealthy);
        Assert.True(inspection.PagesChecked);
        Assert.Equal(Page, inspection.PageSize);
        Assert.Equal(2, inspection.FormatVersion);
        Assert.False(inspection.IsEncrypted);
        Assert.Equal(2, inspection.RootSlots.Count);
        Assert.All(inspection.RootSlots, static slot => Assert.True(slot.IsValid));
        Assert.Equal(pageCount, inspection.RootSlots[inspection.CurrentSlot].PageCount);
        Assert.Equal(free.Count, inspection.PagesFree);
        Assert.Equal(pageCount, 2 + inspection.PagesInUse + inspection.PagesFree + freeListPages.Count);
        Assert.Equal(inspection.PagesInUse, inspection.Trees.Sum(static tree => tree.Pages));

        // Cada árbol, con su nombre y lo que tiene.
        Assert.Equal("catalog", inspection.Trees[0].Name);
        Assert.Equal(300, inspection.Trees.Single(static tree => tree.Name == "table tickets").Entries);
        Assert.Equal(300, inspection.Trees.Single(static tree => tree.Name == "index tickets.Owner").Entries);
        Assert.Equal(40, inspection.Trees.Single(static tree => tree.Name == "table notes").Entries);
        Assert.All(inspection.Trees, static tree => Assert.Equal(0, tree.DamagedPages));
    }

    /// <summary>
    /// El criterio de salida: se daña un byte de cada página del archivo, una por una. El inspector nunca
    /// lanza, y siempre nombra la página dañada, salvo que sea una página libre, que no guarda nada.
    /// </summary>
    [Fact]
    public async Task A_byte_damaged_by_hand_in_any_page_is_diagnosed_without_opening_the_database()
    {
        var device = NewDevice();
        var (pageCount, free, _) = await FillAsync(device);
        var opens = 0;
        for (var page = 0; page < pageCount; page++)
        {
            var damaged = await device.CloneAsync();
            await damaged.CorruptAsync(((long)page * Page) + 100);

            var inspection = await TkInspector.InspectAsync(() => damaged, default, Ct);

            if (free.Contains((uint)page))
            {
                Assert.True(inspection.IsHealthy, $"Free page {page}: {string.Join(" | ", inspection.Findings.Select(static f => f.Message))}");
                continue;
            }

            Assert.False(inspection.IsHealthy, $"Damage to page {page} went unnoticed.");
            Assert.Contains(inspection.Findings, finding => finding.Page == page && finding.Severity != TkSeverity.Info);
            if (page >= 2)
            {
                var finding = inspection.Findings.First(finding => finding.Page == page);
                Assert.Equal(TkSeverity.Error, finding.Severity);
                Assert.False(string.IsNullOrEmpty(finding.Where));
                Assert.Contains($"Page {page}", finding.Message, StringComparison.Ordinal);
            }

            // Y de verdad es "sin abrir": la base, abierta normalmente, falla al llegar a esa página.
            opens += await ReadsEverythingAsync(damaged) ? 1 : 0;
        }

        // Casi siempre la aplicación también falla, pero no siempre: una página que no llega a leer (un
        // índice que no consulta, un slot de raíz viejo) puede estar dañada sin que lo note. El inspector sí.
        Assert.InRange(opens, 1, (int)pageCount / 5);
    }

    [Fact]
    public async Task What_is_not_a_database_is_said_to_be_so()
    {
        var empty = NewDevice();
        var inspection = await TkInspector.InspectAsync(() => empty, default, Ct);
        Assert.Equal(TkFindingKind.NotADatabase, Assert.Single(inspection.Findings).Kind);
        Assert.False(inspection.PagesChecked);

        var noise = NewDevice();
        var bytes = new byte[3 * Page];
        new Random(1).NextBytes(bytes);
        await noise.OverwriteAsync(0, bytes);
        inspection = await TkInspector.InspectAsync(() => noise, default, Ct);
        Assert.Equal(TkFindingKind.NotADatabase, Assert.Single(inspection.Findings).Kind);
        Assert.False(inspection.IsHealthy);
    }

    [Fact]
    public async Task A_damaged_first_header_copy_is_read_around_through_the_second()
    {
        var device = NewDevice();
        await FillAsync(device);
        await device.CorruptAsync(3);

        var inspection = await TkInspector.InspectAsync(() => device, default, Ct);

        var finding = Assert.Single(inspection.Findings);
        Assert.Equal((TkSeverity.Error, TkFindingKind.HeaderDamaged, 0L), (finding.Severity, finding.Kind, finding.Page));
        Assert.True(inspection.PagesChecked);
        Assert.Equal(300, inspection.Trees.Single(static tree => tree.Name == "table tickets").Entries);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await OpenAsync(device));
    }

    [Fact]
    public async Task A_damaged_second_header_copy_is_a_warning_because_the_database_still_opens()
    {
        var device = NewDevice();
        await FillAsync(device);
        await device.CorruptAsync(Page + 3);

        var inspection = await TkInspector.InspectAsync(() => device, default, Ct);

        var finding = Assert.Single(inspection.Findings);
        Assert.Equal((TkSeverity.Warning, TkFindingKind.HeaderDamaged, 1L), (finding.Severity, finding.Kind, finding.Page));
        Assert.Equal(300, inspection.Trees.Single(static tree => tree.Name == "table tickets").Entries);
        await using var database = await OpenAsync(device);
    }

    [Fact]
    public async Task A_database_nobody_has_written_to_is_healthy_and_one_commit_later_both_root_slots_count()
    {
        // Recién creada sólo tiene escrito un registro de raíz: el otro no está dañado, nunca existió.
        var device = NewDevice();
        await using (await OpenAsync(device))
        {
        }

        var inspection = await TkInspector.InspectAsync(() => device, default, Ct);
        Assert.Empty(inspection.Findings);
        Assert.Equal([true, false], inspection.RootSlots.Select(static slot => slot.IsValid));
        Assert.Equal(["catalog"], inspection.Trees.Select(static tree => tree.Name));

        // Con un commit ya están los dos: si el primero no pasa su comprobación, es daño.
        await using (var database = await OpenAsync(device))
        {
            await using var writer = await database.BeginWriteAsync(Ct);
            await database.Table<Counter>().InsertAsync(writer, new Counter { Id = 1, Value = 1 }, Ct);
            await writer.CommitAsync(Ct);
        }

        await device.CorruptAsync(FileHeader.SlotOffset(0, PageSize.FromBytes(Page)) + 2);
        inspection = await TkInspector.InspectAsync(() => device, default, Ct);
        var finding = Assert.Single(inspection.Findings);
        Assert.Equal((TkSeverity.Warning, TkFindingKind.RootSlotDamaged, 0L), (finding.Severity, finding.Kind, finding.Page));
    }

    [Fact]
    public async Task A_read_the_device_refuses_is_a_finding_about_that_page_not_a_failure_of_the_inspection()
    {
        var device = NewDevice();
        await FillAsync(device);
        var healthy = await TkInspector.InspectAsync(() => device, default, Ct);
        device.FailOnReadNumber = device.ReadCount + 12;

        var inspection = await TkInspector.InspectAsync(() => device, default, Ct);

        var finding = Assert.Single(inspection.Findings);
        Assert.Equal((TkSeverity.Error, TkFindingKind.PageDamaged), (finding.Severity, finding.Kind));
        Assert.Contains("Injected read failure", finding.Message, StringComparison.Ordinal);
        Assert.True(inspection.PagesInUse < healthy.PagesInUse + 1);
        Assert.True(inspection.Trees.Count >= healthy.Trees.Count - 1);
    }

    [Fact]
    public async Task A_version_this_build_does_not_read_is_reported_as_such()
    {
        var device = NewDevice();
        await FillAsync(device);
        await device.OverwriteAsync(8, new byte[] { 9, 0 });
        await device.OverwriteAsync(Page + 8, new byte[] { 9, 0 });

        var inspection = await TkInspector.InspectAsync(() => device, default, Ct);

        Assert.Equal(TkFindingKind.UnsupportedFormat, Assert.Single(inspection.Findings).Kind);
        Assert.False(inspection.PagesChecked);
    }

    [Fact]
    public async Task With_the_newest_root_slot_damaged_the_older_state_is_inspected_and_the_loss_is_said()
    {
        var device = NewDevice();
        await FillAsync(device);
        var healthy = await TkInspector.InspectAsync(() => device, default, Ct);
        var current = healthy.CurrentSlot;
        await device.CorruptAsync(FileHeader.SlotOffset(current, PageSize.FromBytes(Page)) + 9);

        var inspection = await TkInspector.InspectAsync(() => device, default, Ct);

        Assert.Equal(1 - current, inspection.CurrentSlot);
        Assert.False(inspection.RootSlots[current].IsValid);
        Assert.True(inspection.RootSlots[inspection.CurrentSlot].TransactionId < healthy.RootSlots[current].TransactionId);
        var finding = Assert.Single(inspection.Findings, static finding => finding.Kind == TkFindingKind.RootSlotDamaged);
        Assert.Equal((TkSeverity.Warning, (long?)current), (finding.Severity, finding.Page));
    }

    [Fact]
    public async Task With_both_root_slots_damaged_nothing_can_be_reached_and_that_is_the_finding()
    {
        var device = NewDevice();
        await FillAsync(device);
        await device.CorruptAsync(FileHeader.SlotOffset(0, PageSize.FromBytes(Page)));
        await device.CorruptAsync(FileHeader.SlotOffset(1, PageSize.FromBytes(Page)));

        var inspection = await TkInspector.InspectAsync(() => device, default, Ct);

        Assert.Equal(TkFindingKind.NoValidRootSlot, Assert.Single(inspection.Findings).Kind);
        Assert.Equal(-1, inspection.CurrentSlot);
        Assert.False(inspection.PagesChecked);
        Assert.Empty(inspection.Trees);
    }

    [Fact]
    public async Task A_file_cut_short_is_reported_with_the_pages_that_are_missing()
    {
        var device = NewDevice();
        var (pageCount, _, _) = await FillAsync(device);
        var whole = await device.ReadAllAsync();
        var cut = NewDevice();
        await cut.OverwriteAsync(0, whole.AsMemory(0, (int)(pageCount - 10) * Page));

        var inspection = await TkInspector.InspectAsync(() => cut, default, Ct);

        Assert.Contains(inspection.Findings, static finding => finding.Kind == TkFindingKind.Truncated);
        Assert.Contains(inspection.Findings, static finding => finding.Page >= 2 && finding.Severity == TkSeverity.Error);
        Assert.True(inspection.PagesChecked);

        var stub = NewDevice();
        await stub.OverwriteAsync(0, whole.AsMemory(0, Page + 100));
        inspection = await TkInspector.InspectAsync(() => stub, default, Ct);
        Assert.Equal(TkFindingKind.Truncated, Assert.Single(inspection.Findings).Kind);
    }

    [Fact]
    public async Task An_encrypted_database_is_checked_only_with_its_password()
    {
        var device = NewDevice();
        await FillAsync(device, "the password");

        var without = await TkInspector.InspectAsync(() => device, default, Ct);
        Assert.True(without.IsEncrypted);
        Assert.False(without.PagesChecked);
        Assert.True(without.IsHealthy);
        Assert.Equal((TkSeverity.Info, TkFindingKind.PasswordRequired), (without.Findings[0].Severity, Assert.Single(without.Findings).Kind));

        var wrong = await TkInspector.InspectAsync(() => device, "another".AsMemory(), Ct);
        Assert.Equal(TkFindingKind.WrongPassword, Assert.Single(wrong.Findings).Kind);
        Assert.False(wrong.IsHealthy);

        var right = await TkInspector.InspectAsync(() => device, "the password".AsMemory(), Ct);
        Assert.Empty(right.Findings);
        Assert.Equal(300, right.Trees.Single(static tree => tree.Name == "table tickets").Entries);

        // Un byte cambiado en una página cifrada: la etiqueta de autenticación no cuadra.
        var table = right.RootSlots[right.CurrentSlot].RootPage;
        await device.CorruptAsync((table * Page) + 2000);
        var damaged = await TkInspector.InspectAsync(() => device, "the password".AsMemory(), Ct);
        Assert.Contains(damaged.Findings, finding => finding.Page == table && finding.Kind == TkFindingKind.PageDamaged);
    }

    [Fact]
    public async Task Damage_in_one_tree_does_not_hide_the_others()
    {
        var device = NewDevice();
        await FillAsync(device);
        var healthy = await TkInspector.InspectAsync(() => device, default, Ct);

        // La raíz de la tabla de tickets: se pierde la tabla entera, y nada más.
        uint root;
        await using (var database = await OpenAsync(device))
        {
            using var read = database.Pager.BeginRead();
            var info = await TekiumDB.Catalog.CatalogStore.FindTableAsync(
                database.Pager, read.Root, TekiumDB.Catalog.CatalogStore.TableKey("tickets"), Ct);
            root = info!.Value.Root.Value;
        }

        await device.CorruptAsync(((long)root * Page) + 500);
        var inspection = await TkInspector.InspectAsync(() => device, default, Ct);

        var finding = Assert.Single(inspection.Findings);
        Assert.Equal((TkFindingKind.PageDamaged, (long?)root, "table tickets"), (finding.Kind, finding.Page, finding.Where));
        var tickets = inspection.Trees.Single(static tree => tree.Name == "table tickets");
        Assert.Equal((0L, 1L), (tickets.Entries, tickets.DamagedPages));
        foreach (var tree in healthy.Trees.Where(static tree => tree.Name != "table tickets"))
        {
            Assert.Equal(tree, inspection.Trees.Single(other => other.Name == tree.Name));
        }
    }

    /// <summary>
    /// Daño que la suma de verificación no ve: una página bien sellada con contenido que no puede ser. No lo
    /// produce un disco que falla, sino un programa con un error; el inspector lo encuentra igual.
    /// </summary>
    [Fact]
    public async Task Pages_that_pass_their_check_but_cannot_be_right_are_found_too()
    {
        var device = NewDevice();
        var (pageCount, free, _) = await FillAsync(device);
        uint root;
        uint leaf;
        uint other;
        uint large;
        uint trunk;
        await using (var database = await OpenAsync(device))
        {
            var pager = database.Pager;
            using var read = pager.BeginRead();
            root = (await TekiumDB.Catalog.CatalogStore.FindTableAsync(pager, read.Root, TekiumDB.Catalog.CatalogStore.TableKey("tickets"), Ct))!.Value.Root.Value;
            using (var page = await pager.PinAsync(new PageNumber(root), Ct))
            {
                Assert.Equal(PageType.BTreeInternal, page.Type);
                leaf = TekiumDB.Indexing.BTreeNode.Child(page.Span, 0).Value;
                other = TekiumDB.Indexing.BTreeNode.Child(page.Span, 1).Value;
            }

            // Una página de overflow: la primera de un adjunto grande.
            var notes = (await TekiumDB.Catalog.CatalogStore.FindTableAsync(pager, read.Root, TekiumDB.Catalog.CatalogStore.TableKey("notes"), Ct))!.Value.Root;
            large = 0;
            await foreach (var page in Reachable(pager, notes))
            {
                if (page.Type == PageType.Overflow)
                {
                    large = page.Number;
                    break;
                }
            }

            trunk = pager.FreeList.MetadataPages.First();
        }

        Assert.NotEqual(0u, large);

        // Claves fuera de orden en una hoja.
        var finding = await InspectAfterAsync(device, leaf, static page =>
        {
            var node = page.AsSpan(PageLayout.HeaderSize, page.Length - PageLayout.Overhead);
            var key = TekiumDB.Indexing.BTreeNode.Key(node, 0).ToArray();
            var value = TekiumDB.Indexing.BTreeNode.LeafValue(node, 0).ToArray();
            TekiumDB.Indexing.BTreeNode.RemoveSlot(node, 0);
            TekiumDB.Indexing.BTreeNode.Compact(node);
            Assert.True(TekiumDB.Indexing.BTreeNode.TryInsertLeaf(node, TekiumDB.Indexing.BTreeNode.Count(node), key, value, overflow: false));
        });
        Assert.Equal((TkFindingKind.TreeDamaged, (long?)leaf, "table tickets"), (finding.Kind, finding.Page, finding.Where));

        // Dos hijos cambiados de lugar: cada hoja está bien, pero no donde su padre dice.
        finding = await InspectAfterAsync(device, root, page =>
        {
            var node = page.AsSpan(PageLayout.HeaderSize, page.Length - PageLayout.Overhead);
            TekiumDB.Indexing.BTreeNode.SetChild(node, 0, new PageNumber(other));
            TekiumDB.Indexing.BTreeNode.SetChild(node, 1, new PageNumber(leaf));
        });
        Assert.Equal((TkFindingKind.TreeDamaged, (long?)other), (finding.Kind, finding.Page));
        Assert.Contains("outside the range", finding.Message, StringComparison.Ordinal);

        // Dos hijos que apuntan a la misma página.
        finding = await InspectAfterAsync(device, root, page =>
            TekiumDB.Indexing.BTreeNode.SetChild(page.AsSpan(PageLayout.HeaderSize, page.Length - PageLayout.Overhead), 1, new PageNumber(leaf)));
        Assert.Equal((TkFindingKind.TreeDamaged, (long?)leaf), (finding.Kind, finding.Page));
        Assert.Contains("second time", finding.Message, StringComparison.Ordinal);

        // Un hijo que apunta fuera del archivo.
        finding = await InspectAfterAsync(device, root, page =>
            TekiumDB.Indexing.BTreeNode.SetChild(page.AsSpan(PageLayout.HeaderSize, page.Length - PageLayout.Overhead), 1, new PageNumber(pageCount + 50)));
        Assert.Equal((TkFindingKind.TreeDamaged, (long?)(pageCount + 50)), (finding.Kind, finding.Page));
        Assert.Contains("not part of the database", finding.Message, StringComparison.Ordinal);

        // Una página de árbol marcada como otra cosa, y una de valor grande marcada como hoja.
        finding = await InspectAfterAsync(device, other, static page => page[0] = (byte)PageType.Overflow);
        Assert.Equal((TkFindingKind.PageDamaged, (long?)other), (finding.Kind, finding.Page));
        finding = await InspectAfterAsync(device, large, static page => page[0] = (byte)PageType.BTreeLeaf);
        Assert.Equal((TkFindingKind.PageDamaged, (long?)large, "table notes"), (finding.Kind, finding.Page, finding.Where));
        Assert.Contains("large value", finding.Message, StringComparison.Ordinal);

        // La nota cuyo adjunto no se puede leer no cuenta como leída.
        var withoutIt = await device.CloneAsync();
        await withoutIt.CorruptAsync(((long)large * Page) + 700);
        Assert.Equal(39, (await TkInspector.InspectAsync(() => withoutIt, default, Ct)).Trees.Single(static tree => tree.Name == "table notes").Entries);

        // La lista de libres ofrece una página que está en uso: una escritura posterior la pisaría.
        var inUse = await InspectAfterAsync(device, trunk, page =>
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(PageLayout.HeaderSize + 4), root), all: true);
        Assert.Equal((TkSeverity.Error, TkFindingKind.FreePageInUse, (long?)root), (inUse.Severity, inUse.Kind, inUse.Page));

        // La lista de libres olvida una página: nadie la usa ni la ofrece.
        var lost = await InspectAfterAsync(device, trunk, static page =>
        {
            var count = page.AsSpan(PageLayout.HeaderSize);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(count, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(count) - 1);
        });
        Assert.Equal((TkSeverity.Warning, TkFindingKind.PagesUnaccounted), (lost.Severity, lost.Kind));
        Assert.StartsWith("1 pages", lost.Message, StringComparison.Ordinal);
        Assert.NotEmpty(free);

        static async IAsyncEnumerable<(uint Number, PageType Type)> Reachable(Pager pager, PageNumber root)
        {
            var pending = new Stack<PageNumber>([root]);
            while (pending.TryPop(out var number))
            {
                using var page = await pager.PinAsync(number, Ct);
                yield return (number.Value, page.Type);
                if (page.Type == PageType.BTreeInternal)
                {
                    for (var i = 0; i <= TekiumDB.Indexing.BTreeNode.Count(page.Span); i++)
                    {
                        pending.Push(TekiumDB.Indexing.BTreeNode.Child(page.Span, i));
                    }
                }
                else if (page.Type == PageType.BTreeLeaf)
                {
                    for (var i = 0; i < TekiumDB.Indexing.BTreeNode.Count(page.Span); i++)
                    {
                        if (TekiumDB.Indexing.BTreeNode.IsOverflow(page.Span, i))
                        {
                            TekiumDB.Indexing.BTreeNode.ReadOverflowReference(TekiumDB.Indexing.BTreeNode.LeafValue(page.Span, i), out _, out var first);
                            pending.Push(first);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Cambia una página en una copia del archivo, la vuelve a sellar, e inspecciona. Devuelve el primer hallazgo (o el único).</summary>
    private static async Task<TkFinding> InspectAfterAsync(TestDevice device, uint number, Action<byte[]> change, bool all = false)
    {
        var copy = await device.CloneAsync();
        var bytes = await copy.ReadAllAsync();
        var page = bytes.AsSpan((int)number * Page, Page).ToArray();
        change(page);
        PageLayout.Seal(page);
        await copy.OverwriteAsync((long)number * Page, page);
        var inspection = await TkInspector.InspectAsync(() => copy, default, Ct);
        Assert.False(inspection.IsHealthy);
        return all ? inspection.Findings[0] : inspection.Findings.Count == 1 ? inspection.Findings[0] : inspection.Findings.First(finding => finding.Page == number || finding.Page is null || finding.Kind == TkFindingKind.TreeDamaged);
    }

    /// <summary>Tres tablas, índices, valores grandes y borrados, en varios commits: quedan páginas libres.</summary>
    private static async Task<(uint PageCount, HashSet<uint> Free, HashSet<uint> FreeListPages)> FillAsync(TestDevice device, string? password = null)
    {
        await using var database = await OpenAsync(device, password);
        var tickets = database.Table<Ticket>();
        var notes = database.Table<Note>();
        var random = new Random(3);
        for (var round = 0; round < 4; round++)
        {
            await using var writer = await database.BeginWriteAsync(Ct);
            for (var i = round * 100; i < (round + 1) * 100; i++)
            {
                await tickets.InsertAsync(
                    writer,
                    new Ticket { Id = i, Owner = $"owner{i % 17}", Priority = i % 5, Title = $"Ticket {i}", Body = new string('b', random.Next(20, 400)) },
                    Ct);
            }

            for (var i = round * 10; i < (round + 1) * 10; i++)
            {
                // Uno de cada tres con un adjunto más grande que una página.
                var attachment = i % 3 == 0 ? new byte[9000] : null;
                await notes.InsertAsync(writer, new Note { Slug = $"note-{i}", Body = "body", Attachment = attachment }, Ct);
            }

            for (var i = 0; round == 3 && i < 100; i++)
            {
                await tickets.DeleteAsync(writer, (long)i, Ct);
            }

            await writer.CommitAsync(Ct);
        }

        return (database.Pager.Current.PageCount, [.. database.Pager.FreeList.FreePages], [.. database.Pager.FreeList.MetadataPages]);
    }

    /// <summary>Abre la base como lo hace una aplicación y lee todas las filas. Falso si algo falla.</summary>
    private static async Task<bool> ReadsEverythingAsync(TestDevice device)
    {
        try
        {
            await using var database = await OpenAsync(device);
            _ = await ToListAsync(database.Table<Ticket>().ScanAsync(Ct));
            _ = await ToListAsync(database.Table<Note>().ScanAsync(Ct));
            _ = await ToListAsync(database.Table<Ticket>().QueryAsync(Ticket.Fields.Owner.AtLeast("a"), Ct));
            await using var writer = await database.BeginWriteAsync(Ct);
            await database.Table<Ticket>().InsertAsync(writer, new Ticket { Id = 100_000, Owner = "x" }, Ct);
            await writer.CommitAsync(Ct);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return false;
        }
    }
}

public sealed class InspectorTests_File : InspectorTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class InspectorTests_Memory : InspectorTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
