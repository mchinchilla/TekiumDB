// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Indexing;

/// <summary>
/// Copy-on-write B+Tree over byte keys compared lexicographically. A write never touches a committed page:
/// it copies the path from the root to the leaf, so every older root keeps describing a complete tree.
/// </summary>
/// <remarks>
/// Put, delete, point lookup and range scan. Not yet: overflow pages for large values.
/// Writes go through one <see cref="WriteTransaction"/> at a time; reads are safe from any thread.
/// </remarks>
internal sealed class BTree
{
    public const int NotFound = -1;

    /// <summary>The largest value the tree stores. A value that does not fit in a leaf goes to overflow pages.</summary>
    public const int MaxValueSize = 1 << 28;

    private const int MaxDepth = 16;

    // An overflow page starts with the number of the next page of the chain.
    private const int OverflowHeaderSize = 4;

    private readonly Pager _pager;
    private readonly int _maxKeySize;
    private readonly int _maxEntrySize;

    // Scratch state of the single writer: the path of the insert in progress and separator buffers.
    private readonly Memory<byte>[] _pathPages = new Memory<byte>[MaxDepth];
    private readonly PageNumber[] _pathNumbers = new PageNumber[MaxDepth];
    private readonly int[] _pathIndexes = new int[MaxDepth];
    private readonly byte[] _mergeScratch;
    private readonly byte[] _reference = new byte[BTreeNode.OverflowReferenceSize];
    private byte[] _separator;

    // Root of the tree the operation in progress works on. It changes as the path is copied.
    private PageNumber _root;
    private byte[] _nextSeparator;

    public BTree(Pager pager)
    {
        _pager = pager;
        _maxKeySize = pager.PageSize.Bytes / 16;
        _maxEntrySize = (pager.PageSize.Bytes / 4) - 8;
        _separator = new byte[_maxKeySize];
        _nextSeparator = new byte[_maxKeySize];
        _mergeScratch = new byte[PageLayout.PayloadSize(pager.PageSize)];
    }

    /// <summary>Gets the longest key the tree accepts.</summary>
    public int MaxKeySize => _maxKeySize;

    /// <summary>Gets the most bytes a key and its value may take together.</summary>
    public int MaxEntrySize => _maxEntrySize;

