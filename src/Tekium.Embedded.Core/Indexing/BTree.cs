// Copyright (c) Tekium. Licensed under the MIT License.

using System.Runtime.CompilerServices;
using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Indexing;

/// <summary>
/// Copy-on-write B+Tree over byte keys compared lexicographically. A write never touches a committed page:
/// it copies the path from the root to the leaf, so every older root keeps describing a complete tree.
/// </summary>
/// <remarks>
/// Phase 0.5 spike: put, point lookup and range scan. No delete, no merge, no overflow pages.
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
    private readonly byte[][] _pathPages = new byte[MaxDepth][];
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

    /// <summary>Copies the value of <paramref name="key"/> in the latest committed state.</summary>
    /// <returns>The value length, or <see cref="NotFound"/>.</returns>
    public ValueTask<int> GetAsync(
        ReadOnlyMemory<byte> key, Memory<byte> destination, CancellationToken cancellationToken) =>
        GetAsync(_pager, _pager.Current.Root, key, destination, cancellationToken);

    /// <summary>Copies the value of <paramref name="key"/> in the tree rooted at <paramref name="root"/>.</summary>
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
            if (!source.TryGetCachedPage(number, out var page))
            {
                return GetSlowAsync(source, number, key, destination, cancellationToken);
            }

            if (BTreeNode.IsLeaf(page))
            {
                return new ValueTask<int>(ReadLeaf(page, key.Span, destination.Span));
            }

            number = BTreeNode.Child(page, BTreeNode.FindChildIndex(page, key.Span));
        }

        return new ValueTask<int>(NotFound);
    }

    /// <summary>Scans the latest committed state. See the overload with an explicit root.</summary>
    public IAsyncEnumerable<BTreeEntry> RangeAsync(
        ReadOnlyMemory<byte> from, ReadOnlyMemory<byte> to, CancellationToken cancellationToken) =>
        RangeAsync(_pager, _pager.Current.Root, from, to, cancellationToken);

    /// <summary>
    /// Scans keys in order from <paramref name="from"/> (inclusive; empty means the first key)
    /// up to <paramref name="to"/> (exclusive; empty means no upper bound).
    /// </summary>
    public static async IAsyncEnumerable<BTreeEntry> RangeAsync(
        IPageSource source,
        PageNumber root,
        ReadOnlyMemory<byte> from,
        ReadOnlyMemory<byte> to,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (root.IsNone)
        {
            yield break;
        }

        // Leaves have no sibling links (copy-on-write could not keep them), so the scan keeps its path.
        var pages = new byte[MaxDepth][];
        var indexes = new int[MaxDepth];
        var depth = 0;
        var page = await source.GetPageAsync(root, cancellationToken).ConfigureAwait(false);
        while (!BTreeNode.IsLeaf(page))
        {
            pages[depth] = page;
            indexes[depth] = BTreeNode.FindChildIndex(page, from.Span);
            page = await source.GetPageAsync(BTreeNode.Child(page, indexes[depth]), cancellationToken)
                .ConfigureAwait(false);
            depth++;
        }

        var index = BTreeNode.LowerBound(page, from.Span, out _);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = BTreeNode.Count(page);
            for (; index < count; index++)
            {
                BTreeNode.LeafKeyRange(page, index, out var keyOffset, out var keyLength);
                if (!to.IsEmpty && page.AsSpan(keyOffset, keyLength).SequenceCompareTo(to.Span) >= 0)
                {
                    yield break;
                }

                BTreeNode.LeafValueRange(page, index, out var valueOffset, out var valueLength);
                yield return new BTreeEntry(
                    new ReadOnlyMemory<byte>(page, keyOffset, keyLength),
                    new ReadOnlyMemory<byte>(page, valueOffset, valueLength));
            }

            // Climb until some ancestor still has a child to the right, then go down its leftmost path.
            while (depth > 0 && indexes[depth - 1] >= BTreeNode.Count(pages[depth - 1]))
            {
                depth--;
            }

            if (depth == 0)
            {
                yield break;
            }

            indexes[depth - 1]++;
            page = await source.GetPageAsync(BTreeNode.Child(pages[depth - 1], indexes[depth - 1]), cancellationToken)
                .ConfigureAwait(false);
            while (!BTreeNode.IsLeaf(page))
            {
                pages[depth] = page;
                indexes[depth] = 0;
                page = await source.GetPageAsync(BTreeNode.Child(page, 0), cancellationToken).ConfigureAwait(false);
                depth++;
            }

            index = 0;
        }
    }

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
            var first = transaction.Allocate(out var firstNumber);
            BTreeNode.Initialize(first, leaf: true, firstNumber, transaction.Id);
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

            if (!transaction.TryGetCachedPage(number, out var page))
            {
                page = await transaction.GetPageAsync(number, cancellationToken).ConfigureAwait(false);
            }

            if (!transaction.IsWritable(number))
            {
                page = transaction.CopyForWrite(page, out number);
                BTreeNode.Stamp(page, number, transaction.Id);
                if (depth == 0)
                {
                    transaction.Root = number;
                }
                else
                {
                    BTreeNode.SetChild(_pathPages[depth - 1], _pathIndexes[depth - 1], number);
                }
            }

            _pathPages[depth] = page;
            _pathNumbers[depth] = number;
            if (BTreeNode.IsLeaf(page))
            {
                break;
            }

            var child = BTreeNode.FindChildIndex(page, key.Span);
            rightmostPath &= child == BTreeNode.Count(page);
            _pathIndexes[depth] = child;
            number = BTreeNode.Child(page, child);
            depth++;
        }

        InsertIntoLeaf(transaction, depth, key.Span, value.Span, rightmostPath);
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
            var page = await source.GetPageAsync(number, cancellationToken).ConfigureAwait(false);
            if (BTreeNode.IsLeaf(page))
            {
                return ReadLeaf(page, key.Span, destination.Span);
            }

            number = BTreeNode.Child(page, BTreeNode.FindChildIndex(page, key.Span));
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
        Span<byte> leaf = _pathPages[depth];
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

        Span<byte> right = transaction.Allocate(out var rightNumber);
        BTreeNode.Initialize(right, leaf: true, rightNumber, transaction.Id);
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
                Span<byte> root = transaction.Allocate(out var rootNumber);
                BTreeNode.Initialize(root, leaf: false, rootNumber, transaction.Id);
                BTreeNode.SetChild(root, 0, _pathNumbers[0]);
                Insert(root, 0, separator, right);
                transaction.Root = rootNumber;
                return;
            }

            Span<byte> node = _pathPages[level];
            var index = _pathIndexes[level];
            if (TryInsertSeparator(node, index, separator, right))
            {
                return;
            }

            Span<byte> sibling = transaction.Allocate(out var siblingNumber);
            BTreeNode.Initialize(sibling, leaf: false, siblingNumber, transaction.Id);
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
