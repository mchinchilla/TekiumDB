// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using CsCheck;
using TekiumDB.Indexing;
using TekiumDB.Storage;
using TekiumDB.Tests.Storage;

namespace TekiumDB.Tests.Indexing;

/// <summary>
/// El árbol construido de abajo hacia arriba tiene que ser un árbol como cualquier otro: las mismas
/// entradas, la misma estructura válida, y después se le inserta y se le borra como a cualquiera.
/// </summary>
public sealed class BTreeBuilderTests
{
    private static readonly PagerOptions Options = new() { PageSize = PageSize.FromBytes(4096), CachePages = 64 };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(300)]
    [InlineData(5000)]
    [InlineData(60_000)]
    public async Task A_tree_built_bottom_up_holds_exactly_the_entries_it_was_given(int count)
    {
        var entries = Enumerable.Range(0, count)
            .Select(static i => (Key(i * 3), Value(i, i % 211 == 0 ? 9000 : 10 + (i % 60))))
            .ToList();

        await using var pager = await Pager.CreateAsync(TestDevice.Memory(), Options, Ct);
        var tree = new BTree(pager);
        await using (var writer = await pager.BeginWriteAsync(Ct))
        {
            writer.Root = await BuildAsync(tree, writer, entries, spillAt: 32);
            await AssertHoldsAsync(writer, writer.Root, entries);
            await writer.CommitAsync(Ct);
        }

        using var read = pager.BeginRead();
        await AssertHoldsAsync(pager, read.Root, entries);
        await BTreeVerifier.AssertNoLeaksAsync(pager, Ct);

        // Búsquedas puntuales: lo que está y lo que cae entre dos claves.
        var buffer = new byte[16_000];
        foreach (var index in new[] { 0, count / 2, count - 1 }.Where(index => index >= 0 && index < count))
        {
            Assert.Equal(entries[index].Item2.Length, await BTree.GetAsync(pager, read.Root, entries[index].Item1, buffer, Ct));
            Assert.Equal(BTree.NotFound, await BTree.GetAsync(pager, read.Root, Key((index * 3) + 1), buffer, Ct));
        }
    }

    /// <summary>
    /// Cuántos hijos caben en un nodo depende del tamaño de las claves. Con estos tamaños el último nodo
    /// de un nivel se queda, según el caso, con uno, dos o muchos hijos: con uno tiene que tomar prestado.
    /// </summary>
    [Fact]
    public async Task The_last_node_of_a_level_is_valid_however_many_children_are_left_for_it()
    {
        await using var pager = await Pager.CreateAsync(TestDevice.Memory(), Options with { CachePages = 2048 }, Ct);
        var tree = new BTree(pager);

        // Cuatro entradas por hoja y unos 340 hijos por nodo: se recorre el número de hojas alrededor de
        // uno y de dos nodos llenos.
        foreach (var leaves in Enumerable.Range(336, 12).Concat(Enumerable.Range(676, 12)))
        {
            var entries = Enumerable.Range(0, leaves * 4).Select(static i => (Key(i), Value(i, 900))).ToList();
            await using var writer = await pager.BeginWriteAsync(Ct);
            var root = await BuildAsync(tree, writer, entries, spillAt: int.MaxValue);
            Assert.Equal(entries.Count, await BTreeVerifier.CountAsync(writer, root, Ct));
            await BTreeVerifier.VerifyAsync(writer, root, Ct);
        }
    }

    [Fact]
    public async Task Any_sorted_entries_build_the_tree_that_inserting_them_would_read_as()
    {
        var entry = Gen.Select(Gen.Byte.Array[1, 200], Gen.Int[0, 30], Gen.Int[0, 700], Gen.Byte)
            .Select(static (key, large, length, fill) => (key, Enumerable.Repeat(fill, large == 0 ? 2000 + length : length).ToArray()));
        await entry.Array[0, 600].SampleAsync(
            async generated =>
            {
                var entries = generated
                    .DistinctBy(static pair => Convert.ToHexString(pair.Item1))
                    .OrderBy(static pair => pair.Item1, Comparer<byte[]>.Create(static (a, b) => a.AsSpan().SequenceCompareTo(b)))
                    .ToList();
                await using var pager = await Pager.CreateAsync(TestDevice.Memory(), Options, Ct);
                var tree = new BTree(pager);
                await using var writer = await pager.BeginWriteAsync(Ct);
                var root = await BuildAsync(tree, writer, entries, spillAt: 8);
                await AssertHoldsAsync(writer, root, entries);
                writer.Root = root;
                await writer.CommitAsync(Ct);
                await BTreeVerifier.AssertNoLeaksAsync(pager, Ct);
            },
            iter: 200);
    }

    [Fact]
    public async Task The_leaves_are_full_and_the_tree_takes_inserts_and_deletes_afterwards()
    {
        var entries = Enumerable.Range(0, 20_000).Select(static i => (Key(i * 2), Value(i, 40))).ToList();
        await using var pager = await Pager.CreateAsync(TestDevice.Memory(), Options, Ct);
        var tree = new BTree(pager);
        var model = new SortedDictionary<int, byte[]>(entries.ToDictionary(static pair => BinaryPrimitives.ReadInt32BigEndian(pair.Item1), static pair => pair.Item2));
        await using (var writer = await pager.BeginWriteAsync(Ct))
        {
            writer.Root = await BuildAsync(tree, writer, entries, spillAt: 64);
            await writer.CommitAsync(Ct);
        }

        // Cada entrada ocupa 4 de cabecera, 4 de clave, 40 de valor y 2 de casilla: 50 bytes.
        using (var read = pager.BeginRead())
        {
            var pages = await BTreeVerifier.VerifyAsync(pager, read.Root, Ct);
            var needed = 20_000 * 50 / (double)PageLayout.PayloadSize(pager.PageSize);
            Assert.True(pages <= needed * 1.03, $"The tree takes {pages} pages for entries that fill {needed:F1}.");
        }

        var random = new Random(68);
        await using (var writer = await pager.BeginWriteAsync(Ct))
        {
            for (var i = 0; i < 6000; i++)
            {
                var key = random.Next(45_000);
                if (random.Next(3) == 0)
                {
                    Assert.Equal(model.Remove(key), await tree.DeleteAsync(writer, Key(key), Ct));
                }
                else
                {
                    model[key] = Value(i, random.Next(100));
                    await tree.PutAsync(writer, Key(key), model[key], Ct);
                }
            }

            await writer.CommitAsync(Ct);
        }

        using var after = pager.BeginRead();
        await AssertHoldsAsync(pager, after.Root, [.. model.Select(static pair => (Key(pair.Key), pair.Value))]);
        await BTreeVerifier.AssertNoLeaksAsync(pager, Ct);
    }

    [Fact]
    public async Task Entries_out_of_order_or_too_long_are_refused()
    {
        await using var pager = await Pager.CreateAsync(TestDevice.Memory(), Options, Ct);
        var tree = new BTree(pager);
        await using var writer = await pager.BeginWriteAsync(Ct);
        var builder = new BTreeBuilder(tree, writer, 64);
        builder.Add(Key(5), Value(1, 10));

        Assert.Throws<ArgumentException>(() => builder.Add(Key(5), Value(1, 10)));
        Assert.Throws<ArgumentException>(() => builder.Add(Key(4), Value(1, 10)));
        Assert.Throws<ArgumentException>(() => builder.Add([], Value(1, 10)));
        Assert.Throws<ArgumentException>(() => builder.Add(new byte[tree.MaxKeySize + 1], Value(1, 10)));

        // Lo rechazado no deja rastro.
        builder.Add(Key(6), Value(2, 10));
        Assert.Equal(2, builder.Count);
        Assert.Equal(2, await BTreeVerifier.CountAsync(writer, builder.Complete(), Ct));
    }

    private static async Task<PageNumber> BuildAsync(BTree tree, WriteTransaction writer, List<(byte[] Key, byte[] Value)> entries, int spillAt)
    {
        var builder = new BTreeBuilder(tree, writer, spillAt);
        foreach (var (key, value) in entries)
        {
            builder.Add(key, value);
            if (builder.ShouldSpill)
            {
                await builder.SpillAsync(Ct);
            }
        }

        Assert.Equal(entries.Count, builder.Count);
        return builder.Complete();
    }

    private static async Task AssertHoldsAsync(IPageSource source, PageNumber root, List<(byte[] Key, byte[] Value)> entries)
    {
        await BTreeVerifier.VerifyAsync(source, root, Ct);
        var found = new List<(string, string)>();
        await foreach (var entry in BTree.RangeAsync(source, root, default, default, Ct))
        {
            found.Add((Convert.ToHexString(entry.Key.Span), Convert.ToHexString(entry.Value.Span)));
        }

        Assert.Equal(entries.Select(static pair => (Convert.ToHexString(pair.Key), Convert.ToHexString(pair.Value))), found);
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
