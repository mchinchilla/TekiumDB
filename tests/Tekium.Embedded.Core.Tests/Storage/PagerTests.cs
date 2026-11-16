// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Tests.Storage;

/// <summary>
/// Las pruebas del pager. Son abstractas respecto al dispositivo: las clases derivadas las corren
/// contra un archivo y contra memoria sin cambiar una línea (criterio de salida de la Fase 1.5).
/// </summary>
public abstract class PagerTests : DeviceBackedTests
{
    private static readonly PagerOptions Options = new() { PageSize = PageSize.Default, CachePages = 64 };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(4096)]
    [InlineData(8192)]
    [InlineData(32768)]
    public async Task Create_then_open_recovers_the_page_size_and_an_empty_snapshot(int pageSize)
    {
        var device = NewDevice();
        await using (await Pager.CreateAsync(device, Options with { PageSize = PageSize.FromBytes(pageSize) }, Ct))
        {
        }

        await using var pager = await Pager.OpenAsync(device, Options, Ct);

        Assert.Equal(pageSize, pager.PageSize.Bytes);
        Assert.True(pager.Current.Root.IsNone);
        Assert.True(pager.Current.FreeList.IsNone);
        Assert.Equal(2u, pager.Current.PageCount);
        Assert.Equal(0ul, pager.Current.TransactionId.Value);
        Assert.Equal(2L * pageSize, device.Length);
    }

    [Fact]
    public async Task Create_refuses_a_device_that_already_has_data()
    {
        var device = NewDevice();
        await device.WriteAsync(0, new byte[1], Ct);

        await Assert.ThrowsAsync<IOException>(async () => await Pager.CreateAsync(device, Options, Ct));
    }

    [Fact]
    public async Task Open_rejects_a_file_that_is_not_a_database()
    {
        var device = NewDevice();
        await device.WriteAsync(0, new byte[16384], Ct);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await Pager.OpenAsync(device, Options, Ct));
    }

    [Fact]
    public async Task Committed_pages_survive_reopening()
    {
        var device = NewDevice();
        PageNumber number;
        await using (var pager = await Pager.CreateAsync(device, Options, Ct))
        {
            number = await CommitOnePageAsync(pager, 0xAB);
        }

        await using var reopened = await Pager.OpenAsync(device, Options, Ct);
        using var page = await reopened.PinAsync(number, Ct);

        Assert.Equal(number, reopened.Current.Root);
        Assert.Equal(3u, reopened.Current.PageCount);
        Assert.Equal(1ul, reopened.Current.TransactionId.Value);
        Assert.Equal(PageType.BTreeLeaf, page.Type);
        Assert.Equal(8192 - 32, page.Span.Length);
        Assert.All(page.Span.ToArray(), b => Assert.Equal(0xAB, b));
    }

    [Fact]
    public async Task Each_commit_flushes_twice_pages_first_then_the_root_slot()
    {
        var device = NewDevice();
        await using var pager = await Pager.CreateAsync(device, Options, Ct);
        var flushesBefore = device.FlushCount;

        await CommitOnePageAsync(pager, 1);

        Assert.Equal(flushesBefore + 2, device.FlushCount);
    }

    [Fact]
    public async Task Consecutive_commits_write_their_root_slots_to_different_pages()
    {
        // Una escritura rota del slot nuevo no puede alcanzar al slot vigente: viven en páginas distintas.
        var first = FileHeader.SlotOffset(0, PageSize.Default);
        var second = FileHeader.SlotOffset(1, PageSize.Default);

        Assert.NotEqual(first / 8192, second / 8192);
        Assert.InRange(first, 0, (2 * 8192) - FileHeader.SlotSize);
        Assert.InRange(second, 0, (2 * 8192) - FileHeader.SlotSize);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_transaction_disposed_without_commit_rolls_back()
    {
        var device = NewDevice();
        await using var pager = await Pager.CreateAsync(device, Options, Ct);
        var before = pager.Current;

        await using (var tx = await pager.BeginWriteAsync(Ct))
        {
            tx.Allocate(PageType.BTreeLeaf, out var number);
            tx.Root = number;
        }

        Assert.Equal(before, pager.Current);
        await using var reopened = await Pager.OpenAsync(device, Options, Ct);
        Assert.Equal(before, reopened.Current);
    }

    [Fact]
    public async Task A_commit_that_fails_before_the_root_slot_leaves_the_previous_state()
    {
        var device = NewDevice();
        await using var pager = await Pager.CreateAsync(device, Options, Ct);
        var first = await CommitOnePageAsync(pager, 0x11);
        var committed = pager.Current;

        // Escritura 1 del commit: las páginas. Escritura 2: el slot de raíz. Falla la segunda.
        device.FailOnWriteNumber = device.WriteCount + 2;
        await using (var tx = await pager.BeginWriteAsync(Ct))
        {
            tx.Allocate(PageType.BTreeLeaf, out var number).Span.Fill(0x22);
            tx.Root = number;
            await Assert.ThrowsAsync<IOException>(async () => await tx.CommitAsync(Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await tx.CommitAsync(Ct));
        }

        Assert.Equal(committed, pager.Current);
        await using var reopened = await Pager.OpenAsync(device, Options, Ct);
        Assert.Equal(committed, reopened.Current);
        using var page = await reopened.PinAsync(first, Ct);
        Assert.Equal(0x11, page.Span[0]);
    }

    [Fact]
    public async Task The_writer_keeps_working_after_a_failed_commit()
    {
        var device = NewDevice();
        await using var pager = await Pager.CreateAsync(device, Options, Ct);
        device.FailOnWriteNumber = device.WriteCount + 1;
        await using (var tx = await pager.BeginWriteAsync(Ct))
        {
            tx.Allocate(PageType.BTreeLeaf, out var number);
            tx.Root = number;
            await Assert.ThrowsAsync<IOException>(async () => await tx.CommitAsync(Ct));
        }

        var root = await CommitOnePageAsync(pager, 0x33);

        await using var reopened = await Pager.OpenAsync(device, Options, Ct);
        Assert.Equal(root, reopened.Current.Root);
        using var page = await reopened.PinAsync(root, Ct);
        Assert.Equal(0x33, page.Span[0]);
    }

    [Fact]
    public async Task A_corrupt_newest_root_slot_falls_back_to_the_previous_commit()
    {
        var device = NewDevice();
        Snapshot previous;
        PageNumber previousRoot;
        await using (var pager = await Pager.CreateAsync(device, Options, Ct))
        {
            previousRoot = await CommitOnePageAsync(pager, 0x11);
            previous = pager.Current;
            await CommitOnePageAsync(pager, 0x22);
            await device.CorruptAsync(FileHeader.SlotOffset(pager.CurrentSlot, PageSize.Default) + 3);
        }

        await using var reopened = await Pager.OpenAsync(device, Options, Ct);

        Assert.Equal(previous, reopened.Current);
        using var page = await reopened.PinAsync(previousRoot, Ct);
        Assert.Equal(0x11, page.Span[0]);
    }

    [Fact]
    public async Task Open_fails_cleanly_when_no_root_slot_is_valid()
    {
        var device = NewDevice();
        await using (var pager = await Pager.CreateAsync(device, Options, Ct))
        {
            await CommitOnePageAsync(pager, 0x11);
        }

        await device.CorruptAsync(FileHeader.SlotOffset(0, PageSize.Default));
        await device.CorruptAsync(FileHeader.SlotOffset(1, PageSize.Default));

        await Assert.ThrowsAsync<InvalidDataException>(async () => await Pager.OpenAsync(device, Options, Ct));
    }

    [Fact]
    public async Task A_page_damaged_on_disk_is_reported_by_number_instead_of_returned()
    {
        var device = NewDevice();
        PageNumber number;
        await using (var pager = await Pager.CreateAsync(device, Options, Ct))
        {
            number = await CommitOnePageAsync(pager, 0x11);
        }

        await device.CorruptAsync((number.Value * 8192L) + 1000, mask: 0x01);
        await using var reopened = await Pager.OpenAsync(device, Options, Ct);

        var error = await Assert.ThrowsAsync<InvalidDataException>(async () => await reopened.PinAsync(number, Ct));
        Assert.Contains(number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);

        // El fallo no deja el marco tomado: reparar el byte permite leer la página.
        await device.CorruptAsync((number.Value * 8192L) + 1000, mask: 0x01);
        using var page = await reopened.PinAsync(number, Ct);
        Assert.Equal(0x11, page.Span[0]);
    }

    [Fact]
    public async Task A_transaction_id_is_never_issued_twice_even_after_failures_and_rollbacks()
    {
        var device = NewDevice();
        await using var pager = await Pager.CreateAsync(device, Options, Ct);
        var seen = new HashSet<ulong>();

        for (var i = 0; i < 12; i++)
        {
            await using var tx = await pager.BeginWriteAsync(Ct);
            Assert.True(seen.Add(tx.Id.Value), "A transaction id was issued twice.");
            Assert.True(tx.Id.Value > pager.Current.TransactionId.Value);
            tx.Allocate(PageType.BTreeLeaf, out var number);
            tx.Root = number;
            if (i % 3 == 0)
            {
                device.FailOnWriteNumber = device.WriteCount + 2;
                await Assert.ThrowsAsync<IOException>(async () => await tx.CommitAsync(Ct));
            }
            else if (i % 3 == 1)
            {
                await tx.CommitAsync(Ct);
                Assert.Equal(tx.Id, pager.Current.TransactionId);
            }
        }
    }

    [Fact]
    public async Task A_second_writer_waits_until_the_first_one_finishes()
    {
        var device = NewDevice();
        await using var pager = await Pager.CreateAsync(device, Options, Ct);
        var first = await pager.BeginWriteAsync(Ct);

        var second = pager.BeginWriteAsync(Ct).AsTask();
        Assert.False(second.IsCompleted);

        await first.DisposeAsync();
        await using var tx = await second;
        Assert.NotNull(tx);
    }

    [Fact]
    public async Task Reading_a_page_outside_the_database_is_reported_as_corruption()
    {
        var device = NewDevice();
        await using var pager = await Pager.CreateAsync(device, Options, Ct);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await pager.PinAsync(new PageNumber(99), Ct));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await pager.PinAsync(new PageNumber(1), Ct));
    }

    [Fact]
    public async Task A_finished_transaction_cannot_be_used_again()
    {
        var device = NewDevice();
        await using var pager = await Pager.CreateAsync(device, Options, Ct);
        var tx = await pager.BeginWriteAsync(Ct);
        await tx.CommitAsync(Ct);

        Assert.Throws<InvalidOperationException>(() => tx.Allocate(PageType.BTreeLeaf, out _));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await tx.CommitAsync(Ct));
        await tx.DisposeAsync();
    }

    [Fact]
    public async Task Pages_written_in_one_transaction_are_read_back_through_a_cache_far_smaller_than_the_file()
    {
        await WriteAndReadBackAsync(NewDevice(), PageSize.FromBytes(4096), pages: 20_000, cachePages: 128);
    }

    [Fact]
    public async Task A_cached_page_is_read_without_allocating()
    {
        var device = NewDevice();
        await using var pager = await Pager.CreateAsync(device, Options, Ct);
        var number = await CommitOnePageAsync(pager, 0x44);
        long sum = 0;
        for (var i = 0; i < 100; i++)
        {
            sum += ReadOnce(pager, number);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            sum += ReadOnce(pager, number);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(10_100 * 0x44, sum);

        static int ReadOnce(Pager pager, PageNumber number)
        {
            using var read = pager.BeginRead();
            if (!pager.TryPin(number, out var page))
            {
                return -1;
            }

            using (page)
            {
                return page.Span[100];
            }
        }
    }

    internal static async Task WriteAndReadBackAsync(IStorageDevice device, PageSize pageSize, int pages, int cachePages)
    {
        var options = new PagerOptions { PageSize = pageSize, CachePages = cachePages };
        var numbers = new PageNumber[pages];
        var pager = await Pager.CreateAsync(device, options, Ct);
        await using (pager)
        {
            for (var start = 0; start < pages; start += 10_000)
            {
                await using var tx = await pager.BeginWriteAsync(Ct);
                for (var i = start; i < Math.Min(start + 10_000, pages); i++)
                {
                    var payload = tx.Allocate(PageType.BTreeLeaf, out numbers[i]).Span;
                    payload.Fill((byte)(i * 31));
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload, i);
                }

                tx.Root = numbers[start];
                await tx.CommitAsync(Ct);
            }

            Assert.Equal((uint)pages + 2, pager.Current.PageCount);

            // Orden pseudoaleatorio: con una caché mucho menor que el archivo, casi todo es fallo y desalojo.
            for (var k = 0; k < pages; k++)
            {
                var i = (int)((k * 7919L) % pages);
                using var page = await pager.PinAsync(numbers[i], Ct);
                var span = page.Span;
                if (System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(span) != i
                    || span[4] != (byte)(i * 31)
                    || span[^1] != (byte)(i * 31))
                {
                    Assert.Fail($"Page {numbers[i].Value} does not hold what was written to it.");
                }
            }
        }
    }

    private static async Task<PageNumber> CommitOnePageAsync(Pager pager, byte fill)
    {
        await using var tx = await pager.BeginWriteAsync(Ct);
        tx.Allocate(PageType.BTreeLeaf, out var number).Span.Fill(fill);
        tx.Root = number;
        await tx.CommitAsync(Ct);
        return number;
    }
}

public sealed class PagerTests_File : PagerTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File();
}

public sealed class PagerTests_Memory : PagerTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}

public sealed class PagerScaleTests
{
    /// <summary>
    /// Criterio de salida de la Fase 1: un millón de páginas escritas y leídas en un archivo real (4 GiB).
    /// Explícita por tamaño y duración: <c>dotnet test --filter-method "*million*" -explicit on</c>.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task One_million_pages_are_written_and_read_back_from_a_real_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tekium-million-{Guid.NewGuid():N}.tkdb");
        try
        {
            await PagerTests.WriteAndReadBackAsync(
                FileStorageDevice.Open(path, FileMode.CreateNew), PageSize.FromBytes(4096), pages: 1_000_000, cachePages: 4096);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
