// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Storage;
using Tekium.Embedded.Tests.Storage;

namespace Tekium.Embedded.Tests.Indexing;

public sealed class BTreeTests
{
    private static readonly Comparer<byte[]> ByteOrder =
        Comparer<byte[]>.Create(static (a, b) => a.AsSpan().SequenceCompareTo(b));

    // Caché de 64 páginas: los árboles de estas pruebas no caben, así que el desalojo participa en todas.
    private static readonly PagerOptions SmallPages = new() { PageSize = PageSize.FromBytes(4096), CachePages = 64 };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_empty_tree_finds_nothing()
    {
        await using var pager = await NewPagerAsync();
        var tree = new BTree(pager);

        Assert.Equal(BTree.NotFound, await tree.GetAsync(new byte[] { 1 }, new byte[16], Ct));
        Assert.Empty(await ToListAsync(tree.RangeAsync(default, default, Ct)));
    }

    [Fact]
    public async Task A_committed_entry_is_found_and_a_missing_key_is_not()
    {
        await using var pager = await NewPagerAsync();
        var tree = new BTree(pager);
        await using (var tx = await pager.BeginWriteAsync(Ct))
        {
            await tree.PutAsync(tx, new byte[] { 1, 2 }, new byte[] { 9, 8, 7 }, Ct);
            await tx.CommitAsync(Ct);
        }

        var buffer = new byte[16];
        var length = await tree.GetAsync(new byte[] { 1, 2 }, buffer, Ct);

        Assert.Equal([9, 8, 7], buffer[..length]);
        Assert.Equal(BTree.NotFound, await tree.GetAsync(new byte[] { 1 }, buffer, Ct));
        Assert.Equal(BTree.NotFound, await tree.GetAsync(new byte[] { 1, 2, 0 }, buffer, Ct));
    }

    [Fact]
    public async Task Uncommitted_writes_are_visible_inside_the_transaction_only()
    {
        await using var pager = await NewPagerAsync();
        var tree = new BTree(pager);
        var buffer = new byte[16];

        await using (var tx = await pager.BeginWriteAsync(Ct))
        {
            await tree.PutAsync(tx, new byte[] { 1 }, new byte[] { 5 }, Ct);

            Assert.Equal(1, await BTree.GetAsync(tx, tx.Root, new byte[] { 1 }, buffer, Ct));
            Assert.Equal(BTree.NotFound, await tree.GetAsync(new byte[] { 1 }, buffer, Ct));
        }

        Assert.Equal(BTree.NotFound, await tree.GetAsync(new byte[] { 1 }, buffer, Ct));
    }

    [Fact]
    public async Task Putting_an_existing_key_replaces_its_value()
    {
        await using var pager = await NewPagerAsync();
        var tree = new BTree(pager);
        var key = new byte[] { 1 };
        var buffer = new byte[64];
        await using (var tx = await pager.BeginWriteAsync(Ct))
        {
            await tree.PutAsync(tx, key, new byte[] { 1, 1, 1 }, Ct);
            await tree.PutAsync(tx, key, new byte[] { 2, 2, 2 }, Ct);
            Assert.Equal([2, 2, 2], buffer[..await BTree.GetAsync(tx, tx.Root, key, buffer, Ct)]);

            await tree.PutAsync(tx, key, new byte[] { 3 }, Ct);
            Assert.Equal([3], buffer[..await BTree.GetAsync(tx, tx.Root, key, buffer, Ct)]);

            await tree.PutAsync(tx, key, new byte[40], Ct);
            Assert.Equal(40, await BTree.GetAsync(tx, tx.Root, key, buffer, Ct));
            await tx.CommitAsync(Ct);
        }

        Assert.Single(await ToListAsync(tree.RangeAsync(default, default, Ct)));
    }

