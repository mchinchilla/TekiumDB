// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Storage;
using Tekium.Embedded.Tests.Storage;

namespace Tekium.Embedded.Tests.Indexing;

/// <summary>Varios árboles en el mismo archivo: cada uno con su raíz, que lleva quien lo usa.</summary>
public abstract class BTreeRootsTests : DeviceBackedTests
{
    private static readonly PagerOptions Options = new() { PageSize = PageSize.FromBytes(4096), CachePages = 64 };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_trees_in_one_file_do_not_see_each_other()
    {
        await using var pager = await Pager.CreateAsync(NewDevice(), Options, Ct);
        var tree = new BTree(pager);
        var first = PageNumber.None;
        var second = PageNumber.None;
        await using (var writer = await pager.BeginWriteAsync(Ct))
        {
            for (var i = 0; i < 2000; i++)
            {
                first = await tree.PutAsync(writer, first, Key(i), Key(i), Ct);
                if (i % 2 == 0)
                {
                    second = await tree.PutAsync(writer, second, Key(i), Key(-i), Ct);
                }
            }

            // Borrar en uno no toca al otro, y vaciarlo devuelve la raíz vacía.
            for (var i = 0; i < 2000; i += 2)
            {
                var (removed, root) = await tree.DeleteAsync(writer, second, Key(i), Ct);
                Assert.True(removed);
                second = root;
            }

            Assert.True(second.IsNone);
            var (missing, unchanged) = await tree.DeleteAsync(writer, first, Key(5000), Ct);
            Assert.False(missing);
            Assert.Equal(first, unchanged);

            Assert.Equal(2000, await BTreeVerifier.CountAsync(writer, first, Ct));
            Assert.True(writer.Root.IsNone, "Explicit roots must not touch the root of the transaction.");
            writer.Root = first;
            await writer.CommitAsync(Ct);
        }

        await BTreeVerifier.AssertNoLeaksAsync(pager, Ct);
    }

    /// <summary>Tirar un índice es liberar su árbol entero: nodos, hojas y las cadenas de los valores grandes.</summary>
    [Fact]
    public async Task Dropping_a_tree_frees_every_page_of_it_and_leaves_the_others_alone()
    {
        await using var pager = await Pager.CreateAsync(NewDevice(), Options, Ct);
        var tree = new BTree(pager);
        var kept = PageNumber.None;
        var dropped = PageNumber.None;
        await using (var writer = await pager.BeginWriteAsync(Ct))
        {
            for (var i = 0; i < 3000; i++)
            {
                kept = await tree.PutAsync(writer, kept, Key(i), Key(i), Ct);
                dropped = await tree.PutAsync(writer, dropped, Key(i), i % 100 == 0 ? new byte[9000] : Key(i), Ct);
            }

            writer.Root = kept;
            await writer.CommitAsync(Ct);
        }

        await using (var writer = await pager.BeginWriteAsync(Ct))
        {
            await BTree.DropAsync(writer, dropped, Ct);
            await BTree.DropAsync(writer, PageNumber.None, Ct);
            await writer.CommitAsync(Ct);
        }

        await using (var writer = await pager.BeginWriteAsync(Ct))
        {
            await BTreeVerifier.VerifyAsync(writer, writer.Root, Ct);
            Assert.Equal(3000, await BTreeVerifier.CountAsync(writer, writer.Root, Ct));
        }

        await BTreeVerifier.AssertNoLeaksAsync(pager, Ct);
    }

    private static byte[] Key(int value)
    {
        var key = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(key, value);
        return key;
    }
}

public sealed class BTreeRootsTests_File : BTreeRootsTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class BTreeRootsTests_Memory : BTreeRootsTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
