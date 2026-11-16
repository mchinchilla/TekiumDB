// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Storage;
using Tekium.Embedded.Tests.Storage;

namespace Tekium.Embedded.Tests.Indexing;

public sealed class BTreeDeleteTests
{
    private static readonly PagerOptions SmallPages = new() { PageSize = PageSize.FromBytes(4096), CachePages = 64 };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deleting_from_an_empty_tree_finds_nothing()
    {
        await using var pager = await NewPagerAsync();
        var tree = new BTree(pager);
        await using var tx = await pager.BeginWriteAsync(Ct);

        Assert.False(await tree.DeleteAsync(tx, Key(1), Ct));
    }

    [Fact]
    public async Task A_deleted_key_is_gone_and_its_neighbours_stay()
    {
        await using var pager = await NewPagerAsync();
        var tree = new BTree(pager);
        await FillAsync(pager, tree, 0, 100);

        await using (var tx = await pager.BeginWriteAsync(Ct))
        {
            Assert.True(await tree.DeleteAsync(tx, Key(50), Ct));
            Assert.False(await tree.DeleteAsync(tx, Key(50), Ct));
            await tx.CommitAsync(Ct);
        }

        var buffer = new byte[64];
        Assert.Equal(BTree.NotFound, await tree.GetAsync(Key(50), buffer, Ct));
        Assert.Equal(20, await tree.GetAsync(Key(49), buffer, Ct));
        Assert.Equal(20, await tree.GetAsync(Key(51), buffer, Ct));
        Assert.Equal(99, await CountAsync(tree));
    }

    [Fact]
    public async Task Deleting_a_missing_key_changes_nothing_on_disk()
    {
        await using var pager = await NewPagerAsync();
        var tree = new BTree(pager);
        await FillAsync(pager, tree, 0, 5000);
        var before = pager.Current;

        await using (var tx = await pager.BeginWriteAsync(Ct))
        {
            Assert.False(await tree.DeleteAsync(tx, Key(5000), Ct));
            Assert.False(await tree.DeleteAsync(tx, new byte[] { 1 }, Ct));
            await tx.CommitAsync(Ct);
        }

        // Buscar una clave que no existe no debe copiar el camino: nada que escribir, ningún commit.
        Assert.Equal(before, pager.Current);
    }

