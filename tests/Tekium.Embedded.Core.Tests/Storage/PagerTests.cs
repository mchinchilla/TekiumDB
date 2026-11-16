// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Tests.Storage;

public sealed class PagerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(4096)]
    [InlineData(8192)]
    [InlineData(32768)]
    public async Task Create_then_open_recovers_the_page_size_and_an_empty_snapshot(int pageSize)
    {
        var device = new InMemoryStorageDevice();
        await using (await Pager.CreateAsync(device, PageSize.FromBytes(pageSize), Ct))
        {
        }

        await using var pager = await Pager.OpenAsync(device, Ct);

        Assert.Equal(pageSize, pager.PageSize.Bytes);
        Assert.True(pager.Current.Root.IsNone);
        Assert.Equal(1u, pager.Current.PageCount);
        Assert.Equal(0ul, pager.Current.TransactionId.Value);
    }

    [Fact]
    public async Task Create_refuses_a_device_that_already_has_data()
    {
        var device = new InMemoryStorageDevice();
        await device.WriteAsync(0, new byte[1], Ct);

        await Assert.ThrowsAsync<IOException>(async () => await Pager.CreateAsync(device, PageSize.Default, Ct));
    }

    [Fact]
    public async Task Open_rejects_a_file_that_is_not_a_database()
    {
        var device = new InMemoryStorageDevice();
        await device.WriteAsync(0, new byte[8192], Ct);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await Pager.OpenAsync(device, Ct));
    }

    [Fact]
    public async Task Committed_pages_survive_reopening()
    {
        var device = new InMemoryStorageDevice();
        PageNumber number;
        await using (var pager = await Pager.CreateAsync(device, PageSize.Default, Ct))
        {
            await using var tx = await pager.BeginWriteAsync(Ct);
            var page = tx.Allocate(out number);
            page.AsSpan().Fill(0xAB);
            tx.Root = number;
            await tx.CommitAsync(Ct);
        }

        await using var reopened = await Pager.OpenAsync(device, Ct);
        var read = await reopened.GetPageAsync(number, Ct);

        Assert.Equal(number, reopened.Current.Root);
        Assert.Equal(2u, reopened.Current.PageCount);
        Assert.Equal(1ul, reopened.Current.TransactionId.Value);
        Assert.Equal(8192, read.Length);
        Assert.All(read, b => Assert.Equal(0xAB, b));
    }

    [Fact]
    public async Task Each_commit_flushes_twice_pages_first_then_the_root_slot()
    {
        var device = new InMemoryStorageDevice();
        await using var pager = await Pager.CreateAsync(device, PageSize.Default, Ct);
        var flushesBefore = device.FlushCount;

        await using (var tx = await pager.BeginWriteAsync(Ct))
        {
            tx.Allocate(out var number);
            tx.Root = number;
            await tx.CommitAsync(Ct);
        }

        Assert.Equal(flushesBefore + 2, device.FlushCount);
    }

    [Fact]
    public async Task A_transaction_disposed_without_commit_rolls_back()
    {
        var device = new InMemoryStorageDevice();
        await using var pager = await Pager.CreateAsync(device, PageSize.Default, Ct);
        var before = pager.Current;

        await using (var tx = await pager.BeginWriteAsync(Ct))
        {
            tx.Allocate(out var number);
            tx.Root = number;
        }

        Assert.Same(before, pager.Current);
        await using var reopened = await Pager.OpenAsync(device, Ct);
        Assert.Equal(before, reopened.Current);
    }

    [Fact]
    public async Task A_commit_that_fails_before_the_root_slot_leaves_the_previous_state()
    {
        var device = new InMemoryStorageDevice();
        await using var pager = await Pager.CreateAsync(device, PageSize.Default, Ct);
        var first = await CommitOnePageAsync(pager, 0x11);
        var committed = pager.Current;

        // Escritura 1 del commit: las páginas. Escritura 2: el slot de raíz. Falla la segunda.
        device.FailOnWriteNumber = device.WriteCount + 2;
        await using (var tx = await pager.BeginWriteAsync(Ct))
        {
            var page = tx.Allocate(out var number);
            page.AsSpan().Fill(0x22);
            tx.Root = number;
            await Assert.ThrowsAsync<IOException>(async () => await tx.CommitAsync(Ct));
        }

        Assert.Same(committed, pager.Current);
        await using var reopened = await Pager.OpenAsync(device, Ct);
        Assert.Equal(committed, reopened.Current);
        Assert.Equal(0x11, (await reopened.GetPageAsync(first, Ct))[0]);
    }

    [Fact]
    public async Task The_writer_keeps_working_after_a_failed_commit()
    {
        var device = new InMemoryStorageDevice();
        await using var pager = await Pager.CreateAsync(device, PageSize.Default, Ct);
        device.FailOnWriteNumber = device.WriteCount + 1;
        await using (var tx = await pager.BeginWriteAsync(Ct))
        {
            tx.Allocate(out var number);
            tx.Root = number;
            await Assert.ThrowsAsync<IOException>(async () => await tx.CommitAsync(Ct));
        }

        var root = await CommitOnePageAsync(pager, 0x33);

        await using var reopened = await Pager.OpenAsync(device, Ct);
        Assert.Equal(root, reopened.Current.Root);
        Assert.Equal(0x33, (await reopened.GetPageAsync(root, Ct))[0]);
    }

    [Fact]
    public async Task A_corrupt_newest_root_slot_falls_back_to_the_previous_commit()
    {
        var device = new InMemoryStorageDevice();
        Snapshot previous;
        await using (var pager = await Pager.CreateAsync(device, PageSize.Default, Ct))
        {
            await CommitOnePageAsync(pager, 0x11);
            previous = pager.Current;
            await CommitOnePageAsync(pager, 0x22);
            var newest = pager.Current;
            device.RawBytes[(int)FileHeader.SlotOffset(newest.TransactionId) + 3] ^= 0xFF;
        }

        await using var reopened = await Pager.OpenAsync(device, Ct);

        Assert.Equal(previous, reopened.Current);
    }

    [Fact]
    public async Task Open_fails_cleanly_when_no_root_slot_is_valid()
    {
        var device = new InMemoryStorageDevice();
        await using (var pager = await Pager.CreateAsync(device, PageSize.Default, Ct))
        {
            await CommitOnePageAsync(pager, 0x11);
        }

        device.RawBytes[(int)FileHeader.SlotOffset(new TransactionId(0))] ^= 0xFF;
        device.RawBytes[(int)FileHeader.SlotOffset(new TransactionId(1))] ^= 0xFF;

        await Assert.ThrowsAsync<InvalidDataException>(async () => await Pager.OpenAsync(device, Ct));
    }

    [Fact]
    public async Task A_second_writer_waits_until_the_first_one_finishes()
    {
        var device = new InMemoryStorageDevice();
        await using var pager = await Pager.CreateAsync(device, PageSize.Default, Ct);
        var first = await pager.BeginWriteAsync(Ct);

        var second = pager.BeginWriteAsync(Ct).AsTask();
        Assert.False(second.IsCompleted);

        await first.DisposeAsync();
        await using var tx = await second;
        Assert.NotNull(tx);
    }

    [Fact]
    public async Task A_snapshot_taken_before_a_commit_still_reads_its_own_pages()
    {
        var device = new InMemoryStorageDevice();
        await using var pager = await Pager.CreateAsync(device, PageSize.Default, Ct);
        var oldRoot = await CommitOnePageAsync(pager, 0x11);
        var old = pager.Current;

        await CommitOnePageAsync(pager, 0x22);

        Assert.NotEqual(old.Root, pager.Current.Root);
        Assert.Equal(0x11, (await pager.GetPageAsync(oldRoot, Ct))[0]);
    }

    [Fact]
    public async Task Reading_a_page_beyond_the_committed_count_is_reported_as_corruption()
    {
        var device = new InMemoryStorageDevice();
        await using var pager = await Pager.CreateAsync(device, PageSize.Default, Ct);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await pager.GetPageAsync(new PageNumber(99), Ct));
    }

    [Fact]
    public async Task A_finished_transaction_cannot_be_used_again()
    {
        var device = new InMemoryStorageDevice();
        await using var pager = await Pager.CreateAsync(device, PageSize.Default, Ct);
        var tx = await pager.BeginWriteAsync(Ct);
        await tx.CommitAsync(Ct);

        Assert.Throws<InvalidOperationException>(() => tx.Allocate(out _));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await tx.CommitAsync(Ct));
        await tx.DisposeAsync();
    }

    private static async Task<PageNumber> CommitOnePageAsync(Pager pager, byte fill)
    {
        await using var tx = await pager.BeginWriteAsync(Ct);
        var page = tx.Allocate(out var number);
        page.AsSpan().Fill(fill);
        tx.Root = number;
        await tx.CommitAsync(Ct);
        return number;
    }
}
