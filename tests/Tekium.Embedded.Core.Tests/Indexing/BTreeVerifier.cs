// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Indexing;
using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Tests.Indexing;

/// <summary>
/// Recorre el árbol entero y comprueba su estructura, no sólo su contenido: un árbol puede devolver
/// las claves correctas y aun así estar roto por dentro.
/// </summary>
internal static class BTreeVerifier
{
    /// <summary>Devuelve cuántas páginas alcanza el árbol.</summary>
    public static async Task<int> VerifyAsync(IPageSource source, PageNumber root, CancellationToken cancellationToken)
    {
        if (root.IsNone)
        {
            return 0;
        }

        var visited = new HashSet<uint>();
        var leafDepth = -1;
        await VisitAsync(root, null, null, depth: 0, parentKeys: -1);
        return visited.Count;

        async Task VisitAsync(PageNumber number, byte[]? lower, byte[]? upper, int depth, int parentKeys)
        {
            Assert.True(visited.Add(number.Value), $"Page {number.Value} is reachable twice.");
            Assert.True(depth < 16, "The tree is deeper than any valid tree can be.");

            // Se copia lo necesario y se suelta la página: el recorrido no debe retener la caché.
            bool leaf;
            byte[][] keys;
            PageNumber[] children;
            var chains = new List<(int Length, PageNumber First)>();
            using (var page = await source.PinAsync(number, cancellationToken))
            {
                var node = page.Span;
                leaf = BTreeNode.IsLeaf(node);
                Assert.Equal(leaf ? PageType.BTreeLeaf : PageType.BTreeInternal, page.Type);
                var count = BTreeNode.Count(node);
                keys = new byte[count][];
                for (var i = 0; i < count; i++)
                {
                    keys[i] = BTreeNode.Key(node, i).ToArray();
                    if (leaf && BTreeNode.IsOverflow(node, i))
                    {
                        BTreeNode.ReadOverflowReference(BTreeNode.LeafValue(node, i), out var length, out var first);
                        chains.Add((length, first));
                    }
                }

                children = new PageNumber[leaf ? 0 : count + 1];
                for (var i = 0; i < children.Length; i++)
                {
                    children[i] = BTreeNode.Child(node, i);
                }

                Assert.True(BTreeNode.FreeSpace(node) >= 0, $"Page {number.Value} has overlapping cells and slots.");
                Assert.True(
                    BTreeNode.UsedSpace(node) <= BTreeNode.Capacity(node),
                    $"Page {number.Value} claims to use more space than it has.");
            }

            for (var i = 0; i < keys.Length; i++)
            {
                Assert.True(
                    i == 0 || keys[i - 1].AsSpan().SequenceCompareTo(keys[i]) < 0,
                    $"Page {number.Value}: keys are not strictly ascending at {i}.");
                Assert.True(
                    lower is null || keys[i].AsSpan().SequenceCompareTo(lower) >= 0,
                    $"Page {number.Value}: key {i} is below the range its parent gives it.");
                Assert.True(
                    upper is null || keys[i].AsSpan().SequenceCompareTo(upper) < 0,
                    $"Page {number.Value}: key {i} is at or above the range its parent gives it.");
            }

            // Las páginas de overflow de cada valor grande: exactamente las que su longitud pide, todas de
            // ese tipo, y ninguna compartida.
            foreach (var (length, first) in chains)
            {
                var next = first;
                for (var remaining = length; remaining > 0;)
                {
                    Assert.False(next.IsNone, $"Page {number.Value}: a large value has fewer pages than its length needs.");
                    Assert.True(visited.Add(next.Value), $"Overflow page {next.Value} is reachable twice.");
                    using var overflow = await source.PinAsync(next, cancellationToken);
                    Assert.Equal(PageType.Overflow, overflow.Type);
                    remaining -= overflow.Span.Length - 4;
                    next = new PageNumber(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(overflow.Span));
                    Assert.True(remaining > 0 || next.IsNone, $"Page {number.Value}: a large value has more pages than its length needs.");
                }
            }

            if (leaf)
            {
                Assert.True(leafDepth < 0 || leafDepth == depth, $"Leaf {number.Value} is at a different depth.");
                leafDepth = depth;

                // Una hoja vacía sólo se tolera como raíz o como hija única de un nodo sin claves.
                Assert.True(
                    keys.Length > 0 || parentKeys <= 0,
                    $"Leaf {number.Value} is empty but has siblings it should have merged with.");
                return;
            }

            for (var i = 0; i < children.Length; i++)
            {
                Assert.False(children[i].IsNone, $"Page {number.Value}: child {i} is missing.");
                await VisitAsync(
                    children[i],
                    i == 0 ? lower : keys[i - 1],
                    i == keys.Length ? upper : keys[i],
                    depth + 1,
                    keys.Length);
            }
        }
    }

    /// <summary>Verifica el árbol y devuelve cuántas entradas tiene.</summary>
    public static async Task<int> CountAsync(IPageSource source, PageNumber root, CancellationToken cancellationToken)
    {
        await VerifyAsync(source, root, cancellationToken);
        var count = 0;
        await foreach (var unused in BTree.RangeAsync(source, root, default, default, cancellationToken))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Ninguna página se pierde: toda página del archivo es de cabecera, la alcanza el árbol, o está en
    /// la free-list (como página libre o como página de la propia lista).
    /// </summary>
    public static async Task AssertNoLeaksAsync(Pager pager, CancellationToken cancellationToken)
    {
        using var read = pager.BeginRead();
        var reachable = await VerifyAsync(pager, read.Root, cancellationToken);

        Assert.Equal(
            read.Snapshot.PageCount,
            FileHeader.MetaPages + (uint)reachable + (uint)pager.FreePageCount + (uint)pager.FreeListPageCount);
    }
}
