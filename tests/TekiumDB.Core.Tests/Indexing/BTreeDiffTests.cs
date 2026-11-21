// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using CsCheck;
using TekiumDB.Indexing;
using TekiumDB.Storage;
using TekiumDB.Tests.Storage;

namespace TekiumDB.Tests.Indexing;

/// <summary>
/// Lo que cambió entre dos estados de un árbol, sin leer lo que comparten. Es la base del change feed:
/// el modelo es comparar dos diccionarios.
/// </summary>
public sealed class BTreeDiffTests
{
    private static readonly PagerOptions Options = new() { PageSize = PageSize.FromBytes(4096), CachePages = 64 };

    // Clave, longitud del valor (una de cada diez grande, a páginas de overflow), relleno, y si es un borrado.
    private static readonly Gen<(int Key, int Length, byte Fill, bool Delete)> AnyOperation =
        Gen.Select(Gen.Int[0, 299], Gen.Int[0, 9], Gen.Int[0, 600], Gen.Byte, Gen.Int[0, 3])
            .Select(static (key, large, length, fill, delete) => (key, large == 0 ? 3000 + length : length, fill, delete == 0));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_difference_between_two_states_is_what_comparing_them_key_by_key_gives()
    {
        await Gen.Select(AnyOperation.Array[0, 400], AnyOperation.Array[0, 120]).SampleAsync(
            async (first, second) =>
            {
                await using var pager = await Pager.CreateAsync(TestDevice.Memory(), Options, Ct);
                var tree = new BTree(pager);
                var before = new SortedDictionary<int, byte[]>();
                await ApplyAsync(pager, tree, first, before);
                using var old = pager.BeginRead();
                var after = new SortedDictionary<int, byte[]>(before);
                await ApplyAsync(pager, tree, second, after);
                using var current = pager.BeginRead();

                Assert.Equal(Expected(before, after), await DiffAsync(pager, old.Root, current.Root));

                // Al revés es el cambio contrario, y un estado no difiere de sí mismo.
                Assert.Equal(Expected(after, before), await DiffAsync(pager, current.Root, old.Root));
                Assert.Empty(await DiffAsync(pager, current.Root, current.Root));
            },
            iter: 300);
    }

    [Fact]
    public async Task A_tree_differs_from_nothing_by_everything_it_has()
    {
        await using var pager = await Pager.CreateAsync(TestDevice.Memory(), Options, Ct);
        var tree = new BTree(pager);
        var model = new SortedDictionary<int, byte[]>();
        await ApplyAsync(pager, tree, [.. Enumerable.Range(0, 2000).Select(static i => (i, i % 50 == 0 ? 5000 : 20, (byte)i, false))], model);
        using var current = pager.BeginRead();
        var empty = new SortedDictionary<int, byte[]>();

        Assert.Equal(Expected(empty, model), await DiffAsync(pager, PageNumber.None, current.Root));
        Assert.Equal(Expected(model, empty), await DiffAsync(pager, current.Root, PageNumber.None));
        Assert.Empty(await DiffAsync(pager, PageNumber.None, PageNumber.None));
    }

    [Fact]
    public async Task A_value_written_again_exactly_as_it_was_is_not_a_change()
    {
        await using var pager = await Pager.CreateAsync(TestDevice.Memory(), Options, Ct);
        var tree = new BTree(pager);
        var model = new SortedDictionary<int, byte[]>();
        await ApplyAsync(pager, tree, [(1, 20, 7, false), (2, 9000, 8, false), (3, 9000, 9, false)], model);
        using var old = pager.BeginRead();

        // El grande se reescribe en páginas nuevas, con otra referencia en la hoja, pero dice lo mismo.
        await ApplyAsync(pager, tree, [(1, 20, 7, false), (2, 9000, 8, false), (3, 9000, 10, false)], model);
        using var current = pager.BeginRead();

        Assert.Equal(3, Assert.Single(await DiffAsync(pager, old.Root, current.Root)).Key);
    }

    /// <summary>
    /// Un estado retenido sigue entero aunque nadie lo esté leyendo: desde el piso de retención, o fijado
    /// uno por uno mientras el piso avanza.
    /// </summary>
    [Fact]
    public async Task A_retained_state_stays_readable_while_the_writer_moves_on()
    {
        await using var pager = await Pager.CreateAsync(TestDevice.Memory(), Options, Ct);
        var tree = new BTree(pager);
        var model = new SortedDictionary<int, byte[]>();
        await ApplyAsync(pager, tree, [.. Enumerable.Range(0, 500).Select(static i => (i, 40, (byte)i, false))], model);
        var kept = pager.Current;
        var expected = Expected([], model);
        pager.SetRetentionFloor(kept.TransactionId.Value);

        async Task ChurnAsync(int rounds)
        {
            var scratch = new SortedDictionary<int, byte[]>();
            for (var round = 0; round < rounds; round++)
            {
                await ApplyAsync(pager, tree, [.. Enumerable.Range(0, 500).Select(i => (i, 40, (byte)(i + round + 1), false))], scratch);
            }
        }

        // Por el piso.
        await ChurnAsync(10);
        Assert.Equal(expected, await DiffAsync(pager, PageNumber.None, kept.Root));

        // Fijado: el piso pasa de largo y el estado sigue ahí hasta que se suelta.
        Assert.True(pager.TryRetain(kept.TransactionId.Value, out var pin));
        pager.SetRetentionFloor(pager.Current.TransactionId.Value);
        var size = pager.Current.PageCount;
        await ChurnAsync(10);
        Assert.Equal(expected, await DiffAsync(pager, PageNumber.None, kept.Root));
        Assert.True(pager.Current.PageCount > size, "Nothing was written past the retained pages.");

        // Suelto, sus páginas vuelven a usarse y el archivo deja de crecer; y ya no puede fijarse.
        pager.Release(pin);
        Assert.False(pager.TryRetain(kept.TransactionId.Value, out _));
        pager.SetRetentionFloor(null);
        await ChurnAsync(3);
        size = pager.Current.PageCount;
        await ChurnAsync(10);
        Assert.Equal(size, pager.Current.PageCount);
        await BTreeVerifier.AssertNoLeaksAsync(pager, Ct);
    }

