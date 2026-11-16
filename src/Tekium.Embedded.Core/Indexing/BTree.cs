// Copyright (c) Tekium. Licensed under the MIT License.

using System.Runtime.CompilerServices;
using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Indexing;

/// <summary>
/// Copy-on-write B+Tree over byte keys compared lexicographically. A write never touches a committed page:
/// it copies the path from the root to the leaf, so every older root keeps describing a complete tree.
/// </summary>
/// <remarks>
/// Put, point lookup and range scan. Not yet: delete, merge, overflow pages.
/// Writes go through one <see cref="WriteTransaction"/> at a time; reads are safe from any thread.
/// </remarks>
internal sealed class BTree
{
    public const int NotFound = -1;

    private const int MaxDepth = 16;

    private readonly Pager _pager;
    private readonly int _maxKeySize;
    private readonly int _maxEntrySize;

    // Scratch state of the single writer: the path of the insert in progress and separator buffers.
    private readonly Memory<byte>[] _pathPages = new Memory<byte>[MaxDepth];
    private readonly PageNumber[] _pathNumbers = new PageNumber[MaxDepth];
    private readonly int[] _pathIndexes = new int[MaxDepth];
    private byte[] _separator;
    private byte[] _nextSeparator;

    public BTree(Pager pager)
    {
        _pager = pager;
        _maxKeySize = pager.PageSize.Bytes / 16;
        _maxEntrySize = (pager.PageSize.Bytes / 4) - 8;
        _separator = new byte[_maxKeySize];
        _nextSeparator = new byte[_maxKeySize];
    }

    /// <summary>Copies the value of <paramref name="key"/> in the tree rooted at <paramref name="root"/>.</summary>
    /// <remarks>The caller keeps the pages reachable: it holds a read snapshot or is the writer.</remarks>
    /// <returns>The value length, or <see cref="NotFound"/>.</returns>
    public static ValueTask<int> GetAsync(
        IPageSource source,
        PageNumber root,
        ReadOnlyMemory<byte> key,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        // Fast path: while every page on the way down is in memory, no state machine and no scheduler.
        var number = root;
        while (!number.IsNone)
        {
            if (!source.TryPin(number, out var page))
            {
                return GetSlowAsync(source, number, key, destination, cancellationToken);
            }

            using (page)
            {
                var node = page.Span;
                if (BTreeNode.IsLeaf(node))
                {
                    return new ValueTask<int>(ReadLeaf(node, key.Span, destination.Span));
                }

                number = BTreeNode.Child(node, BTreeNode.FindChildIndex(node, key.Span));
            }
        }

        return new ValueTask<int>(NotFound);
    }

    /// <summary>
    /// Scans keys in order from <paramref name="from"/> (inclusive; empty means the first key)
    /// up to <paramref name="to"/> (exclusive; empty means no upper bound).
    /// </summary>
    /// <remarks>
    /// Each entry points into its page and is valid until the enumerator advances.
    /// The caller keeps the pages reachable: it holds a read snapshot or is the writer.
    /// </remarks>
    public static IAsyncEnumerable<BTreeEntry> RangeAsync(
        IPageSource source,
        PageNumber root,
        ReadOnlyMemory<byte> from,
        ReadOnlyMemory<byte> to,
        CancellationToken cancellationToken) =>
        RangeCoreAsync(source, root, null, from, to, cancellationToken);

    /// <summary>Copies the value of <paramref name="key"/> in the latest committed state.</summary>
    /// <returns>The value length, or <see cref="NotFound"/>.</returns>
    public ValueTask<int> GetAsync(
        ReadOnlyMemory<byte> key, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var snapshot = _pager.BeginRead();
        ValueTask<int> pending;
        try
        {
            pending = GetAsync(_pager, snapshot.Root, key, destination, cancellationToken);
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }

        if (pending.IsCompletedSuccessfully)
        {
            snapshot.Dispose();
            return pending;
        }

        return AwaitThenRelease(pending, snapshot);

        static async ValueTask<int> AwaitThenRelease(ValueTask<int> pending, ReadSnapshot snapshot)
        {
            using (snapshot)
            {
                return await pending.ConfigureAwait(false);
            }
        }
    }

