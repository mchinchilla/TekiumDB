// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Security;
using Tekium.Embedded.Storage;
using Tekium.Embedded.Tests.Indexing;
using Tekium.Embedded.Tests.Storage;

namespace Tekium.Embedded.Tests.Transactions;

/// <summary>
/// Una transacción que no cabe en memoria vuelca al archivo las páginas que ya terminó, antes del commit.
/// Eso no puede cambiar nada de lo que se ve: ni para ella, ni para los demás, ni después de un corte.
/// </summary>
public abstract class SpillTests : DeviceBackedTests
{
    private static readonly PagerOptions Options = new()
    {
        PageSize = PageSize.FromBytes(4096),
        CachePages = 64,
        KeyDerivation = new Argon2Parameters(MemoryKiB: 64, Iterations: 1, Parallelism: 1),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null, false)]
    [InlineData("spill", false)]
    [InlineData(null, true)]
    [InlineData("spill", true)]
    public async Task A_transaction_that_spills_reads_its_own_pages_back_and_commits_like_any_other(string? password, bool compress)
    {
        var device = NewDevice();
        var options = Options with { Compression = compress };
        var model = new SortedDictionary<int, byte[]>();
        await using (var pager = await Pager.CreateAsync(device, options, password, Ct))
        {
            var tree = new BTree(pager);
            await using var writer = await pager.BeginWriteAsync(Ct);
            var most = 0;
            for (var i = 0; i < 6000; i++)
            {
                // Vuelve sobre claves ya volcadas: cambiarlas copia la página, como una confirmada.
                var key = i % 7 == 0 ? i / 3 : i;
                var value = Value(i, key % 97 == 0 ? 6000 : 30);
                await tree.PutAsync(writer, Key(key), value, Ct);
                model[key] = value;
                if (i % 9 == 0 && model.Remove(i / 2))
                {
                    Assert.True(await tree.DeleteAsync(writer, Key(i / 2), Ct));
                }

                most = Math.Max(most, writer.Dirty.Count);
                if (writer.Dirty.Count >= 64)
                {
                    await writer.SpillAsync(Ct);
                    Assert.Empty(writer.Dirty);
                }
            }

            Assert.True(most < 90, $"The transaction held {most} pages in memory.");
            await AssertMatchesAsync(writer, writer.Root, model);

            // Los demás no ven nada hasta el commit.
            Assert.True(pager.Current.Root.IsNone);
            await writer.CommitAsync(Ct);
            await BTreeVerifier.AssertNoLeaksAsync(pager, Ct);
        }

        await using var reopened = await Pager.OpenAsync(device, options, password, Ct);
        using var read = reopened.BeginRead();
        await AssertMatchesAsync(reopened, read.Root, model);
        await BTreeVerifier.AssertNoLeaksAsync(reopened, Ct);
    }

    /// <summary>Lo volcado se escribió pero no se vació: el commit lo vacía antes de tocar la raíz, aunque no le quede nada más que escribir.</summary>
    [Fact]
    public async Task A_commit_with_nothing_left_in_memory_still_flushes_what_was_spilled_before_the_root()
    {
        var device = NewDevice();
        await using var pager = await Pager.CreateAsync(device, Options, Ct);
        var tree = new BTree(pager);
        await using var writer = await pager.BeginWriteAsync(Ct);
        for (var i = 0; i < 500; i++)
        {
            await tree.PutAsync(writer, Key(i), Value(i, 30), Ct);
        }

        await writer.SpillAsync(Ct);
        Assert.Empty(writer.Dirty);
        var flushes = device.FlushCount;
        var writes = device.WriteCount;
        await writer.CommitAsync(Ct);

        // Una sola escritura, la de la raíz, entre dos vaciados: el de las páginas y el suyo.
        Assert.Equal(writes + 1, device.WriteCount);
        Assert.Equal(flushes + 2, device.FlushCount);
    }

    [Fact]
    public async Task What_was_spilled_by_a_transaction_that_rolls_back_is_just_unused_space()
    {
        var device = NewDevice();
        var model = new SortedDictionary<int, byte[]>();
        await using var pager = await Pager.CreateAsync(device, Options, Ct);
        var tree = new BTree(pager);
        await FillAsync(pager, tree, model, 0, 2000, spillEvery: 0);
        var committed = pager.Current;

        await using (var writer = await pager.BeginWriteAsync(Ct))
        {
            for (var i = 0; i < 4000; i++)
            {
                await tree.PutAsync(writer, Key(i), Value(i + 1, 50), Ct);
                if (i % 500 == 499)
                {
                    await writer.SpillAsync(Ct);
                }
            }
        }

        Assert.Equal(committed, pager.Current);
        using (var read = pager.BeginRead())
        {
            await AssertMatchesAsync(pager, read.Root, model);
        }

        // El siguiente escritor reusa ese espacio sin tropezar con lo que quedó escrito ahí.
        await FillAsync(pager, tree, model, 1000, 5000, spillEvery: 300);
        using var after = pager.BeginRead();
        await AssertMatchesAsync(pager, after.Root, model);
        await BTreeVerifier.AssertNoLeaksAsync(pager, Ct);
    }

    /// <summary>
    /// Corte de energía antes de cada operación de una transacción que vuelca: lo que abre después es el
    /// estado anterior o el nuevo, nunca uno a medias.
    /// </summary>
    [Fact]
    public async Task Losing_power_while_spilling_or_committing_leaves_the_previous_state_or_the_new_one()
    {
        // Los dos estados posibles, calculados aparte de la ejecución.
        var before = new SortedDictionary<int, byte[]>();
        for (var i = 0; i < 600; i++)
        {
            before[i] = Value(i + 600, 40);
        }

        var after = new SortedDictionary<int, byte[]>(before);
        for (var i = 300; i < 1500; i++)
        {
            after[i] = Value(i + 1500, 40);
        }

        async Task<bool> RunAsync(TestDevice device, int? powerLossAt)
        {
            try
            {
                await using var pager = await Pager.CreateAsync(device, Options, Ct);
                var tree = new BTree(pager);
                await FillAsync(pager, tree, [], 0, 600, spillEvery: 0);
                if (powerLossAt is { } at)
                {
                    device.TrackUnflushedWrites = true;
                    device.PowerLossAtOperation = device.OperationCount + at;
                }

                await FillAsync(pager, tree, [], 300, 1500, spillEvery: 150);
                return true;
            }
            catch (IOException) when (device.PowerLost)
            {
                return false;
            }
        }

        var complete = NewDevice();
        var start = NewDevice();
        await RunAsync(start, 1);
        Assert.True(await RunAsync(complete, null));
        var operations = complete.OperationCount - start.OperationCount + 1;
        Assert.True(operations > 12, "The transaction did not spill often enough to say anything.");

        var random = new Random(68);
        for (var at = 1; at <= operations; at++)
        {
            var device = NewDevice();
            var acknowledged = await RunAsync(device, at);
            foreach (var seed in new[] { 0, 1, random.Next(), random.Next(), random.Next() })
            {
                var image = await device.PowerLossImageAsync(
                    (write, sector) => seed == 1 || (seed != 0 && HashCode.Combine(seed, write, sector) % 2 == 0));
                try
                {
                    await using var recovered = await Pager.OpenAsync(image, Options, Ct);
                    using var read = recovered.BeginRead();
                    var count = await BTreeVerifier.CountAsync(recovered, read.Root, Ct);
                    Assert.True(
                        acknowledged ? count == after.Count : count == before.Count || count == after.Count,
                        $"Power lost at operation {at} of {operations} (image {seed}): {count} entries.");
                    await AssertMatchesAsync(recovered, read.Root, count == after.Count ? after : before);
                    await BTreeVerifier.AssertNoLeaksAsync(recovered, Ct);
                }
                finally
                {
                    await image.DestroyAsync();
                }
            }
        }
    }

    private static async Task FillAsync(Pager pager, BTree tree, SortedDictionary<int, byte[]> model, int from, int to, int spillEvery)
    {
        await using var writer = await pager.BeginWriteAsync(Ct);
        for (var i = from; i < to; i++)
        {
            var value = Value(i + to, 40);
            await tree.PutAsync(writer, Key(i), value, Ct);
            model[i] = value;
            if (spillEvery > 0 && i % spillEvery == spillEvery - 1)
            {
                await writer.SpillAsync(Ct);
            }
        }

        await writer.CommitAsync(Ct);
    }

    private static async Task AssertMatchesAsync(IPageSource source, PageNumber root, SortedDictionary<int, byte[]> model)
    {
        await BTreeVerifier.VerifyAsync(source, root, Ct);
        var found = new List<(int, string)>();
        await foreach (var entry in BTree.RangeAsync(source, root, default, default, Ct))
        {
            found.Add((BinaryPrimitives.ReadInt32BigEndian(entry.Key.Span), Convert.ToHexString(entry.Value.Span)));
        }

        Assert.Equal(model.Select(static pair => (pair.Key, Convert.ToHexString(pair.Value))), found);
    }

    private static byte[] Key(int value)
    {
        var key = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(key, value);
        return key;
    }

    private static byte[] Value(int seed, int length)
    {
        var value = new byte[length];
        new Random(seed).NextBytes(value);
        return value;
    }
}

public sealed class SpillTests_File : SpillTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class SpillTests_Memory : SpillTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
