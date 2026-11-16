// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.CrashChild;
using Tekium.Embedded.Storage;
using Tekium.Embedded.Tests.Indexing;
using Tekium.Embedded.Tests.Storage;

namespace Tekium.Embedded.Tests.Transactions;

/// <summary>
/// Qué queda cuando un commit no termina bien sin que el proceso muera: el llamador sigue ahí y va a
/// seguir usando la base, así que lo que el motor cree y lo que hay en el disco no pueden separarse.
/// </summary>
public abstract class CommitOutcomeTests : DeviceBackedTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_commit_whose_durability_flush_fails_stops_the_writer_until_the_database_is_reopened()
    {
        var workload = new CrashWorkload(seed: 5, transactions: 6, password: null);
        var device = NewDevice();
        var acknowledged = 0;
        await using (var pager = await workload.CreateAsync(device, Ct))
        {
            // El primer flush del commit 4 pone a salvo las páginas; el segundo, el slot de raíz. Falla ése:
            // el slot ya se escribió y nadie sabe si llegó al disco.
            device.FailOnFlushNumber = device.FlushCount + (3 * 2) + 2;
            var failure = await Assert.ThrowsAsync<IOException>(
                async () => await workload.RunAsync(pager, 0, count => acknowledged = count, Ct));
            Assert.Contains("reopen", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(3, acknowledged);

            // Los lectores siguen en el estado anterior, que está intacto.
            Assert.Equal(3, await workload.MatchAsync(pager, Ct));

            // Seguir escribiendo desde ese estado pisaría páginas que el slot dudoso puede estar alcanzando.
            var refused = await Assert.ThrowsAsync<IOException>(async () => await pager.BeginWriteAsync(Ct));
            Assert.Contains("reopen", refused.Message, StringComparison.OrdinalIgnoreCase);
        }

        await CrashRecovery.AssertRecoversAsync(workload, device, acknowledged, "After a failed durability flush", Ct);
    }

    [Fact]
    public async Task A_commit_whose_root_slot_write_fails_stops_the_writer_too()
    {
        var device = NewDevice();
        await using var pager = await Pager.CreateAsync(device, new PagerOptions(), Ct);

        // Escritura 1 del commit: la página. Escritura 2: el slot de raíz.
        device.FailOnWriteNumber = device.WriteCount + 2;
        await using (var writer = await pager.BeginWriteAsync(Ct))
        {
            writer.Allocate(PageType.BTreeLeaf, out var number);
            writer.Root = number;
            await Assert.ThrowsAsync<IOException>(async () => await writer.CommitAsync(Ct));
        }

        await Assert.ThrowsAsync<IOException>(async () => await pager.BeginWriteAsync(Ct));
    }

    [Fact]
    public async Task Cancelling_once_the_pages_are_safe_does_not_leave_the_commit_half_published()
    {
        var workload = new CrashWorkload(seed: 5, transactions: 3, password: null);
        var device = NewDevice();
        await using var pager = await workload.CreateAsync(device, Ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        // La cancelación llega justo cuando el commit 2 va a escribir su slot de raíz, en las páginas meta.
        var metaBytes = workload.Options.PageSize.Bytes * (long)FileHeader.MetaPages;
        var commits = 0;
        device.OnWrite = (offset, _) =>
        {
            if (offset < metaBytes && ++commits == 2)
            {
                cancellation.Cancel();
            }
        };

        var acknowledged = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await workload.RunAsync(pager, 0, count => acknowledged = count, cancellation.Token));

        // El commit 2 terminó: la cancelación se observó después, al empezar la transacción siguiente.
        Assert.Equal(2, acknowledged);
        Assert.Equal(2, await workload.MatchAsync(pager, Ct));
        device.OnWrite = null;
        await workload.RunAsync(pager, 2, null, Ct);
        Assert.Equal(3, await workload.MatchAsync(pager, Ct));
    }

    [Fact]
    public async Task Cancelling_while_the_pages_are_being_written_rolls_back_and_the_writer_goes_on()
    {
        var workload = new CrashWorkload(seed: 5, transactions: 3, password: null);
        var device = NewDevice();
        await using var pager = await workload.CreateAsync(device, Ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await workload.RunAsync(pager, 0, null, Ct);
        var metaBytes = workload.Options.PageSize.Bytes * (long)FileHeader.MetaPages;
        device.OnWrite = (offset, _) =>
        {
            if (offset >= metaBytes)
            {
                cancellation.Cancel();
            }
        };

        var extra = new CrashWorkload(seed: 5, transactions: 5, password: null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await extra.RunAsync(pager, 3, null, cancellation.Token));

        Assert.Equal(3, await extra.MatchAsync(pager, Ct));
        device.OnWrite = null;
        await extra.RunAsync(pager, 3, null, Ct);
        Assert.Equal(5, await extra.MatchAsync(pager, Ct));
        await BTreeVerifier.AssertNoLeaksAsync(pager, Ct);
    }

    [Fact]
    public async Task A_full_disk_fails_the_commit_cleanly_and_the_writer_goes_on_when_there_is_room()
    {
        var workload = new CrashWorkload(seed: 9, transactions: 12, password: null);
        var device = NewDevice();
        await using (var pager = await workload.CreateAsync(device, Ct))
        {
            var acknowledged = 0;
            device.DiskFull = true;
            await Assert.ThrowsAsync<IOException>(
                async () => await workload.RunAsync(pager, 0, count => acknowledged = count, Ct));
            Assert.True(acknowledged < workload.Transactions);
            Assert.Equal(acknowledged, await workload.MatchAsync(pager, Ct));

            device.DiskFull = false;
            await workload.RunAsync(pager, acknowledged, null, Ct);
            Assert.Equal(workload.Transactions, await workload.MatchAsync(pager, Ct));
            await BTreeVerifier.AssertNoLeaksAsync(pager, Ct);
        }

        await using var reopened = await workload.OpenAsync(device, Ct);
        Assert.Equal(workload.Transactions, await workload.MatchAsync(reopened, Ct));
    }
}

public sealed class CommitOutcomeTests_File : CommitOutcomeTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class CommitOutcomeTests_Memory : CommitOutcomeTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