    /// <summary>Scans the latest committed state, which stays pinned until the enumeration ends.</summary>
    public IAsyncEnumerable<BTreeEntry> RangeAsync(
        ReadOnlyMemory<byte> from, ReadOnlyMemory<byte> to, CancellationToken cancellationToken) =>
        RangeCoreAsync(_pager, PageNumber.None, _pager, from, to, cancellationToken);

    /// <summary>Inserts the entry, or replaces the value if the key exists.</summary>
    public async ValueTask PutAsync(
        WriteTransaction transaction,
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken)
    {
        if (key.IsEmpty || key.Length > _maxKeySize)
        {
            throw new ArgumentException($"The key must be between 1 and {_maxKeySize} bytes.", nameof(key));
        }

        if (key.Length + value.Length > _maxEntrySize)
        {
            throw new ArgumentException(
                $"Key and value together must not exceed {_maxEntrySize} bytes.", nameof(value));
        }

        if (transaction.Root.IsNone)
        {
            var first = transaction.Allocate(PageType.BTreeLeaf, out var firstNumber);
            BTreeNode.Initialize(first.Span, leaf: true);
            transaction.Root = firstNumber;
        }

        // Walk down making every page on the path writable. After this loop the insert needs no I/O.
        var depth = 0;
        var number = transaction.Root;
        var rightmostPath = true;
        while (true)
        {
            if (depth == MaxDepth)
            {
                throw new InvalidDataException("The B+Tree is deeper than any valid tree can be.");
            }

            if (!transaction.TryGetWritable(number, out var page))
            {
                if (!transaction.TryPin(number, out var committed))
                {
                    committed = await transaction.PinAsync(number, cancellationToken).ConfigureAwait(false);
                }

                using (committed)
                {
                    page = transaction.CopyForWrite(committed, number, out number);
                }

                if (depth == 0)
                {
                    transaction.Root = number;
                }
                else
                {
                    BTreeNode.SetChild(_pathPages[depth - 1].Span, _pathIndexes[depth - 1], number);
                }
            }

            _pathPages[depth] = page;
            _pathNumbers[depth] = number;
            var node = page.Span;
            if (BTreeNode.IsLeaf(node))
            {
                break;
            }

            var child = BTreeNode.FindChildIndex(node, key.Span);
            rightmostPath &= child == BTreeNode.Count(node);
            _pathIndexes[depth] = child;
            number = BTreeNode.Child(node, child);
            depth++;
        }

        InsertIntoLeaf(transaction, depth, key.Span, value.Span, rightmostPath);
    }

