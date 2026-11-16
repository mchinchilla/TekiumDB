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
