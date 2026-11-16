// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.CrashChild;
using Tekium.Embedded.Storage;
using Tekium.Embedded.Tests.Indexing;
using Tekium.Embedded.Tests.Storage;
using Tekium.Embedded.Transactions;

namespace Tekium.Embedded.Tests.Transactions;

public abstract class ReadTransactionTests : DeviceBackedTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_read_transaction_keeps_seeing_its_state_while_the_writer_commits()
    {
        var workload = new CrashWorkload(seed: 3, transactions: 30, password: null);
        await using var pager = await workload.CreateAsync(NewDevice(), Ct);
        await workload.RunAsync(pager, 0, null, Ct);

        var longer = new CrashWorkload(seed: 3, transactions: 60, password: null);
        await using (var read = ReadTransaction.Begin(pager))
        {
            Assert.Equal(pager.Current.TransactionId, read.Id);
            await longer.RunAsync(pager, 30, null, Ct);

            // Treinta commits después, con una caché que no retiene nada de entonces.
            Assert.Equal(30, await longer.MatchAsync(read, read.Root, Ct));
            await BTreeVerifier.VerifyAsync(read, read.Root, Ct);
        }

        Assert.Equal(60, await longer.MatchAsync(pager, Ct));
    }

    [Fact]
    public async Task Disposing_a_read_transaction_twice_releases_its_snapshot_only_once()
    {
        var workload = new CrashWorkload(seed: 3, transactions: 60, password: null);
        await using var pager = await workload.CreateAsync(NewDevice(), Ct);
        var early = new CrashWorkload(seed: 3, transactions: 10, password: null);
        await early.RunAsync(pager, 0, null, Ct);

        var careless = ReadTransaction.Begin(pager);
        await using var careful = ReadTransaction.Begin(pager);
        await careless.DisposeAsync();
        await careless.DisposeAsync();

        // Si el segundo Dispose hubiera descontado otro lector, el estado quedaría sin dueño y sus
        // páginas se reutilizarían debajo de la transacción que sigue viva.
        await workload.RunAsync(pager, 10, null, Ct);

        Assert.Equal(10, await workload.MatchAsync(careful, careful.Root, Ct));
        await BTreeVerifier.VerifyAsync(careful, careful.Root, Ct);
    }

    [Fact]
    public async Task A_disposed_read_transaction_refuses_to_read()
    {
        var workload = new CrashWorkload(seed: 3, transactions: 2, password: null);
        await using var pager = await workload.CreateAsync(NewDevice(), Ct);
        await workload.RunAsync(pager, 0, null, Ct);
        var read = ReadTransaction.Begin(pager);
        var root = read.Root;
        await read.DisposeAsync();

        // Leer con un snapshot ya liberado devolvería páginas que el escritor pudo haber reutilizado.
        Assert.Throws<ObjectDisposedException>(() => read.Root);
        Assert.Throws<ObjectDisposedException>(() => read.TryPin(root, out _));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await read.PinAsync(root, Ct));
    }

    [Fact]
    public async Task Pages_held_by_a_read_transaction_become_reusable_after_it_is_disposed()
    {
        var workload = new CrashWorkload(seed: 3, transactions: 400, password: null);
        await using var pager = await workload.CreateAsync(NewDevice(), Ct);
        var warmup = new CrashWorkload(seed: 3, transactions: 100, password: null);
        await warmup.RunAsync(pager, 0, null, Ct);

        // Sin lectores, reescribir los mismos datos no hace crecer el archivo indefinidamente.
        var steady = new CrashWorkload(seed: 3, transactions: 200, password: null);
        await steady.RunAsync(pager, 100, null, Ct);
        var settled = pager.Current.PageCount;

        // Con un lector retenido, cada commit necesita páginas nuevas; al soltarlo, el archivo deja de crecer.
        uint held;
        await using (ReadTransaction.Begin(pager))
        {
            var more = new CrashWorkload(seed: 3, transactions: 300, password: null);
            await more.RunAsync(pager, 200, null, Ct);
            held = pager.Current.PageCount;
        }

        await workload.RunAsync(pager, 300, null, Ct);
        Assert.True(held > settled * 2, $"A held snapshot should have pinned pages: {settled} then {held}.");
        Assert.True(
            pager.Current.PageCount <= held + 8,
            $"The file kept growing after the reader left: {held} then {pager.Current.PageCount}.");
        await BTreeVerifier.AssertNoLeaksAsync(pager, Ct);
    }
}

public sealed class ReadTransactionTests_File : ReadTransactionTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class ReadTransactionTests_Memory : ReadTransactionTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