    /// <summary>Copies the value of <paramref name="key"/> in the tree rooted at <paramref name="root"/>.</summary>
    /// <remarks>The caller keeps the pages reachable: it holds a read snapshot or is the writer.</remarks>
    /// <returns>
    /// The value length, or <see cref="NotFound"/>, or a result for which <see cref="IsTooSmall"/> is true
    /// when <paramref name="destination"/> cannot hold the value.
    /// </returns>
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
                    var length = ReadLeaf(node, key.Span, destination.Span, out var chain);
                    return chain.IsNone
                        ? new ValueTask<int>(length)
                        : ReadChainAsync(source, chain, destination[..length], cancellationToken);
                }

                number = BTreeNode.Child(node, BTreeNode.FindChildIndex(node, key.Span));
            }
        }

        return new ValueTask<int>(NotFound);
    }

    /// <summary>Whether a result of <see cref="GetAsync(IPageSource, PageNumber, ReadOnlyMemory{byte}, Memory{byte}, CancellationToken)"/> says the destination was too small.</summary>
    /// <param name="result">What the read returned.</param>
    /// <param name="required">How many bytes the value needs.</param>
    public static bool IsTooSmall(int result, out int required)
    {
        required = result < NotFound ? NotFound - result : 0;
        return result < NotFound;
    }

    /// <summary>
    /// Reads a value of any size into a buffer rented from the shared pool, which the caller returns.
    /// </summary>
    /// <returns>The buffer and the value length, or no buffer if the key is not there.</returns>
    public static async ValueTask<(byte[]? Buffer, int Length)> GetRentedAsync(
        IPageSource source, PageNumber root, ReadOnlyMemory<byte> key, int expectedSize, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(expectedSize);
        try
        {
            var length = await GetAsync(source, root, key, buffer, cancellationToken).ConfigureAwait(false);
            if (IsTooSmall(length, out var required))
            {
                // Rare: the value lives in overflow pages. Now that its size is known, read it again.
                ArrayPool<byte>.Shared.Return(buffer);
                buffer = null;
                buffer = ArrayPool<byte>.Shared.Rent(required);
                length = await GetAsync(source, root, key, buffer, cancellationToken).ConfigureAwait(false);
            }

            if (length < 0)
            {
                ArrayPool<byte>.Shared.Return(buffer);
                return (null, 0);
            }

            var result = buffer;
            buffer = null;
            return (result, length);
        }
        catch
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            throw;
        }
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

    /// <summary>
    /// The keys whose value differs between two states of a tree, in key order. A page both states
    /// share holds the same entries in both, because committed pages are never overwritten: the
    /// comparison steps over it without reading it, so its cost follows what changed, not the size of
    /// the tree.
    /// </summary>
    /// <remarks>
    /// The caller keeps both states reachable for as long as it enumerates: it holds a read snapshot no
    /// newer than the older one, or is the writer.
    /// </remarks>
    public static async IAsyncEnumerable<BTreeChange> DiffAsync(
        IPageSource source,
        PageNumber before,
        PageNumber after,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (before == after)
        {
            yield break;
        }

        var old = new DiffCursor(source, before, await HeightAsync(source, before, cancellationToken).ConfigureAwait(false));
        var current = new DiffCursor(source, after, await HeightAsync(source, after, cancellationToken).ConfigureAwait(false));
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var left = old.Peek();
                var right = current.Peek();
                if (left == DiffItem.End && right == DiffItem.End)
                {
                    yield break;
                }

                if (left == DiffItem.Subtree || right == DiffItem.Subtree)
                {
                    if (left == right && old.Subtree == current.Subtree)
                    {
                        // Both are about to enter the same page, having passed the same keys: nothing in it differs.
                        old.Skip();
                        current.Skip();
                        continue;
                    }

                    // Go down on the side that is higher up, so that shared pages meet at the same level.
                    var leftLevel = left == DiffItem.Subtree ? old.Level : -1;
                    var rightLevel = right == DiffItem.Subtree ? current.Level : -1;
                    if (leftLevel >= rightLevel)
                    {
                        await old.ExpandAsync(cancellationToken).ConfigureAwait(false);
                    }

                    if (rightLevel >= leftLevel)
                    {
                        await current.ExpandAsync(cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                var order = left == DiffItem.End ? 1 : right == DiffItem.End ? -1 : old.Key.Span.SequenceCompareTo(current.Key.Span);
                if (order < 0)
                {
                    yield return new BTreeChange(old.Key, true, await old.ValueAsync(cancellationToken).ConfigureAwait(false), false, default);
                    old.Skip();
                }
                else if (order > 0)
                {
                    yield return new BTreeChange(current.Key, false, default, true, await current.ValueAsync(cancellationToken).ConfigureAwait(false));
                    current.Skip();
                }
                else
                {
                    // The same bytes in the leaf are the same value: a large one is never changed in place.
                    if (old.IsLarge != current.IsLarge || !old.Stored.Span.SequenceEqual(current.Stored.Span))
                    {
                        var oldValue = await old.ValueAsync(cancellationToken).ConfigureAwait(false);
                        var newValue = await current.ValueAsync(cancellationToken).ConfigureAwait(false);
                        if (!oldValue.Span.SequenceEqual(newValue.Span))
                        {
                            yield return new BTreeChange(current.Key, true, oldValue, true, newValue);
                        }
                    }

                    old.Skip();
                    current.Skip();
                }
            }
        }
        finally
        {
            old.Dispose();
            current.Dispose();
        }
    }

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

    /// <summary>Inserts the entry into the main tree of the transaction, or replaces the value if the key exists.</summary>
    public async ValueTask PutAsync(
        WriteTransaction transaction,
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken)
    {
        try
        {
            await PutAsync(transaction, transaction.Root, key, value, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Also when it fails halfway: the pages copied so far hang from the new root.
            transaction.Root = _root;
        }
    }

    /// <summary>
    /// Inserts the entry into the tree rooted at <paramref name="root"/>, or replaces the value if the key exists.
    /// </summary>
    /// <returns>The root of the tree afterwards, which the caller must keep: copy-on-write moves it.</returns>
    public async ValueTask<PageNumber> PutAsync(
        WriteTransaction transaction,
        PageNumber root,
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken)
    {
        _root = root;
        if (key.IsEmpty || key.Length > _maxKeySize)
        {
            throw new ArgumentException($"The key must be between 1 and {_maxKeySize} bytes.", nameof(key));
        }

        if (value.Length > MaxValueSize)
        {
            throw new ArgumentException($"A value must not exceed {MaxValueSize} bytes.", nameof(value));
        }

        if (_root.IsNone)
        {
            var first = transaction.Allocate(PageType.BTreeLeaf, out var firstNumber);
            BTreeNode.Initialize(first.Span, leaf: true);
            _root = firstNumber;
        }

        // Walk down making every page on the path writable. After this loop the insert needs no I/O.
        var depth = 0;
        var number = _root;
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
                    _root = number;
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

        // If the key exists and its value lives in overflow pages, those pages are no longer reached.
        if (TryGetOverflow(_pathPages[depth].Span, key.Span, out var oldLength, out var oldFirst))
        {
            await FreeChainAsync(transaction, oldFirst, oldLength, cancellationToken).ConfigureAwait(false);
        }

        var large = key.Length + value.Length > _maxEntrySize;
        if (large)
        {
            // Too large for a leaf: the value goes to its own pages and the leaf keeps where they start.
            BTreeNode.WriteOverflowReference(_reference, value.Length, WriteChain(transaction, value.Span));
        }

        var stored = large ? _reference : value;
        if (!CanSpill(depth, key.Span, stored.Length)
            || !await SpillAsync(transaction, depth, key, stored, large, cancellationToken).ConfigureAwait(false))
        {
            InsertIntoLeaf(transaction, depth, key.Span, stored.Span, large, rightmostPath);
        }

        return _root;
    }

    /// <summary>
    /// Frees every page of the tree rooted at <paramref name="root"/>: nodes, leaves and the overflow
    /// pages of large values. Nothing may reach the tree afterwards.
    /// </summary>
    public static async ValueTask DropAsync(WriteTransaction transaction, PageNumber root, CancellationToken cancellationToken)
    {
        if (root.IsNone)
        {
            return;
        }

        // What hangs from the page is noted down while it is pinned, and followed once it no longer is.
        PageNumber[] children = [];
        List<(PageNumber First, int Length)>? chains = null;
        if (!transaction.TryPin(root, out var page))
        {
            page = await transaction.PinAsync(root, cancellationToken).ConfigureAwait(false);
        }

        using (page)
        {
            var node = page.Span;
            var count = BTreeNode.Count(node);
            if (BTreeNode.IsLeaf(node))
            {
                for (var i = 0; i < count; i++)
                {
                    if (BTreeNode.IsOverflow(node, i))
                    {
                        BTreeNode.ReadOverflowReference(BTreeNode.LeafValue(node, i), out var length, out var first);
                        (chains ??= []).Add((first, length));
                    }
                }
            }
            else
            {
                children = new PageNumber[count + 1];
                for (var i = 0; i <= count; i++)
                {
                    children[i] = BTreeNode.Child(node, i);
                }
            }
        }

        foreach (var child in children)
        {
            await DropAsync(transaction, child, cancellationToken).ConfigureAwait(false);
        }

        if (chains is not null)
        {
            foreach (var (first, length) in chains)
            {
                await FreeChainAsync(transaction, first, length, cancellationToken).ConfigureAwait(false);
            }
        }

        transaction.Free(root);
    }

    /// <summary>Removes the entry with this key from the main tree of the transaction.</summary>
    /// <returns><see langword="true"/> if the key existed.</returns>
    public async ValueTask<bool> DeleteAsync(
        WriteTransaction transaction, ReadOnlyMemory<byte> key, CancellationToken cancellationToken)
    {
        try
        {
            return (await DeleteAsync(transaction, transaction.Root, key, cancellationToken).ConfigureAwait(false))
                .Removed;
        }
        finally
        {
            transaction.Root = _root;
        }
    }

    /// <summary>Removes the entry with this key from the tree rooted at <paramref name="root"/>.</summary>
    /// <returns>Whether the key existed, and the root of the tree afterwards.</returns>
    public async ValueTask<(bool Removed, PageNumber Root)> DeleteAsync(
        WriteTransaction transaction, PageNumber root, ReadOnlyMemory<byte> key, CancellationToken cancellationToken)
    {
        _root = root;
        var removed = await DeleteCoreAsync(transaction, key, cancellationToken).ConfigureAwait(false);
        return (removed, _root);
    }

    private async ValueTask<bool> DeleteCoreAsync(
        WriteTransaction transaction, ReadOnlyMemory<byte> key, CancellationToken cancellationToken)
    {
        // First find the key without touching anything: a delete that finds nothing must not copy the path.
        var depth = 0;
        var number = _root;
        while (true)
        {
            if (number.IsNone)
            {
                return false;
            }

            if (depth == MaxDepth)
            {
                throw new InvalidDataException("The B+Tree is deeper than any valid tree can be.");
            }

            if (!transaction.TryPin(number, out var page))
            {
                page = await transaction.PinAsync(number, cancellationToken).ConfigureAwait(false);
            }

            using (page)
            {
                var node = page.Span;
                _pathNumbers[depth] = number;
                if (BTreeNode.IsLeaf(node))
                {
                    BTreeNode.LowerBound(node, key.Span, out var found);
                    if (!found)
                    {
                        return false;
                    }

                    break;
                }

                _pathIndexes[depth] = BTreeNode.FindChildIndex(node, key.Span);
                number = BTreeNode.Child(node, _pathIndexes[depth]);
            }

            depth++;
        }

        // Now copy the path, top down, so that every page on it may be modified.
        for (var level = 0; level <= depth; level++)
        {
            var current = _pathNumbers[level];
            if (!transaction.TryGetWritable(current, out var writable))
            {
                if (!transaction.TryPin(current, out var committed))
                {
                    committed = await transaction.PinAsync(current, cancellationToken).ConfigureAwait(false);
                }

                using (committed)
                {
                    writable = transaction.CopyForWrite(committed, current, out current);
                }

                _pathNumbers[level] = current;
                if (level == 0)
                {
                    _root = current;
                }
                else
                {
                    BTreeNode.SetChild(_pathPages[level - 1].Span, _pathIndexes[level - 1], current);
                }
            }

            _pathPages[level] = writable;
        }

        if (TryGetOverflow(_pathPages[depth].Span, key.Span, out var oldLength, out var oldFirst))
        {
            await FreeChainAsync(transaction, oldFirst, oldLength, cancellationToken).ConfigureAwait(false);
        }

        var leaf = _pathPages[depth].Span;
        BTreeNode.RemoveSlot(leaf, BTreeNode.LowerBound(leaf, key.Span, out _));

        // A node left under a quarter full joins a sibling if both fit in one page. That removes an entry
        // from the parent, which may then need the same, up to the root.
        for (var level = depth; level > 0; level--)
        {
            var node = _pathPages[level].Span;
            if (BTreeNode.UsedSpace(node) >= BTreeNode.Capacity(node) / 4)
            {
                return true;
            }

            var index = _pathIndexes[level - 1];
            var lastChild = BTreeNode.Count(_pathPages[level - 1].Span);
            var merged = index > 0
                && await TryMergeAsync(transaction, level, index - 1, cancellationToken).ConfigureAwait(false);
            if (!merged && index < lastChild)
            {
                merged = await TryMergeAsync(transaction, level, index, cancellationToken).ConfigureAwait(false);
            }

            if (!merged)
            {
                return true;
            }
        }

        CollapseRoot(transaction);
        return true;
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
        byte[]? large = null;
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
                    if (!BTreeNode.IsOverflow(leaf.Span, index))
                    {
                        yield return new BTreeEntry(
                            leaf.Slice(keyOffset, keyLength), leaf.Slice(valueOffset, valueLength));
                        continue;
                    }

                    // The value lives in pages of its own: it is put together in a buffer that, like the
                    // page of any other entry, is good until the scan moves on.
                    BTreeNode.ReadOverflowReference(
                        leaf.Span.Slice(valueOffset, valueLength), out var length, out var first);
                    if (large is null || large.Length < length)
                    {
                        if (large is not null)
                        {
                            ArrayPool<byte>.Shared.Return(large);
                        }

                        large = null;
                        large = ArrayPool<byte>.Shared.Rent(length);
                    }

                    await ReadChainAsync(source, first, large.AsMemory(0, length), cancellationToken)
                        .ConfigureAwait(false);
                    yield return new BTreeEntry(leaf.Slice(keyOffset, keyLength), large.AsMemory(0, length));
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
            if (large is not null)
            {
                ArrayPool<byte>.Shared.Return(large);
            }
        }
    }

    /// <summary>
    /// Copies the value of <paramref name="key"/> if it is in the leaf itself. If it lives in overflow pages,
    /// <paramref name="chain"/> says where and the caller reads them into the first bytes of the destination.
    /// </summary>
    /// <returns>The value length, <see cref="NotFound"/>, or a too-small result.</returns>
    private static int ReadLeaf(ReadOnlySpan<byte> leaf, ReadOnlySpan<byte> key, Span<byte> destination, out PageNumber chain)
    {
        chain = PageNumber.None;
        var index = BTreeNode.LowerBound(leaf, key, out var found);
        if (!found)
        {
            return NotFound;
        }

        var value = BTreeNode.LeafValue(leaf, index);
        if (BTreeNode.IsOverflow(leaf, index))
        {
            BTreeNode.ReadOverflowReference(value, out var length, out var first);
            if (length > destination.Length)
            {
                return NotFound - length;
            }

            chain = first;
            return length;
        }

        if (value.Length > destination.Length)
        {
            return NotFound - value.Length;
        }

        value.CopyTo(destination);
        return value.Length;
    }

    /// <summary>Fills <paramref name="destination"/> with a value from its overflow pages. Returns its length.</summary>
    private static async ValueTask<int> ReadChainAsync(
        IPageSource source, PageNumber first, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var number = first;
        var read = 0;
        while (read < destination.Length)
        {
            if (number.IsNone)
            {
                throw new InvalidDataException("The overflow pages of a value end before the value does.");
            }

            if (!source.TryPin(number, out var page))
            {
                page = await source.PinAsync(number, cancellationToken).ConfigureAwait(false);
            }

            using (page)
            {
                var data = OverflowData(page, number);
                var count = Math.Min(data.Length, destination.Length - read);
                data[..count].CopyTo(destination.Span[read..]);
                read += count;
                number = NextInChain(page);
            }
        }

        return read;
    }

    /// <summary>The bytes of the value in one overflow page, after checking that it is one.</summary>
    private static ReadOnlySpan<byte> OverflowData(in PageLease page, PageNumber number) =>
        page.Type == PageType.Overflow
            ? page.Span[OverflowHeaderSize..]
            : throw new InvalidDataException(
                $"Page {number.Value} should hold part of a large value but is a {page.Type} page.");

    private static PageNumber NextInChain(in PageLease page) =>
        new(BinaryPrimitives.ReadUInt32LittleEndian(page.Span));

    private static bool TryGetOverflow(ReadOnlySpan<byte> leaf, ReadOnlySpan<byte> key, out int length, out PageNumber first)
    {
        var index = BTreeNode.LowerBound(leaf, key, out var found);
        if (found && BTreeNode.IsOverflow(leaf, index))
        {
            BTreeNode.ReadOverflowReference(BTreeNode.LeafValue(leaf, index), out length, out first);
            return true;
        }

        length = 0;
        first = PageNumber.None;
        return false;
    }

    /// <summary>
    /// Puts a value in pages of its own, each starting with the number of the next one, and returns the first.
    /// </summary>
    internal static PageNumber WriteChain(WriteTransaction transaction, ReadOnlySpan<byte> value)
    {
        var first = PageNumber.None;
        Span<byte> previous = default;
        while (!value.IsEmpty)
        {
            var page = transaction.Allocate(PageType.Overflow, out var number).Span;
            if (first.IsNone)
            {
                first = number;
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(previous, number.Value);
            }

            var count = Math.Min(value.Length, page.Length - OverflowHeaderSize);
            value[..count].CopyTo(page[OverflowHeaderSize..]);
            value = value[count..];
            previous = page;
        }

        return first;
    }

    /// <summary>How many levels a tree has: 0 if it is empty, 1 if its root is a leaf.</summary>
    internal static async ValueTask<int> HeightAsync(IPageSource source, PageNumber root, CancellationToken cancellationToken)
    {
        var height = 0;
        for (var number = root; !number.IsNone; height++)
        {
            if (!source.TryPin(number, out var page))
            {
                page = await source.PinAsync(number, cancellationToken).ConfigureAwait(false);
            }

            using (page)
            {
                number = BTreeNode.IsLeaf(page.Span) ? PageNumber.None : BTreeNode.Child(page.Span, 0);
            }
        }

        return height;
    }

    /// <summary>Frees the overflow pages of a value that the tree no longer reaches.</summary>
    private static async ValueTask FreeChainAsync(
        WriteTransaction transaction, PageNumber first, int length, CancellationToken cancellationToken)
    {
        var number = first;
        for (var remaining = length; remaining > 0;)
        {
            if (number.IsNone)
            {
                throw new InvalidDataException("The overflow pages of a value end before the value does.");
            }

            if (!transaction.TryPin(number, out var page))
            {
                page = await transaction.PinAsync(number, cancellationToken).ConfigureAwait(false);
            }

            PageNumber next;
            using (page)
            {
                remaining -= OverflowData(page, number).Length;
                next = NextInChain(page);
            }

            transaction.Free(number);
            number = next;
        }
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
                var length = ReadLeaf(page.Span, key.Span, destination.Span, out var chain);
                return chain.IsNone
                    ? length
                    : await ReadChainAsync(source, chain, destination[..length], cancellationToken).ConfigureAwait(false);
            }

            number = BTreeNode.Child(page.Span, BTreeNode.FindChildIndex(page.Span, key.Span));
        }
    }

    private static bool TryInsertLeaf(
        Span<byte> leaf, int index, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, bool overflow)
    {
        if (BTreeNode.TryInsertLeaf(leaf, index, key, value, overflow))
        {
            return true;
        }

        if (BTreeNode.FreeSpace(leaf) + BTreeNode.Garbage(leaf) < BTreeNode.LeafEntrySize(key.Length, value.Length))
        {
            return false;
        }

        BTreeNode.Compact(leaf);
        return BTreeNode.TryInsertLeaf(leaf, index, key, value, overflow);
    }

    /// <summary>
    /// Records that the child at <paramref name="index"/> was split: <paramref name="separator"/> is the
    /// first key of the new sibling <paramref name="right"/>.
    /// </summary>
    private static bool TryInsertSeparator(Span<byte> node, int index, ReadOnlySpan<byte> separator, PageNumber right)
    {
        var left = BTreeNode.Child(node, index);
        if (!BTreeNode.TryInsertInternal(node, index, left, separator))
        {
            // Deletes leave dead cells behind; reclaim them before deciding the node is full.
            if (BTreeNode.Garbage(node) == 0)
            {
                return false;
            }

            BTreeNode.Compact(node);
            if (!BTreeNode.TryInsertInternal(node, index, left, separator))
            {
                return false;
            }
        }

        // The slot that pointed to the split child moved one place right and now covers the new sibling.
        BTreeNode.SetChild(node, index + 1, right);
        return true;
    }

    /// <summary>
    /// Joins the node at <paramref name="level"/> of the path with a sibling: the children at
    /// <paramref name="leftIndex"/> and the next one of their parent. The result stays in the node's own
    /// page, which is already writable, so the sibling is read but never copied.
    /// </summary>
    private async ValueTask<bool> TryMergeAsync(
        WriteTransaction transaction, int level, int leftIndex, CancellationToken cancellationToken)
    {
        var nodeNumber = _pathNumbers[level];
        var nodeIsLeft = BTreeNode.Child(_pathPages[level - 1].Span, leftIndex) == nodeNumber;
        var siblingNumber = BTreeNode.Child(_pathPages[level - 1].Span, nodeIsLeft ? leftIndex + 1 : leftIndex);
        if (!transaction.TryPin(siblingNumber, out var sibling))
        {
            sibling = await transaction.PinAsync(siblingNumber, cancellationToken).ConfigureAwait(false);
        }

        using (sibling)
        {
            var node = _pathPages[level].Span;
            var parent = _pathPages[level - 1].Span;
            ReadOnlySpan<byte> left = nodeIsLeft ? node : sibling.Span;
            ReadOnlySpan<byte> right = nodeIsLeft ? sibling.Span : node;
            if (!BTreeNode.TryMerge(_mergeScratch, left, right, BTreeNode.Key(parent, leftIndex)))
            {
                return false;
            }

            _mergeScratch.CopyTo(node);

            // The parent loses the key between the two and keeps one pointer, to the node.
            BTreeNode.RemoveSlot(parent, leftIndex);
            BTreeNode.SetChild(parent, leftIndex, nodeNumber);
        }

        transaction.Free(siblingNumber);
        return true;
    }

    /// <summary>After a delete reached the root: an empty leaf root is an empty tree, and an internal root with one child is that child.</summary>
    private void CollapseRoot(WriteTransaction transaction)
    {
        var root = _pathPages[0].Span;
        if (BTreeNode.Count(root) > 0)
        {
            return;
        }

        _root = BTreeNode.IsLeaf(root) ? PageNumber.None : BTreeNode.Child(root, 0);
        transaction.Free(_pathNumbers[0]);
    }

    /// <summary>
    /// Whether a full leaf is the one just before the last leaf of the tree, and the key is new. Keys that
    /// grow with time but arrive slightly out of order (UUID v7 of the same millisecond) land there.
    /// </summary>
    private bool CanSpill(int depth, ReadOnlySpan<byte> key, int storedLength)
    {
        if (depth == 0)
        {
            return false;
        }

        var leaf = _pathPages[depth].Span;
        if (BTreeNode.FreeSpace(leaf) + BTreeNode.Garbage(leaf) >= BTreeNode.LeafEntrySize(key.Length, storedLength)
            || _pathIndexes[depth - 1] != BTreeNode.Count(_pathPages[depth - 1].Span) - 1)
        {
            return false;
        }

        for (var level = 0; level < depth - 1; level++)
        {
            if (_pathIndexes[level] != BTreeNode.Count(_pathPages[level].Span))
            {
                return false;
            }
        }

        _ = BTreeNode.LowerBound(leaf, key, out var found);
        return !found;
    }

    /// <summary>
    /// Makes room in a full leaf by passing its highest entries to the last leaf of the tree, instead of
    /// splitting it in two halves that would never fill up. Returns whether the entry was inserted.
    /// </summary>
    private async ValueTask<bool> SpillAsync(
        WriteTransaction transaction,
        int depth,
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> stored,
        bool overflow,
        CancellationToken cancellationToken)
    {
        var slot = _pathIndexes[depth - 1];
        var number = BTreeNode.Child(_pathPages[depth - 1].Span, slot + 1);
        if (!transaction.TryGetWritable(number, out var sibling))
        {
            if (!transaction.TryPin(number, out var committed))
            {
                committed = await transaction.PinAsync(number, cancellationToken).ConfigureAwait(false);
            }

            using (committed)
            {
                sibling = transaction.CopyForWrite(committed, number, out number);
            }

            BTreeNode.SetChild(_pathPages[depth - 1].Span, slot + 1, number);
        }

        return TrySpill(_pathPages[depth].Span, sibling.Span, _pathPages[depth - 1].Span, slot, key.Span, stored.Span, overflow);
    }

    private static bool TrySpill(
        Span<byte> leaf,
        Span<byte> sibling,
        Span<byte> parent,
        int slot,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> stored,
        bool overflow)
    {
        // Decide everything before moving anything: the fewest entries from the end that make room.
        var index = BTreeNode.LowerBound(leaf, key, out _);
        var needed = BTreeNode.LeafEntrySize(key.Length, stored.Length);
        var room = BTreeNode.FreeSpace(leaf) + BTreeNode.Garbage(leaf);
        var count = BTreeNode.Count(leaf);
        var from = count;
        var moved = 0;
        while (room + moved < needed && from > index)
        {
            from--;
            moved += BTreeNode.LeafEntrySize(BTreeNode.Key(leaf, from).Length, BTreeNode.LeafValue(leaf, from).Length);
        }

        // Nothing above the key is left to move and it still does not fit: the key goes across too.
        var across = room + moved < needed;
        var first = across ? key : BTreeNode.Key(leaf, from);
        if (moved + (across ? needed : 0) > BTreeNode.FreeSpace(sibling) + BTreeNode.Garbage(sibling)
            || first.Length - BTreeNode.Key(parent, slot).Length > BTreeNode.FreeSpace(parent) + BTreeNode.Garbage(parent))
        {
            return false;
        }

        // The key between the two leaves becomes the first one of the sibling.
        var left = BTreeNode.Child(parent, slot);
        BTreeNode.RemoveSlot(parent, slot);
        if (!BTreeNode.TryInsertInternal(parent, slot, left, first))
        {
            BTreeNode.Compact(parent);
            if (!BTreeNode.TryInsertInternal(parent, slot, left, first))
            {
                throw new InvalidDataException("A B+Tree node has no room for a key it had room for.");
            }
        }

        for (var i = count - 1; i >= from; i--)
        {
            Insert(sibling, 0, BTreeNode.Key(leaf, i), BTreeNode.LeafValue(leaf, i), BTreeNode.IsOverflow(leaf, i));
            BTreeNode.RemoveSlot(leaf, i);
        }

        Insert(across ? sibling : leaf, across ? 0 : index, key, stored, overflow);
        return true;

        static void Insert(Span<byte> node, int at, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, bool overflow)
        {
            if (!TryInsertLeaf(node, at, key, value, overflow))
            {
                throw new InvalidDataException("A B+Tree leaf has no room for an entry it had room for.");
            }
        }
    }

    private void InsertIntoLeaf(
        WriteTransaction transaction,
        int depth,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> value,
        bool overflow,
        bool rightmostPath)
    {
        var leaf = _pathPages[depth].Span;
        var index = BTreeNode.LowerBound(leaf, key, out var found);
        if (found)
        {
            BTreeNode.LeafValueRange(leaf, index, out var offset, out var length);
            if (length == value.Length && BTreeNode.IsOverflow(leaf, index) == overflow)
            {
                value.CopyTo(leaf[offset..]);
                return;
            }

            BTreeNode.RemoveSlot(leaf, index);
        }

        if (TryInsertLeaf(leaf, index, key, value, overflow))
        {
            return;
        }

        var right = transaction.Allocate(PageType.BTreeLeaf, out var rightNumber).Span;
        BTreeNode.Initialize(right, leaf: true);
        int separatorLength;
        if (rightmostPath && index == BTreeNode.Count(leaf))
        {
            // Append: split 100/0. The full leaf stays full and the new key starts the next one.
            Insert(right, 0, key, value, overflow);
            key.CopyTo(_separator);
            separatorLength = key.Length;
        }
        else
        {
            // Near the end of the last leaf (UUID v7 of one millisecond arrive in any order): split where
            // the key goes. What is before it will not grow any more, so it stays, nearly full.
            var splitAt = BTreeNode.ChooseSplitIndex(leaf);
            var atKey = rightmostPath && index > splitAt;
            if (atKey)
            {
                splitAt = index;
            }

            BTreeNode.SplitLeaf(leaf, right, splitAt);
            if (index <= splitAt && !atKey)
            {
                Insert(leaf, index, key, value, overflow);
            }
            else
            {
                Insert(right, index - splitAt, key, value, overflow);
            }

            var first = BTreeNode.Key(right, 0);
            first.CopyTo(_separator);
            separatorLength = first.Length;
        }

        InsertIntoParent(transaction, depth - 1, separatorLength, rightNumber, rightmostPath);

        static void Insert(Span<byte> node, int at, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, bool overflow)
        {
            if (!TryInsertLeaf(node, at, key, value, overflow))
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
                _root = rootNumber;
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

    private enum DiffItem
    {
        End,
        Subtree,
        Entry,
    }

    /// <summary>
    /// One state of a tree being compared, as a sequence in key order of entries and of subtrees not yet
    /// opened. A subtree can be stepped over whole, or opened to go on with what it holds.
    /// </summary>
    private sealed class DiffCursor(IPageSource source, PageNumber root, int height) : IDisposable
    {
        private readonly List<(PageLease Page, int Index)> _path = [];
        private PageNumber _root = root;

        /// <summary>Gets the subtree that comes next. Meaningful after <see cref="Peek"/> says there is one.</summary>
        public PageNumber Subtree => _path.Count == 0 ? _root : BTreeNode.Child(_path[^1].Page.Span, _path[^1].Index);

        /// <summary>Gets how many levels the subtree that comes next has.</summary>
        public int Level => height - _path.Count;

        public ReadOnlyMemory<byte> Key
        {
            get
            {
                var (page, index) = _path[^1];
                BTreeNode.LeafKeyRange(page.Span, index, out var offset, out var length);
                return page.Payload.Slice(offset, length);
            }
        }

        /// <summary>Gets what the leaf holds for the entry: its value, or the reference to a large one.</summary>
        public ReadOnlyMemory<byte> Stored
        {
            get
            {
                var (page, index) = _path[^1];
                BTreeNode.LeafValueRange(page.Span, index, out var offset, out var length);
                return page.Payload.Slice(offset, length);
            }
        }

        public bool IsLarge => BTreeNode.IsOverflow(_path[^1].Page.Span, _path[^1].Index);

        public DiffItem Peek()
        {
            while (_path.Count > 0)
            {
                var (page, index) = _path[^1];
                var node = page.Span;
                var isLeaf = BTreeNode.IsLeaf(node);

                // An internal node has one child more than keys.
                if (index < BTreeNode.Count(node) + (isLeaf ? 0 : 1))
                {
                    return isLeaf ? DiffItem.Entry : DiffItem.Subtree;
                }

                page.Dispose();
                _path.RemoveAt(_path.Count - 1);
            }

            return _root.IsNone ? DiffItem.End : DiffItem.Subtree;
        }

        /// <summary>Steps over the entry or the subtree that comes next.</summary>
        public void Skip()
        {
            if (_path.Count == 0)
            {
                _root = PageNumber.None;
            }
            else
            {
                _path[^1] = (_path[^1].Page, _path[^1].Index + 1);
            }
        }

        /// <summary>Opens the subtree that comes next: what follows is what it holds.</summary>
        public async ValueTask ExpandAsync(CancellationToken cancellationToken)
        {
            var number = Subtree;
            Skip();
            if (!source.TryPin(number, out var page))
            {
                page = await source.PinAsync(number, cancellationToken).ConfigureAwait(false);
            }

            _path.Add((page, 0));
        }

        /// <summary>The value of the entry that comes next, read from its own pages if it is a large one.</summary>
        public async ValueTask<ReadOnlyMemory<byte>> ValueAsync(CancellationToken cancellationToken)
        {
            if (!IsLarge)
            {
                return Stored;
            }

            BTreeNode.ReadOverflowReference(Stored.Span, out var length, out var first);
            var value = new byte[length];
            await ReadChainAsync(source, first, value, cancellationToken).ConfigureAwait(false);
            return value;
        }

        public void Dispose()
        {
            foreach (var (page, _) in _path)
            {
                page.Dispose();
            }

            _path.Clear();
        }
    }
}