    [Fact]
    public async Task Entries_larger_than_the_inline_limit_are_rejected()
    {
        await using var pager = await NewPagerAsync();
        var tree = new BTree(pager);
        await using var tx = await pager.BeginWriteAsync(Ct);

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await tree.PutAsync(tx, new byte[] { 1 }, new byte[4096 / 4], Ct));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await tree.PutAsync(tx, ReadOnlyMemory<byte>.Empty, new byte[1], Ct));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await tree.PutAsync(tx, new byte[(4096 / 16) + 1], new byte[1], Ct));
    }

    [Fact]
    public async Task A_destination_too_small_for_the_value_is_an_error()
    {
        await using var pager = await NewPagerAsync();
        var tree = new BTree(pager);
        await using var tx = await pager.BeginWriteAsync(Ct);
        await tree.PutAsync(tx, new byte[] { 1 }, new byte[10], Ct);

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await BTree.GetAsync(tx, tx.Root, new byte[] { 1 }, new byte[9], Ct));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Random_operations_match_a_sorted_dictionary_model(int seed)
    {
        var random = new Random(seed);
        var model = new SortedDictionary<byte[], byte[]>(ByteOrder);
        var device = new InMemoryStorageDevice();
        var buffer = new byte[1024];

        await using (var pager = await Pager.CreateAsync(device, SmallPages, Ct))
        {
            var tree = new BTree(pager);
            for (var batch = 0; batch < 40; batch++)
            {
                await using var tx = await pager.BeginWriteAsync(Ct);
                for (var i = 0; i < 500; i++)
                {
                    // Claves de un espacio pequeño para forzar reemplazos; tamaños variados para forzar splits desiguales.
                    var key = new byte[random.Next(1, 40)];
                    random.NextBytes(key);
                    key[0] = (byte)random.Next(0, 8);
                    if (random.Next(4) == 0 && model.Count > 0)
                    {
                        key = model.Keys.ElementAt(random.Next(Math.Min(model.Count, 50)));
                    }

                    var value = new byte[random.Next(0, 300)];
                    random.NextBytes(value);
                    await tree.PutAsync(tx, key, value, Ct);
                    model[key] = value;
                }

                // Un lote de cada cuatro se abandona: el modelo no debe verlo.
                if (batch % 4 == 3)
                {
                    await AssertMatchesModelAsync(tx, tx.Root, model, buffer);
                }

                await tx.CommitAsync(Ct);
            }

            await AssertMatchesModelAsync(pager, pager.Current.Root, model, buffer);
        }

        await using var reopened = await Pager.OpenAsync(device, SmallPages, Ct);
        await AssertMatchesModelAsync(reopened, reopened.Current.Root, model, buffer);
    }

    [Fact]
    public async Task A_rolled_back_transaction_leaves_the_tree_untouched()
    {
        await using var pager = await NewPagerAsync();
        var tree = new BTree(pager);
        var model = new SortedDictionary<byte[], byte[]>(ByteOrder);
        await using (var tx = await pager.BeginWriteAsync(Ct))
        {
            for (var i = 0; i < 2000; i++)
            {
                var key = Key(i);
                await tree.PutAsync(tx, key, key, Ct);
                model[key] = key;
            }

            await tx.CommitAsync(Ct);
        }

        await using (var tx = await pager.BeginWriteAsync(Ct))
        {
            for (var i = 0; i < 3000; i++)
            {
                await tree.PutAsync(tx, Key(i * 7), new byte[50], Ct);
            }
        }

        await AssertMatchesModelAsync(pager, pager.Current.Root, model, new byte[64]);
    }

    [Fact]
    public async Task A_reader_on_an_old_snapshot_never_sees_later_commits()
    {
        await using var pager = await NewPagerAsync();
        var tree = new BTree(pager);
        var before = new SortedDictionary<byte[], byte[]>(ByteOrder);
        await using (var tx = await pager.BeginWriteAsync(Ct))
        {
            for (var i = 0; i < 3000; i += 2)
            {
                var key = Key(i);
                await tree.PutAsync(tx, key, new byte[] { 1 }, Ct);
                before[key] = [1];
            }

            await tx.CommitAsync(Ct);
        }

        // El lector retiene su snapshot: las páginas que éste alcanza no se pueden reutilizar mientras viva.
        using var old = pager.BeginRead();
        for (var round = 0; round < 6; round++)
        {
            await using var tx = await pager.BeginWriteAsync(Ct);
            for (var i = 0; i < 3000; i++)
            {
                await tree.PutAsync(tx, Key(i), new byte[] { 2, (byte)round }, Ct);
            }

            await tx.CommitAsync(Ct);
        }

        await AssertMatchesModelAsync(pager, old.Root, before, new byte[64]);
        Assert.Equal(3000, (await ToListAsync(tree.RangeAsync(default, default, Ct))).Count);
    }

    [Fact]
    public async Task Range_is_inclusive_at_the_start_and_exclusive_at_the_end()
    {
        await using var pager = await NewPagerAsync();
        var tree = new BTree(pager);
        var random = new Random(11);
        await using (var tx = await pager.BeginWriteAsync(Ct))
        {
            for (var i = 0; i < 5000; i++)
            {
                await tree.PutAsync(tx, Key(i * 3), Key(i), Ct);
            }

            await tx.CommitAsync(Ct);
        }

        for (var round = 0; round < 50; round++)
        {
            var from = random.Next(-10, 15_010);
            var to = from + random.Next(0, 2000);
            var expected = Enumerable.Range(0, 5000).Select(i => i * 3).Where(k => k >= from && k < to).ToList();

            var actual = await ToListAsync(tree.RangeAsync(Key(from), Key(to), Ct));

            Assert.Equal(expected, actual.Select(e => BinaryPrimitives.ReadInt32BigEndian(e.Key) ^ int.MinValue));
        }

        Assert.Equal(5000, (await ToListAsync(tree.RangeAsync(default, default, Ct))).Count);
        Assert.Equal(1000, (await ToListAsync(tree.RangeAsync(Key(12_000), default, Ct))).Count);
        Assert.Equal(1000, (await ToListAsync(tree.RangeAsync(default, Key(3000), Ct))).Count);
    }

    [Fact]
    public async Task Sequential_inserts_leave_the_leaves_full()
    {
        // Split 100/0: 20 000 filas de 122 bytes (con su slot) caben en ~304 hojas de 8 KiB.
        // Con splits 50/50 serían cerca de 600.
        var device = new InMemoryStorageDevice();
        await using var pager = await Pager.CreateAsync(device, new PagerOptions { CachePages = 64 }, Ct);
        var tree = new BTree(pager);
        var key = new byte[16];
        var value = new byte[100];

        await using (var tx = await pager.BeginWriteAsync(Ct))
        {
            for (var i = 0; i < 20_000; i++)
            {
                BinaryPrimitives.WriteInt64BigEndian(key, i);
                await tree.PutAsync(tx, key, value, Ct);
            }

            await tx.CommitAsync(Ct);
        }

        Assert.InRange(pager.Current.PageCount, 304u, 345u);
        Assert.Equal(20_000, (await ToListAsync(tree.RangeAsync(default, default, Ct))).Count);
    }

    [Fact]
    public async Task Sequential_inserts_stay_correct_when_internal_nodes_split()
    {
        // 100 000 claves ascendentes en páginas de 4 KiB: ~740 hojas, más de las ~339 que referencia
        // un nodo interno, así que el split 100/0 también ocurre en los niveles de arriba.
        await using var pager = await NewPagerAsync();
        var tree = new BTree(pager);
        const int Rows = 100_000;
        for (var start = 0; start < Rows; start += 10_000)
        {
            await using var tx = await pager.BeginWriteAsync(Ct);
            for (var i = start; i < start + 10_000; i++)
            {
                await tree.PutAsync(tx, Key(i), new byte[20], Ct);
            }

            await tx.CommitAsync(Ct);
        }

        var buffer = new byte[20];
        for (var i = 0; i < Rows; i += 7)
        {
            Assert.Equal(20, await tree.GetAsync(Key(i), buffer, Ct));
        }

        Assert.Equal(BTree.NotFound, await tree.GetAsync(Key(Rows), buffer, Ct));
        var scanned = await ToListAsync(tree.RangeAsync(default, default, Ct));
        Assert.Equal(Rows, scanned.Count);
        Assert.Equal(Enumerable.Range(0, Rows), scanned.Select(e => BinaryPrimitives.ReadInt32BigEndian(e.Key) ^ int.MinValue));
    }

    [Fact]
    public async Task A_tree_written_to_a_real_file_is_read_back_after_reopening()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tekium-{Guid.NewGuid():N}.tkdb");
        try
        {
            await using (var pager = await Pager.CreateAsync(
                FileStorageDevice.Open(path, FileMode.CreateNew), new PagerOptions { CachePages = 64 }, Ct))
            {
                var tree = new BTree(pager);
                for (var batch = 0; batch < 5; batch++)
                {
                    await using var tx = await pager.BeginWriteAsync(Ct);
                    for (var i = 0; i < 2000; i++)
                    {
                        await tree.PutAsync(tx, Key((batch * 2000) + i), Key(i), Ct);
                    }

                    await tx.CommitAsync(Ct);
                }
            }

            // Reabrir con la caché vacía obliga a leer cada página desde el archivo (camino asíncrono lento).
            await using var reopened = await Pager.OpenAsync(
                FileStorageDevice.Open(path, FileMode.Open), new PagerOptions { CachePages = 64 }, Ct);
            var reader = new BTree(reopened);
            var buffer = new byte[4];

            Assert.Equal(5ul, reopened.Current.TransactionId.Value);
            Assert.Equal(4, await reader.GetAsync(Key(7777), buffer, Ct));
            Assert.Equal(Key(1777), buffer);
            Assert.Equal(10_000, (await ToListAsync(reader.RangeAsync(default, default, Ct))).Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_cancelled_scan_stops()
    {
        await using var pager = await NewPagerAsync();
        var tree = new BTree(pager);
        await using (var tx = await pager.BeginWriteAsync(Ct))
        {
            for (var i = 0; i < 5000; i++)
            {
                await tree.PutAsync(tx, Key(i), new byte[20], Ct);
            }

            await tx.CommitAsync(Ct);
        }

        using var cts = new CancellationTokenSource();
        var seen = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var entry in tree.RangeAsync(default, default, cts.Token))
            {
                if (++seen == 10)
                {
                    await cts.CancelAsync();
                }
            }
        });
        Assert.InRange(seen, 10, 4999);
    }

    private static async Task<Pager> NewPagerAsync() =>
        await Pager.CreateAsync(new InMemoryStorageDevice(), SmallPages, Ct);

    // Entero con signo codificado de forma que el orden de bytes sea el orden numérico.
    private static byte[] Key(int value)
    {
        var key = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(key, value ^ int.MinValue);
        return key;
    }

    private static async Task AssertMatchesModelAsync(
        IPageSource source, PageNumber root, SortedDictionary<byte[], byte[]> model, byte[] buffer)
    {
        foreach (var (key, value) in model)
        {
            var length = await BTree.GetAsync(source, root, key, buffer, Ct);
            Assert.True(length >= 0, "A key present in the model is missing from the tree.");
            Assert.True(value.AsSpan().SequenceEqual(buffer.AsSpan(0, length)), "A value differs from the model.");
        }

        var scanned = await ToListAsync(BTree.RangeAsync(source, root, default, default, Ct));
        Assert.Equal(model.Count, scanned.Count);
        var index = 0;
        foreach (var (key, value) in model)
        {
            Assert.True(key.AsSpan().SequenceEqual(scanned[index].Key), "Scan order differs from the model.");
            Assert.True(value.AsSpan().SequenceEqual(scanned[index].Value), "A scanned value differs from the model.");
            index++;
        }
    }

    private static async Task<List<(byte[] Key, byte[] Value)>> ToListAsync(IAsyncEnumerable<BTreeEntry> entries)
    {
        var list = new List<(byte[] Key, byte[] Value)>();
        await foreach (var entry in entries)
        {
            list.Add((entry.Key.ToArray(), entry.Value.ToArray()));
        }

        return list;
    }
}
