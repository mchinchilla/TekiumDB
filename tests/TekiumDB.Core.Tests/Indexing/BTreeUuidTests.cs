// Copyright (c) Tekium. Licensed under the MIT License.

using TekiumDB.Indexing;
using TekiumDB.Storage;
using TekiumDB.Tests.Storage;

namespace TekiumDB.Tests.Indexing;

/// <summary>
/// Inserción por UUID v7 tal como llegan de verdad: crecen de un milisegundo al siguiente, pero los de un
/// mismo milisegundo llegan en cualquier orden, porque lo que sigue a la marca de tiempo es azar.
/// </summary>
public sealed class BTreeUuidTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Comparer<byte[]> ByteOrder =
        Comparer<byte[]>.Create(static (a, b) => a.AsSpan().SequenceCompareTo(b));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Criterio de salida de la Fase 8. Una hoja de 8 KiB lleva unas 66 de estas filas: se prueba con
    /// menos de una hoja por milisegundo, que es donde las inserciones caen todas en la última hoja.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    public async Task Uuid_v7_inserts_leave_the_leaves_more_than_ninety_percent_full(int perMillisecond)
    {
        await using var pager = await Pager.CreateAsync(TestDevice.Memory(), new PagerOptions { CachePages = 64 }, Ct);
        var tree = new BTree(pager);
        var keys = Keys(40_000, perMillisecond);
        await InsertAsync(pager, tree, keys);

        using var read = pager.BeginRead();
        var (used, capacity, leaves) = await LeafSpaceAsync(pager, read.Root);
        Assert.True(used >= capacity * 0.9, $"{leaves} leaves are {100.0 * used / capacity:F1}% full.");
        await AssertHoldsAsync(pager, tree, read.Root, keys);
    }

    /// <summary>Muchas hojas por milisegundo: dentro del milisegundo es inserción al azar, y no debe ser peor que eso.</summary>
    [Fact]
    public async Task Many_leaves_per_millisecond_are_no_worse_than_random_inserts()
    {
        await using var pager = await Pager.CreateAsync(TestDevice.Memory(), new PagerOptions { CachePages = 64 }, Ct);
        var tree = new BTree(pager);
        var keys = Keys(40_000, 2000);
        await InsertAsync(pager, tree, keys);

        using var read = pager.BeginRead();
        var (used, capacity, leaves) = await LeafSpaceAsync(pager, read.Root);
        Assert.True(used >= capacity * 0.6, $"{leaves} leaves are {100.0 * used / capacity:F1}% full.");
        await AssertHoldsAsync(pager, tree, read.Root, keys);
    }

    /// <summary>
    /// Claves casi en orden de cualquier largo, con valores chicos y grandes: pasar entradas a la última
    /// hoja cambia la clave que las separa en el padre por otra de otro largo, que puede no caber.
    /// </summary>
    [Fact]
    public async Task Nearly_ordered_keys_of_any_length_match_the_model()
    {
        await using var pager = await Pager.CreateAsync(
            TestDevice.Memory(), new PagerOptions { PageSize = PageSize.FromBytes(4096), CachePages = 64 }, Ct);
        var tree = new BTree(pager);
        var random = new Random(88);
        var model = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var keys = new List<byte[]>();
        for (var i = 0; i < 12_000; i++)
        {
            // Los primeros cuatro bytes crecen; el resto, de 0 a 246 bytes, es azar.
            var key = new byte[4 + (random.Next(3) == 0 ? random.Next(200, 247) : random.Next(0, 12))];
            random.NextBytes(key);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(key, i);
            keys.Add(key);
        }

        // Desorden local: cada clave puede llegar hasta veinte lugares antes o después.
        var arrival = keys.Select((key, i) => (Key: key, At: i + random.Next(-20, 21))).OrderBy(static pair => pair.At).Select(static pair => pair.Key).ToList();
        for (var start = 0; start < arrival.Count; start += 500)
        {
            await using var tx = await pager.BeginWriteAsync(Ct);
            foreach (var key in arrival.Skip(start).Take(500))
            {
                var value = new byte[random.Next(25) == 0 ? random.Next(1100, 5000) : random.Next(0, 60)];
                random.NextBytes(value);
                await tree.PutAsync(tx, key, value, Ct);
                model[Convert.ToHexString(key)] = Convert.ToHexString(value);
            }

            await tx.CommitAsync(Ct);
        }

        using var read = pager.BeginRead();
        var actual = new List<KeyValuePair<string, string>>();
        await foreach (var entry in tree.RangeAsync(default, default, Ct))
        {
            actual.Add(new(Convert.ToHexString(entry.Key.Span), Convert.ToHexString(entry.Value.Span)));
        }

        Assert.Equal(model.OrderBy(static pair => Convert.FromHexString(pair.Key), ByteOrder), actual);
        await BTreeVerifier.VerifyAsync(pager, read.Root, Ct);
    }

    /// <summary>UUID v7 en el orden en que se crearon: <paramref name="perMillisecond"/> por cada milisegundo.</summary>
    internal static byte[][] Keys(int count, int perMillisecond)
    {
        // El azar de Guid.CreateVersion7 no se puede sembrar: se reemplaza por uno que sí, para que la
        // prueba sea la misma cada vez. La marca de tiempo y la versión quedan donde las puso .NET.
        var random = new Random(8);
        var keys = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            var key = Guid.CreateVersion7(Epoch.AddMilliseconds(i / perMillisecond)).ToByteArray(bigEndian: true);
            random.NextBytes(key.AsSpan(8));
            key[7] = (byte)random.Next();
            keys[i] = key;
        }

        return keys;
    }

    internal static async Task<(long Used, long Capacity, int Leaves)> LeafSpaceAsync(IPageSource source, PageNumber root)
    {
        long used = 0;
        long capacity = 0;
        var leaves = 0;
        var pending = new Stack<PageNumber>();
        pending.Push(root);
        while (pending.TryPop(out var number))
        {
            using var page = await source.PinAsync(number, Ct);
            var node = page.Span;
            if (BTreeNode.IsLeaf(node))
            {
                used += BTreeNode.UsedSpace(node);
                capacity += BTreeNode.Capacity(node);
                leaves++;
                continue;
            }

            for (var i = 0; i <= BTreeNode.Count(node); i++)
            {
                pending.Push(BTreeNode.Child(node, i));
            }
        }

        return (used, capacity, leaves);
    }

    private static async Task InsertAsync(Pager pager, BTree tree, byte[][] keys)
    {
        var value = new byte[100];
        for (var start = 0; start < keys.Length; start += 5000)
        {
            await using var tx = await pager.BeginWriteAsync(Ct);
            for (var i = start; i < Math.Min(start + 5000, keys.Length); i++)
            {
                await tree.PutAsync(tx, keys[i], value, Ct);
            }

            await tx.CommitAsync(Ct);
        }
    }

    private static async Task AssertHoldsAsync(Pager pager, BTree tree, PageNumber root, byte[][] keys)
    {
        var expected = keys.Select(Convert.ToHexString).Order(StringComparer.Ordinal).ToList();
        var actual = new List<string>();
        await foreach (var entry in tree.RangeAsync(default, default, Ct))
        {
            actual.Add(Convert.ToHexString(entry.Key.Span));
        }

        Assert.Equal(expected, actual);
        await BTreeVerifier.VerifyAsync(pager, root, Ct);
    }
}