    [Fact]
    public async Task Deleting_every_key_leaves_an_empty_tree_and_returns_every_page()
    {
        await using var pager = await NewPagerAsync();
        var tree = new BTree(pager);
        await FillAsync(pager, tree, 0, 20_000);
        var full = pager.Current.PageCount;

        for (var start = 0; start < 20_000; start += 2500)
        {
            await using var tx = await pager.BeginWriteAsync(Ct);
            for (var i = start; i < start + 2500; i++)
            {
                Assert.True(await tree.DeleteAsync(tx, Key(i), Ct));
            }

            await tx.CommitAsync(Ct);
            await BTreeVerifier.AssertNoLeaksAsync(pager, Ct);
        }

        Assert.True(pager.Current.Root.IsNone);
        Assert.Equal(0, await CountAsync(tree));

        // El árbol vacío no retiene nada: lo que ocupaba queda disponible y el archivo deja de crecer.
        await FillAsync(pager, tree, 0, 20_000);
        await FillAsync(pager, tree, 0, 20_000);
        Assert.InRange(pager.Current.PageCount, full, full * 3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deleting_in_order_from_either_end_keeps_the_tree_valid(bool fromTheEnd)
    {
        await using var pager = await NewPagerAsync();
        var tree = new BTree(pager);
        const int Rows = 30_000;
        await FillAsync(pager, tree, 0, Rows);

        for (var done = 0; done < Rows - 10; done += 1999)
        {
            await using var tx = await pager.BeginWriteAsync(Ct);
            var until = Math.Min(done + 1999, Rows - 10);
            for (var i = done; i < until; i++)
            {
                Assert.True(await tree.DeleteAsync(tx, Key(fromTheEnd ? Rows - 1 - i : i), Ct));
            }

            await tx.CommitAsync(Ct);
            await BTreeVerifier.AssertNoLeaksAsync(pager, Ct);
        }

        Assert.Equal(10, await CountAsync(tree));
    }

    [Fact]
    public async Task Deleting_most_keys_shrinks_the_tree_instead_of_leaving_empty_leaves()
    {
        await using var pager = await NewPagerAsync();
        var tree = new BTree(pager);
        const int Rows = 40_000;
        await FillAsync(pager, tree, 0, Rows);
        int pagesFull;
        using (var read = pager.BeginRead())
        {
            pagesFull = await BTreeVerifier.VerifyAsync(pager, read.Root, Ct);
        }

        await using (var tx = await pager.BeginWriteAsync(Ct))
        {
            for (var i = 0; i < Rows; i++)
            {
                if (i % 20 != 0)
                {
                    Assert.True(await tree.DeleteAsync(tx, Key(i), Ct));
                }
            }

            await tx.CommitAsync(Ct);
        }

        using var after = pager.BeginRead();
        var pagesAfter = await BTreeVerifier.VerifyAsync(pager, after.Root, Ct);

        // Queda el 5 % de las filas: sin merge seguirían todas las hojas; con merge, unas pocas.
        Assert.Equal(Rows / 20, await CountAsync(tree));
        Assert.InRange(pagesAfter, 1, pagesFull / 4);
    }

    [Fact]
    public async Task A_rolled_back_delete_leaves_everything_in_place()
    {
        await using var pager = await NewPagerAsync();
        var tree = new BTree(pager);
        await FillAsync(pager, tree, 0, 5000);
        var before = pager.Current;

        await using (var tx = await pager.BeginWriteAsync(Ct))
        {
            for (var i = 0; i < 5000; i++)
            {
                await tree.DeleteAsync(tx, Key(i), Ct);
            }

            Assert.True(tx.Root.IsNone);
        }

        Assert.Equal(before, pager.Current);
        Assert.Equal(5000, await CountAsync(tree));
        await BTreeVerifier.AssertNoLeaksAsync(pager, Ct);
    }

    [Fact]
    public async Task A_reader_keeps_seeing_the_keys_deleted_after_it_started()
    {
        await using var pager = await NewPagerAsync();
        var tree = new BTree(pager);
        await FillAsync(pager, tree, 0, 6000);

        using var old = pager.BeginRead();
        for (var round = 0; round < 6; round++)
        {
            await using var tx = await pager.BeginWriteAsync(Ct);
            for (var i = round * 1000; i < (round + 1) * 1000; i++)
            {
                Assert.True(await tree.DeleteAsync(tx, Key(i), Ct));
            }

            await tx.CommitAsync(Ct);
        }

        var seen = 0;
        await foreach (var entry in BTree.RangeAsync(pager, old.Root, default, default, Ct))
        {
            seen++;
        }

        Assert.Equal(6000, seen);
        Assert.Equal(0, await CountAsync(tree));
    }

    private static async Task<Pager> NewPagerAsync() =>
        await Pager.CreateAsync(TestDevice.Memory(), SmallPages, Ct);

    private static byte[] Key(int value)
    {
        var key = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(key, value);
        return key;
    }

    private static async Task FillAsync(Pager pager, BTree tree, int from, int to)
    {
        await using var tx = await pager.BeginWriteAsync(Ct);
        for (var i = from; i < to; i++)
        {
            await tree.PutAsync(tx, Key(i), new byte[20], Ct);
        }

        await tx.CommitAsync(Ct);
    }

    private static async Task<int> CountAsync(BTree tree)
    {
        var count = 0;
        await foreach (var entry in tree.RangeAsync(default, default, Ct))
        {
            count++;
        }

        return count;
    }
}
