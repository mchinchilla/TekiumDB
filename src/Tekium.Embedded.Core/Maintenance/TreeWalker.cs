// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Maintenance;

/// <summary>
/// Walks trees of a file that may be damaged. A page that cannot be read, or that does not hold what it
/// should, is noted and skipped with everything that hangs from it; the walk goes on. Nothing is trusted:
/// every page number is checked before it is followed and every page is checked when it is read.
/// </summary>
internal sealed class TreeWalker(IPageSource pager, long pageCount, List<TkFinding> findings, CancellationToken cancellationToken)
{
    private const int MaxDepth = 16;
    private const int OverflowHeaderSize = 4;

    private string _where = string.Empty;
    private int _leafDepth;
    private long _entries;
    private long _pages;
    private long _damaged;
    private Func<byte[], byte[], ValueTask>? _onEntry;

    /// <summary>Gets every page reached so far, by any walk.</summary>
    public HashSet<uint> Visited { get; } = [];

    /// <summary>
    /// Walks the tree rooted at <paramref name="root"/>, giving every entry it can read, in key order, to
    /// <paramref name="onEntry"/>. The values of large entries are read whole.
    /// </summary>
    public async ValueTask<TkTreeSummary> WalkAsync(PageNumber root, string name, Func<byte[], byte[], ValueTask>? onEntry)
    {
        _where = name;
        _leafDepth = -1;
        _entries = 0;
        _pages = 0;
        _damaged = 0;
        _onEntry = onEntry;
        if (!root.IsNone)
        {
            await VisitAsync(root, null, null, 0).ConfigureAwait(false);
        }

        return new TkTreeSummary(name, _entries, _pages, _damaged);
    }

    private static bool IsDamage(Exception exception) =>
        exception is InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException;

    private async ValueTask VisitAsync(PageNumber number, byte[]? lower, byte[]? upper, int depth)
    {
        if (!Claim(number))
        {
            return;
        }

        if (depth >= MaxDepth)
        {
            Damaged(TkFindingKind.TreeDamaged, number, "is deeper than any valid tree can be");
            return;
        }

        // What the page holds is copied out while it is pinned and followed once it no longer is.
        bool leaf;
        byte[][] keys;
        byte[][] values = [];
        bool[] large = [];
        PageNumber[] children = [];
        try
        {
            using var page = await pager.PinAsync(number, cancellationToken).ConfigureAwait(false);
            var node = page.Span;
            leaf = BTreeNode.IsLeaf(node);
            if (page.Type != (leaf ? PageType.BTreeLeaf : PageType.BTreeInternal) || (page.Type is not (PageType.BTreeLeaf or PageType.BTreeInternal)))
            {
                Damaged(TkFindingKind.PageDamaged, number, $"should be a tree page but is marked as {page.Type}");
                return;
            }

            var count = BTreeNode.Count(node);
            keys = new byte[count][];
            for (var i = 0; i < count; i++)
            {
                keys[i] = BTreeNode.Key(node, i).ToArray();
            }

            if (leaf)
            {
                values = new byte[count][];
                large = new bool[count];
                for (var i = 0; i < count; i++)
                {
                    values[i] = BTreeNode.LeafValue(node, i).ToArray();
                    large[i] = BTreeNode.IsOverflow(node, i);
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
        catch (Exception exception) when (IsDamage(exception))
        {
            Damaged(TkFindingKind.PageDamaged, number, $"cannot be read: {exception.Message.TrimEnd('.')}");
            return;
        }

        _pages++;
        for (var i = 0; i < keys.Length; i++)
        {
            if ((i > 0 && keys[i - 1].AsSpan().SequenceCompareTo(keys[i]) >= 0)
                || (lower is not null && keys[i].AsSpan().SequenceCompareTo(lower) < 0)
                || (upper is not null && keys[i].AsSpan().SequenceCompareTo(upper) >= 0))
            {
                Damaged(TkFindingKind.TreeDamaged, number, "holds keys out of order or outside the range its parent gives it");
                return;
            }
        }

        if (!leaf)
        {
            for (var i = 0; i < children.Length; i++)
            {
                await VisitAsync(children[i], i == 0 ? lower : keys[i - 1], i == keys.Length ? upper : keys[i], depth + 1)
                    .ConfigureAwait(false);
            }

            return;
        }

        if (_leafDepth < 0)
        {
            _leafDepth = depth;
        }
        else if (_leafDepth != depth)
        {
            Damaged(TkFindingKind.TreeDamaged, number, "is a leaf at a different depth than the others");
            return;
        }

        for (var i = 0; i < keys.Length; i++)
        {
            var value = values[i];
            if (large[i])
            {
                var whole = await ReadChainAsync(value).ConfigureAwait(false);
                if (whole is null)
                {
                    continue;
                }

                value = whole;
            }

            _entries++;
            if (_onEntry is not null)
            {
                await _onEntry(keys[i], value).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Reads a large value from its pages. Null if any of them is damaged: the entry is lost.</summary>
    private async ValueTask<byte[]?> ReadChainAsync(byte[] reference)
    {
        BTreeNode.ReadOverflowReference(reference, out var length, out var next);
        if (length < 0 || length > BTree.MaxValueSize)
        {
            findings.Add(new TkFinding(TkSeverity.Error, TkFindingKind.TreeDamaged, null, _where, $"A large value of {_where} claims an impossible size."));
            _damaged++;
            return null;
        }

        var value = new byte[length];
        var read = 0;
        while (read < length)
        {
            if (!Claim(next))
            {
                return null;
            }

            try
            {
                using var page = await pager.PinAsync(next, cancellationToken).ConfigureAwait(false);
                if (page.Type != PageType.Overflow)
                {
                    Damaged(TkFindingKind.PageDamaged, next, $"should hold part of a large value but is marked as {page.Type}");
                    return null;
                }

                var data = page.Span[OverflowHeaderSize..];
                var count = Math.Min(data.Length, length - read);
                data[..count].CopyTo(value.AsSpan(read));
                read += count;
                _pages++;
                next = new PageNumber(BinaryPrimitives.ReadUInt32LittleEndian(page.Span));
            }
            catch (Exception exception) when (IsDamage(exception))
            {
                Damaged(TkFindingKind.PageDamaged, next, $"cannot be read: {exception.Message.TrimEnd('.')}");
                return null;
            }
        }

        return value;
    }

    /// <summary>Whether a page number can be followed: inside the file and not reached before.</summary>
    private bool Claim(PageNumber number)
    {
        if (number.Value < FileHeader.MetaPages || number.Value >= pageCount)
        {
            findings.Add(new TkFinding(
                TkSeverity.Error, TkFindingKind.TreeDamaged, number.Value, _where, $"Page {number.Value}, referenced by {_where}, is not part of the database."));
            _damaged++;
            return false;
        }

        if (!Visited.Add(number.Value))
        {
            findings.Add(new TkFinding(
                TkSeverity.Error, TkFindingKind.TreeDamaged, number.Value, _where, $"Page {number.Value} is reached a second time, from {_where}."));
            _damaged++;
            return false;
        }

        return true;
    }

    private void Damaged(TkFindingKind kind, PageNumber number, string what)
    {
        findings.Add(new TkFinding(TkSeverity.Error, kind, number.Value, _where, $"Page {number.Value} of {_where} {what}."));
        _damaged++;
    }
}