    private static async IAsyncEnumerable<BTreeEntry> RangeCoreAsync(
        IPageSource source,
        PageNumber root,
        Pager? latestOf,
        ReadOnlyMemory<byte> from,
        ReadOnlyMemory<byte> to,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The snapshot is taken here, when the enumeration starts, so that an enumerable that is
        // never enumerated holds nothing.
        ReadSnapshot? owned = latestOf?.BeginRead();
        if (owned is { } snapshot)
        {
            root = snapshot.Root;
        }

        // Leaves have no sibling links (copy-on-write could not keep them), so the scan keeps its path,
        // pinned: levels [0, depth) are internal nodes and level depth is the current leaf.
        var pages = new PageLease[MaxDepth];
        var indexes = new int[MaxDepth];
        var depth = 0;
        var held = 0;
        try
        {
            if (root.IsNone)
            {
                yield break;
            }

            pages[0] = await source.PinAsync(root, cancellationToken).ConfigureAwait(false);
            held = 1;
            while (!BTreeNode.IsLeaf(pages[depth].Span))
            {
                indexes[depth] = BTreeNode.FindChildIndex(pages[depth].Span, from.Span);
                var child = BTreeNode.Child(pages[depth].Span, indexes[depth]);
                pages[depth + 1] = await source.PinAsync(child, cancellationToken).ConfigureAwait(false);
                depth++;
                held++;
            }

            var index = BTreeNode.LowerBound(pages[depth].Span, from.Span, out _);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var leaf = pages[depth].Payload;
                var count = BTreeNode.Count(leaf.Span);
                for (; index < count; index++)
                {
                    BTreeNode.LeafKeyRange(leaf.Span, index, out var keyOffset, out var keyLength);
                    if (!to.IsEmpty && leaf.Span.Slice(keyOffset, keyLength).SequenceCompareTo(to.Span) >= 0)
                    {
                        yield break;
                    }

                    BTreeNode.LeafValueRange(leaf.Span, index, out var valueOffset, out var valueLength);
                    yield return new BTreeEntry(
                        leaf.Slice(keyOffset, keyLength), leaf.Slice(valueOffset, valueLength));
                }

                // Climb until some ancestor still has a child to the right, then go down its leftmost path.
                pages[depth].Dispose();
                held--;
                while (depth > 0 && indexes[depth - 1] >= BTreeNode.Count(pages[depth - 1].Span))
                {
                    depth--;
                    pages[depth].Dispose();
                    held--;
                }

                if (depth == 0)
                {
                    yield break;
                }

                indexes[depth - 1]++;
                var next = BTreeNode.Child(pages[depth - 1].Span, indexes[depth - 1]);
                pages[depth] = await source.PinAsync(next, cancellationToken).ConfigureAwait(false);
                held++;
                while (!BTreeNode.IsLeaf(pages[depth].Span))
                {
                    indexes[depth] = 0;
                    var child = BTreeNode.Child(pages[depth].Span, 0);
                    pages[depth + 1] = await source.PinAsync(child, cancellationToken).ConfigureAwait(false);
                    depth++;
                    held++;
                }

                index = 0;
            }
        }
        finally
        {
            for (var i = 0; i < held; i++)
            {
                pages[i].Dispose();
            }

            owned?.Dispose();
        }
    }

    private static int ReadLeaf(ReadOnlySpan<byte> leaf, ReadOnlySpan<byte> key, Span<byte> destination)
    {
        var index = BTreeNode.LowerBound(leaf, key, out var found);
        if (!found)
        {
            return NotFound;
        }

        var value = BTreeNode.LeafValue(leaf, index);
        if (value.Length > destination.Length)
        {
            throw new ArgumentException(
                $"The value is {value.Length} bytes but the destination holds {destination.Length}.",
                nameof(destination));
        }

        value.CopyTo(destination);
        return value.Length;
    }

    private static async ValueTask<int> GetSlowAsync(
        IPageSource source,
        PageNumber number,
        ReadOnlyMemory<byte> key,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var page = await source.PinAsync(number, cancellationToken).ConfigureAwait(false);
            if (BTreeNode.IsLeaf(page.Span))
            {
                return ReadLeaf(page.Span, key.Span, destination.Span);
            }

            number = BTreeNode.Child(page.Span, BTreeNode.FindChildIndex(page.Span, key.Span));
        }
    }

    private static bool TryInsertLeaf(Span<byte> leaf, int index, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        if (BTreeNode.TryInsertLeaf(leaf, index, key, value))
        {
            return true;
        }

        if (BTreeNode.FreeSpace(leaf) + BTreeNode.Garbage(leaf) < BTreeNode.LeafEntrySize(key.Length, value.Length))
        {
            return false;
        }

        BTreeNode.Compact(leaf);
        return BTreeNode.TryInsertLeaf(leaf, index, key, value);
    }

    /// <summary>
    /// Records that the child at <paramref name="index"/> was split: <paramref name="separator"/> is the
    /// first key of the new sibling <paramref name="right"/>.
    /// </summary>
    private static bool TryInsertSeparator(Span<byte> node, int index, ReadOnlySpan<byte> separator, PageNumber right)
    {
        if (!BTreeNode.TryInsertInternal(node, index, BTreeNode.Child(node, index), separator))
        {
            return false;
        }

        // The slot that pointed to the split child moved one place right and now covers the new sibling.
        BTreeNode.SetChild(node, index + 1, right);
        return true;
    }

    private void InsertIntoLeaf(
        WriteTransaction transaction, int depth, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, bool rightmostPath)
    {
        var leaf = _pathPages[depth].Span;
        var index = BTreeNode.LowerBound(leaf, key, out var found);
        if (found)
        {
            BTreeNode.LeafValueRange(leaf, index, out var offset, out var length);
            if (length == value.Length)
            {
                value.CopyTo(leaf[offset..]);
                return;
            }

            BTreeNode.RemoveSlot(leaf, index);
        }

        if (TryInsertLeaf(leaf, index, key, value))
        {
            return;
        }

        var right = transaction.Allocate(PageType.BTreeLeaf, out var rightNumber).Span;
        BTreeNode.Initialize(right, leaf: true);
        int separatorLength;
        if (rightmostPath && index == BTreeNode.Count(leaf))
        {
            // Append: split 100/0. The full leaf stays full and the new key starts the next one.
            Insert(right, 0, key, value);
            key.CopyTo(_separator);
            separatorLength = key.Length;
        }
        else
        {
            var splitAt = BTreeNode.ChooseSplitIndex(leaf);
            BTreeNode.SplitLeaf(leaf, right, splitAt);
            if (index <= splitAt)
            {
                Insert(leaf, index, key, value);
            }
            else
            {
                Insert(right, index - splitAt, key, value);
            }

            var first = BTreeNode.Key(right, 0);
            first.CopyTo(_separator);
            separatorLength = first.Length;
        }

        InsertIntoParent(transaction, depth - 1, separatorLength, rightNumber, rightmostPath);

        static void Insert(Span<byte> node, int at, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
        {
            if (!TryInsertLeaf(node, at, key, value))
            {
                throw new InvalidDataException("A B+Tree leaf has no room for an entry after being split.");
            }
        }
    }

    /// <summary>Propagates a split upwards. The separator to insert is in <see cref="_separator"/>.</summary>
    private void InsertIntoParent(
        WriteTransaction transaction, int level, int separatorLength, PageNumber right, bool rightmostPath)
    {
        while (true)
        {
            var separator = _separator.AsSpan(0, separatorLength);
            if (level < 0)
            {
                // The root itself was split: the tree grows one level.
                var root = transaction.Allocate(PageType.BTreeInternal, out var rootNumber).Span;
                BTreeNode.Initialize(root, leaf: false);
                BTreeNode.SetChild(root, 0, _pathNumbers[0]);
                Insert(root, 0, separator, right);
                transaction.Root = rootNumber;
                return;
            }

            var node = _pathPages[level].Span;
            var index = _pathIndexes[level];
            if (TryInsertSeparator(node, index, separator, right))
            {
                return;
            }

            var sibling = transaction.Allocate(PageType.BTreeInternal, out var siblingNumber).Span;
            BTreeNode.Initialize(sibling, leaf: false);
            var count = BTreeNode.Count(node);
            var splitAt = rightmostPath ? count - 1 : BTreeNode.ChooseSplitIndex(node);
            var promotedLength = BTreeNode.SplitInternal(node, sibling, splitAt, _nextSeparator);
            if (index <= splitAt)
            {
                Insert(node, index, separator, right);
            }
            else
            {
                Insert(sibling, index - splitAt - 1, separator, right);
            }

            (_separator, _nextSeparator) = (_nextSeparator, _separator);
            separatorLength = promotedLength;
            right = siblingNumber;
            level--;
        }

        static void Insert(Span<byte> node, int at, ReadOnlySpan<byte> separator, PageNumber right)
        {
            if (!TryInsertSeparator(node, at, separator, right))
            {
                throw new InvalidDataException("A B+Tree node has no room for a separator after being split.");
            }
        }
    }
}