    /// <summary>
    /// Copy-on-write deja intacto casi todo el árbol entre un commit y el siguiente: la comparación sólo
    /// baja por donde las páginas son distintas.
    /// </summary>
    [Fact]
    public async Task Comparing_two_states_reads_only_the_pages_that_differ()
    {
        await using var pager = await Pager.CreateAsync(TestDevice.Memory(), Options with { CachePages = 4096 }, Ct);
        var tree = new BTree(pager);
        var model = new SortedDictionary<int, byte[]>();
        await ApplyAsync(pager, tree, [.. Enumerable.Range(0, 60_000).Select(static i => (i, 24, (byte)i, false))], model);
        using var old = pager.BeginRead();
        var before = new SortedDictionary<int, byte[]>(model);
        await ApplyAsync(pager, tree, [(7, 24, 0xEE, false), (30_000, 0, 0, true), (59_999, 30, 0xEE, false), (70_000, 8, 1, false)], model);
        using var current = pager.BeginRead();

        var counting = new CountingSource(pager);
        var changes = new List<(int, string?, string?)>();
        await foreach (var change in BTree.DiffAsync(counting, old.Root, current.Root, Ct))
        {
            changes.Add(Describe(change));
        }

        Assert.Equal(Expected(before, model), changes);
        Assert.Equal(4, changes.Count);

        // El árbol tiene cientos de hojas; cuatro cambios tocan cuatro caminos de raíz a hoja en cada estado.
        Assert.True(counting.Pins < 60, $"The comparison read {counting.Pins} pages.");

        // Con una hoja de diferencia de altura entre los dos estados tampoco se recorre todo.
        await using var writer = await pager.BeginWriteAsync(Ct);
        var small = PageNumber.None;
        small = await tree.PutAsync(writer, small, Key(1), new byte[10], Ct);
        counting = new CountingSource(writer);
        var count = 0;
        await foreach (var change in BTree.DiffAsync(counting, small, writer.Root, Ct))
        {
            count++;
        }

        Assert.Equal(model.Count, count);
    }

    private static List<(int Key, string? Before, string? After)> Expected(SortedDictionary<int, byte[]> before, SortedDictionary<int, byte[]> after)
    {
        var expected = new List<(int, string?, string?)>();
        foreach (var key in before.Keys.Union(after.Keys).Order())
        {
            var had = before.TryGetValue(key, out var old);
            var has = after.TryGetValue(key, out var current);
            if (had != has || !old.AsSpan().SequenceEqual(current))
            {
                expected.Add((key, had ? Convert.ToHexString(old!) : null, has ? Convert.ToHexString(current!) : null));
            }
        }

        return expected;
    }

    private static async Task<List<(int Key, string? Before, string? After)>> DiffAsync(IPageSource source, PageNumber before, PageNumber after)
    {
        var changes = new List<(int, string?, string?)>();
        await foreach (var change in BTree.DiffAsync(source, before, after, Ct))
        {
            changes.Add(Describe(change));
        }

        return changes;
    }

    private static (int Key, string? Before, string? After) Describe(BTreeChange change) => (
        BinaryPrimitives.ReadInt32BigEndian(change.Key.Span),
        change.HasBefore ? Convert.ToHexString(change.Before.Span) : null,
        change.HasAfter ? Convert.ToHexString(change.After.Span) : null);

    private static async Task ApplyAsync(
        Pager pager, BTree tree, (int Key, int Length, byte Fill, bool Delete)[] operations, SortedDictionary<int, byte[]> model)
    {
        await using var writer = await pager.BeginWriteAsync(Ct);
        foreach (var (key, length, fill, delete) in operations)
        {
            if (delete)
            {
                await tree.DeleteAsync(writer, Key(key), Ct);
                model.Remove(key);
            }
            else
            {
                var value = new byte[length];
                value.AsSpan().Fill(fill);
                await tree.PutAsync(writer, Key(key), value, Ct);
                model[key] = value;
            }
        }

        await writer.CommitAsync(Ct);
    }

    private static byte[] Key(int value)
    {
        var key = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(key, value);
        return key;
    }

    /// <summary>Cuenta las páginas que se piden.</summary>
    private sealed class CountingSource(IPageSource inner) : IPageSource
    {
        public int Pins { get; private set; }

        public bool TryPin(PageNumber number, out PageLease lease)
        {
            Pins++;
            return inner.TryPin(number, out lease);
        }

        public ValueTask<PageLease> PinAsync(PageNumber number, CancellationToken cancellationToken) =>
            inner.PinAsync(number, cancellationToken);
    }
}
