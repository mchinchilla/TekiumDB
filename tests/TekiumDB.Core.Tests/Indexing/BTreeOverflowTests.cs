// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using TekiumDB.Indexing;
using TekiumDB.Storage;
using TekiumDB.Tests.Storage;

namespace TekiumDB.Tests.Indexing;

/// <summary>
/// Valores que no caben en una hoja: salen a una cadena de páginas de overflow y la hoja guarda sólo
/// dónde empieza. Lo delicado no es escribirlos sino no perder páginas al reemplazarlos o borrarlos.
/// </summary>
public abstract class BTreeOverflowTests : DeviceBackedTests
{
    private const int PageBytes = 4096;

    private static readonly PagerOptions Options = new() { PageSize = PageSize.FromBytes(PageBytes), CachePages = 48 };

    // Con páginas de 4 KiB: una entrada cabe en la hoja hasta 1016 bytes, y cada página de overflow lleva 4060.
    private static readonly int[] Sizes =
        [0, 1, 1000, 1011, 1012, 1013, 1014, 2000, 4059, 4060, 4061, 8119, 8120, 8121, 20_000, 300_000];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Values_of_any_size_come_back_whole_by_key_and_by_scan_after_reopening()
    {
        var device = NewDevice();
        await using (var pager = await Pager.CreateAsync(device, Options, Ct))
        {
            var tree = new BTree(pager);
            await using var writer = await pager.BeginWriteAsync(Ct);
            for (var i = 0; i < Sizes.Length; i++)
            {
                await tree.PutAsync(writer, Key(i), Value(i, Sizes[i]), Ct);
            }

            // Antes del commit el escritor ya lee lo suyo.
            await AssertAllAsync(writer, writer.Root);
            await writer.CommitAsync(Ct);
            await BTreeVerifier.AssertNoLeaksAsync(pager, Ct);
        }

        await using var reopened = await Pager.OpenAsync(device, Options, Ct);
        using var read = reopened.BeginRead();
        await AssertAllAsync(reopened, read.Root);
    }

    [Fact]
    public async Task Replacing_and_deleting_large_values_gives_every_page_back()
    {
        await using var pager = await Pager.CreateAsync(NewDevice(), Options, Ct);
        var tree = new BTree(pager);
        var random = new Random(31);
        var expected = new Dictionary<int, int>();
        uint settled = 0;
        for (var round = 0; round < 60; round++)
        {
            await using (var writer = await pager.BeginWriteAsync(Ct))
            {
                for (var i = 0; i < 12; i++)
                {
                    // Chico por grande, grande por chico, grande por grande del mismo y de otro tamaño, y borrados.
                    var key = random.Next(24);
                    if (random.Next(5) == 0)
                    {
                        Assert.Equal(expected.Remove(key), await tree.DeleteAsync(writer, Key(key), Ct));
                        continue;
                    }

                    var size = Sizes[random.Next(Sizes.Length - 1)];
                    await tree.PutAsync(writer, Key(key), Value(key, size), Ct);
                    expected[key] = size;
                }

                await writer.CommitAsync(Ct);
            }

            await BTreeVerifier.AssertNoLeaksAsync(pager, Ct);
            settled = round == 29 ? pager.Current.PageCount : settled;
        }

        // Las páginas de los valores reemplazados se reutilizan: el archivo deja de crecer.
        Assert.True(
            pager.Current.PageCount <= settled * 2,
            $"The file kept growing: {settled} pages after 30 rounds, {pager.Current.PageCount} after 60.");

        var buffer = new byte[32_768];
        using var read = pager.BeginRead();
        foreach (var (key, size) in expected)
        {
            Assert.Equal(size, await BTree.GetAsync(pager, read.Root, Key(key), buffer, Ct));
            Assert.True(buffer.AsSpan(0, size).SequenceEqual(Value(key, size)));
        }

        Assert.Equal(expected.Count, await BTreeVerifier.CountAsync(pager, read.Root, Ct));
    }

    [Fact]
    public async Task Emptying_a_tree_of_large_values_leaves_nothing_behind()
    {
        await using var pager = await Pager.CreateAsync(NewDevice(), Options, Ct);
        var tree = new BTree(pager);
        await using (var writer = await pager.BeginWriteAsync(Ct))
        {
            for (var i = 0; i < 40; i++)
            {
                await tree.PutAsync(writer, Key(i), Value(i, 9000 + i), Ct);
            }

            await writer.CommitAsync(Ct);
        }

        await using (var writer = await pager.BeginWriteAsync(Ct))
        {
            // Uno se reemplaza y se borra dentro de la misma transacción: su cadena nunca llegó al disco.
            await tree.PutAsync(writer, Key(100), Value(100, 50_000), Ct);
            for (var i = 0; i < 40; i++)
            {
                Assert.True(await tree.DeleteAsync(writer, Key(i), Ct));
            }

            Assert.True(await tree.DeleteAsync(writer, Key(100), Ct));
            Assert.True(writer.Root.IsNone);
            await writer.CommitAsync(Ct);
        }

        await BTreeVerifier.AssertNoLeaksAsync(pager, Ct);
    }

