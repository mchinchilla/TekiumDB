// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Indexing;
using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Tests.Indexing;

public sealed class BTreeNodeTests
{
    [Fact]
    public void A_new_leaf_is_empty()
    {
        var page = NewLeaf();

        Assert.True(BTreeNode.IsLeaf(page));
        Assert.Equal(0, BTreeNode.Count(page));
        Assert.Equal(4096 - BTreeNode.HeaderSize, BTreeNode.FreeSpace(page));
    }

    [Fact]
    public void Leaf_entries_inserted_out_of_order_are_kept_sorted()
    {
        var page = NewLeaf();
        foreach (var key in new byte[] { 30, 10, 20, 40 })
        {
            var index = BTreeNode.LowerBound(page, [key], out var found);
            Assert.False(found);
            Assert.True(BTreeNode.TryInsertLeaf(page, index, [key], [key, key]));
        }

        Assert.Equal(4, BTreeNode.Count(page));
        for (var i = 0; i < 4; i++)
        {
            var expected = (byte)((i + 1) * 10);
            Assert.Equal([expected], BTreeNode.Key(page, i).ToArray());
            Assert.Equal([expected, expected], BTreeNode.LeafValue(page, i).ToArray());
        }
    }

    [Fact]
    public void LowerBound_finds_existing_keys_and_the_insertion_point_of_missing_ones()
    {
        var page = NewLeaf();
        byte[][] keys = [[1], [3], [3, 0], [5]];
        for (var i = 0; i < keys.Length; i++)
        {
            Assert.True(BTreeNode.TryInsertLeaf(page, i, keys[i], []));
        }

        Assert.Equal(1, BTreeNode.LowerBound(page, [3], out var found));
        Assert.True(found);
        Assert.Equal(0, BTreeNode.LowerBound(page, [0], out found));
        Assert.False(found);
        Assert.Equal(3, BTreeNode.LowerBound(page, [4], out found));
        Assert.False(found);
        Assert.Equal(4, BTreeNode.LowerBound(page, [9], out found));
        Assert.False(found);
    }

    [Fact]
    public void A_full_leaf_rejects_the_insert_without_changing()
    {
        var page = NewLeaf();
        var value = new byte[500];
        var inserted = 0;
        while (BTreeNode.TryInsertLeaf(page, inserted, [(byte)inserted], value))
        {
            inserted++;
        }

        Assert.Equal(inserted, BTreeNode.Count(page));
        Assert.InRange(inserted, 7, 8);
    }

    [Fact]
    public void Removing_a_slot_leaves_garbage_that_compaction_reclaims()
    {
        var page = NewLeaf();
        for (var i = 0; i < 3; i++)
        {
            Assert.True(BTreeNode.TryInsertLeaf(page, i, [(byte)i], new byte[100]));
        }

        var freeBefore = BTreeNode.FreeSpace(page);
        BTreeNode.RemoveSlot(page, 1);

        Assert.Equal(2, BTreeNode.Count(page));
        Assert.Equal(105, BTreeNode.Garbage(page));

        BTreeNode.Compact(page);

        Assert.Equal(0, BTreeNode.Garbage(page));
        Assert.Equal(freeBefore + 105 + 2, BTreeNode.FreeSpace(page));
        Assert.Equal([0], BTreeNode.Key(page, 0).ToArray());
        Assert.Equal([2], BTreeNode.Key(page, 1).ToArray());
    }

    [Fact]
    public void Splitting_a_leaf_moves_the_upper_entries_and_keeps_every_key()
    {
        var left = NewLeaf();
        for (var i = 0; i < 10; i++)
        {
            Assert.True(BTreeNode.TryInsertLeaf(left, i, [(byte)i], [(byte)(i * 2)]));
        }

        var right = new byte[4096];
        BTreeNode.Initialize(right, leaf: true);

        BTreeNode.SplitLeaf(left, right, 4);

        Assert.Equal(4, BTreeNode.Count(left));
        Assert.Equal(6, BTreeNode.Count(right));
        Assert.Equal([3], BTreeNode.Key(left, 3).ToArray());
        Assert.Equal([4], BTreeNode.Key(right, 0).ToArray());
        Assert.Equal([18], BTreeNode.LeafValue(right, 5).ToArray());
        Assert.Equal(0, BTreeNode.Garbage(left));
    }

    [Fact]
    public void An_internal_node_routes_keys_to_the_child_whose_range_contains_them()
    {
        var page = NewInternal();
        Assert.True(BTreeNode.TryInsertInternal(page, 0, new PageNumber(100), [10]));
        Assert.True(BTreeNode.TryInsertInternal(page, 1, new PageNumber(200), [20]));
        BTreeNode.SetChild(page, 2, new PageNumber(300));

        // Hijo i contiene las claves < K_i; las claves >= a la última van al hijo de más a la derecha.
        Assert.Equal(0, BTreeNode.FindChildIndex(page, [5]));
        Assert.Equal(1, BTreeNode.FindChildIndex(page, [10]));
        Assert.Equal(1, BTreeNode.FindChildIndex(page, [15]));
        Assert.Equal(2, BTreeNode.FindChildIndex(page, [20]));
        Assert.Equal(2, BTreeNode.FindChildIndex(page, [99]));
        Assert.Equal(new PageNumber(100), BTreeNode.Child(page, 0));
        Assert.Equal(new PageNumber(200), BTreeNode.Child(page, 1));
        Assert.Equal(new PageNumber(300), BTreeNode.Child(page, 2));
    }

    [Fact]
    public void Splitting_an_internal_node_pushes_the_middle_key_up()
    {
        var left = NewInternal();
        for (var i = 0; i < 5; i++)
        {
            Assert.True(BTreeNode.TryInsertInternal(left, i, new PageNumber((uint)(100 + i)), [(byte)((i + 1) * 10)]));
        }

        BTreeNode.SetChild(left, 5, new PageNumber(105));
        var right = new byte[4096];
        BTreeNode.Initialize(right, leaf: false);
        var separator = new byte[16];

        var length = BTreeNode.SplitInternal(left, right, 2, separator);

        Assert.Equal([30], separator[..length]);
        Assert.Equal(2, BTreeNode.Count(left));
        Assert.Equal(new PageNumber(102), BTreeNode.Child(left, 2));
        Assert.Equal(2, BTreeNode.Count(right));
        Assert.Equal([40], BTreeNode.Key(right, 0).ToArray());
        Assert.Equal(new PageNumber(103), BTreeNode.Child(right, 0));
        Assert.Equal(new PageNumber(105), BTreeNode.Child(right, 2));
    }

    private static byte[] NewLeaf()
    {
        var page = new byte[4096];
        BTreeNode.Initialize(page, leaf: true);
        return page;
    }

    private static byte[] NewInternal()
    {
        var page = new byte[4096];
        BTreeNode.Initialize(page, leaf: false);
        return page;
    }
}