    [Fact]
    public async Task A_reader_keeps_the_large_value_it_saw_while_the_writer_replaces_it()
    {
        await using var pager = await Pager.CreateAsync(NewDevice(), Options, Ct);
        var tree = new BTree(pager);
        await PutAsync(pager, tree, 1, 30_000);
        var buffer = new byte[65_536];
        using (var read = pager.BeginRead())
        {
            for (var round = 0; round < 20; round++)
            {
                await PutAsync(pager, tree, 1, 20_000 + round);
                await PutAsync(pager, tree, 2 + round, 12_000);
            }

            Assert.Equal(30_000, await BTree.GetAsync(pager, read.Root, Key(1), buffer, Ct));
            Assert.True(buffer.AsSpan(0, 30_000).SequenceEqual(Value(1, 30_000)));
        }

        Assert.Equal(20_019, await tree.GetAsync(Key(1), buffer, Ct));
    }

    [Fact]
    public async Task A_destination_that_is_too_small_says_how_much_room_the_value_needs()
    {
        await using var pager = await Pager.CreateAsync(NewDevice(), Options, Ct);
        var tree = new BTree(pager);
        await PutAsync(pager, tree, 1, 500);
        await PutAsync(pager, tree, 2, 70_000);

        foreach (var (key, size) in new[] { (1, 500), (2, 70_000) })
        {
            var result = await tree.GetAsync(Key(key), new byte[size - 1], Ct);
            Assert.True(BTree.IsTooSmall(result, out var required));
            Assert.Equal(size, required);
            Assert.True(BTree.IsTooSmall(await tree.GetAsync(Key(key), Memory<byte>.Empty, Ct), out _));
            Assert.Equal(size, await tree.GetAsync(Key(key), new byte[size], Ct));
        }

        Assert.Equal(BTree.NotFound, await tree.GetAsync(Key(3), Memory<byte>.Empty, Ct));
        Assert.False(BTree.IsTooSmall(BTree.NotFound, out _));
        Assert.False(BTree.IsTooSmall(0, out _));
    }

    [Fact]
    public async Task A_value_beyond_the_limit_is_refused_before_anything_is_written()
    {
        await using var pager = await Pager.CreateAsync(NewDevice(), Options, Ct);
        var tree = new BTree(pager);
        await using var writer = await pager.BeginWriteAsync(Ct);

        // Memoria reservada y nunca tocada: la validación ocurre antes de leerla.
        var tooLarge = new byte[BTree.MaxValueSize + 1];
        await Assert.ThrowsAsync<ArgumentException>(async () => await tree.PutAsync(writer, Key(1), tooLarge, Ct));
        Assert.True(writer.Root.IsNone);
    }

    [Fact]
    public async Task A_chain_that_leads_to_a_page_that_is_not_overflow_is_reported_as_corruption()
    {
        await using var pager = await Pager.CreateAsync(NewDevice(), Options, Ct);
        var tree = new BTree(pager);

        // Una hoja hecha a mano cuya única entrada dice continuar en la propia hoja.
        await using (var writer = await pager.BeginWriteAsync(Ct))
        {
            var leaf = writer.Allocate(PageType.BTreeLeaf, out var number).Span;
            BTreeNode.Initialize(leaf, leaf: true);
            var reference = new byte[BTreeNode.OverflowReferenceSize];
            BTreeNode.WriteOverflowReference(reference, 5000, number);
            Assert.True(BTreeNode.TryInsertLeaf(leaf, 0, Key(1), reference, overflow: true));
            writer.Root = number;
            await writer.CommitAsync(Ct);
        }

        await Assert.ThrowsAsync<InvalidDataException>(async () => await tree.GetAsync(Key(1), new byte[8192], Ct));
        await Assert.ThrowsAsync<InvalidDataException>(
            async () =>
            {
                await foreach (var unused in tree.RangeAsync(default, default, Ct))
                {
                }
            });
        await using var again = await pager.BeginWriteAsync(Ct);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await tree.DeleteAsync(again, Key(1), Ct));
    }

    private static byte[] Key(int value)
    {
        var key = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(key, value);
        return key;
    }

    /// <summary>Contenido que depende de la clave, del tamaño y de la posición: un trozo fuera de lugar se nota.</summary>
    private static byte[] Value(int key, int size)
    {
        var value = new byte[size];
        for (var i = 0; i < size; i++)
        {
            value[i] = (byte)((i * 31) + (i >> 8) + key + size);
        }

        return value;
    }

    private static async Task PutAsync(Pager pager, BTree tree, int key, int size)
    {
        await using var writer = await pager.BeginWriteAsync(Ct);
        await tree.PutAsync(writer, Key(key), Value(key, size), Ct);
        await writer.CommitAsync(Ct);
    }

    private static async Task AssertAllAsync(IPageSource source, PageNumber root)
    {
        var buffer = new byte[400_000];
        for (var i = 0; i < Sizes.Length; i++)
        {
            Assert.Equal(Sizes[i], await BTree.GetAsync(source, root, Key(i), buffer, Ct));
            Assert.True(buffer.AsSpan(0, Sizes[i]).SequenceEqual(Value(i, Sizes[i])), $"Value {i} ({Sizes[i]} bytes) came back different.");
        }

        var index = 0;
        await foreach (var entry in BTree.RangeAsync(source, root, default, default, Ct))
        {
            Assert.True(entry.Key.Span.SequenceEqual(Key(index)));
            Assert.True(entry.Value.Span.SequenceEqual(Value(index, Sizes[index])), $"Scanned value {index} came back different.");
            index++;
        }

        Assert.Equal(Sizes.Length, index);
    }
}

public sealed class BTreeOverflowTests_File : BTreeOverflowTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class BTreeOverflowTests_Memory : BTreeOverflowTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
