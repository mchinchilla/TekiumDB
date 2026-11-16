// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Indexing;

/// <summary>
/// Builds a tree from entries that arrive in key order, bottom-up: each leaf is filled to the brim and
/// written once, then the level above it, up to the root. Nothing is searched, split or copied, which is
/// what makes loading a table this way much faster than inserting its rows one by one.
/// </summary>
/// <remarks>
/// A page is handed to the transaction only when it is complete, so the transaction may spill its pages
/// at any point between two entries: see <see cref="ShouldSpill"/>.
/// </remarks>
internal sealed class BTreeBuilder
{
    private readonly WriteTransaction _transaction;
    private readonly int _maxKeySize;
    private readonly int _maxEntrySize;
    private readonly int _spillAt;
    private readonly byte[] _leaf;
    private readonly byte[] _reference = new byte[BTreeNode.OverflowReferenceSize];
    private readonly List<Level> _levels = [];
    private byte[] _leafFirstKey;
    private byte[] _lastKey;
    private int _lastKeyLength;
    private int _leafFirstKeyLength;

    /// <summary>Initializes a new instance of the <see cref="BTreeBuilder"/> class.</summary>
    /// <param name="tree">The tree of the database, for its limits.</param>
    /// <param name="transaction">The transaction the pages belong to.</param>
    /// <param name="spillAt">How many finished pages the transaction may hold before they should be spilled.</param>
    public BTreeBuilder(BTree tree, WriteTransaction transaction, int spillAt)
    {
        _transaction = transaction;
        _maxKeySize = tree.MaxKeySize;
        _maxEntrySize = tree.MaxEntrySize;
        _spillAt = spillAt;
        _leaf = new byte[PageLayout.PayloadSize(transaction.PageSize)];
        _leafFirstKey = new byte[_maxKeySize];
        _lastKey = new byte[_maxKeySize];
        BTreeNode.Initialize(_leaf, leaf: true);
    }

    /// <summary>Gets the number of entries added.</summary>
    public long Count { get; private set; }

    /// <summary>Gets a value indicating whether the transaction holds enough finished pages to be worth spilling.</summary>
    public bool ShouldSpill => _transaction.Dirty.Count >= _spillAt;

    /// <summary>Gets the last key added.</summary>
    public ReadOnlySpan<byte> LastKey => _lastKey.AsSpan(0, _lastKeyLength);

    /// <summary>Adds the next entry. Its key must be above every key added before.</summary>
    public void Add(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        if (key.IsEmpty || key.Length > _maxKeySize)
        {
            throw new ArgumentException($"The key must be between 1 and {_maxKeySize} bytes.", nameof(key));
        }

        if (value.Length > BTree.MaxValueSize)
        {
            throw new ArgumentException($"A value must not exceed {BTree.MaxValueSize} bytes.", nameof(value));
        }

        if (Count > 0 && key.SequenceCompareTo(LastKey) <= 0)
        {
            throw new ArgumentException("The entries of a tree built bottom-up must arrive in strictly ascending key order.", nameof(key));
        }

        var overflow = key.Length + value.Length > _maxEntrySize;
        if (overflow)
        {
            // A large value goes to pages of its own, complete from the start; the leaf keeps where they are.
            BTreeNode.WriteOverflowReference(_reference, value.Length, BTree.WriteChain(_transaction, value));
            value = _reference;
        }

        if (!BTreeNode.TryInsertLeaf(_leaf, BTreeNode.Count(_leaf), key, value, overflow))
        {
            FlushLeaf();
            if (!BTreeNode.TryInsertLeaf(_leaf, 0, key, value, overflow))
            {
                throw new InvalidDataException("An entry does not fit in an empty B+Tree leaf.");
            }
        }

        if (BTreeNode.Count(_leaf) == 1)
        {
            key.CopyTo(_leafFirstKey);
            _leafFirstKeyLength = key.Length;
        }

        key.CopyTo(_lastKey);
        _lastKeyLength = key.Length;
        Count++;
    }

    /// <summary>Writes the pages the transaction holds to the file. Safe between any two entries.</summary>
    public ValueTask SpillAsync(CancellationToken cancellationToken) => _transaction.SpillAsync(cancellationToken);

    /// <summary>Closes every level and returns the root of the tree: <see cref="PageNumber.None"/> if nothing was added.</summary>
    public PageNumber Complete()
    {
        if (Count == 0)
        {
            return PageNumber.None;
        }

        FlushLeaf();
        for (var i = 0; i < _levels.Count; i++)
        {
            var level = _levels[i];
            if (BTreeNode.Count(level.Node) == 0)
            {
                if (!level.HasHeld)
                {
                    // One child and nothing above it: that child is the root.
                    return level.Pending;
                }

                // The last node got a single child, and a node needs two. It takes the last child of the
                // node before it, which was held back for this: that one has plenty.
                var held = level.Held;
                var last = BTreeNode.Count(held) - 1;
                var moved = BTreeNode.Child(held, last);
                var movedKey = BTreeNode.Key(held, last).ToArray();
                BTreeNode.RemoveSlot(held, last);
                BTreeNode.Compact(held);
                if (!BTreeNode.TryInsertInternal(level.Node, 0, level.HeldRight, level.FirstKey.AsSpan(0, level.FirstKeyLength)))
                {
                    throw new InvalidDataException("A separator does not fit in an empty B+Tree node.");
                }

                level.HeldRight = moved;
                movedKey.CopyTo(level.FirstKey, 0);
                level.FirstKeyLength = movedKey.Length;
            }

            FlushHeld(i);
            BTreeNode.SetChild(level.Node, BTreeNode.Count(level.Node), level.Pending);
            Emit(i + 1, Write(level.Node, PageType.BTreeInternal), level.FirstKey.AsSpan(0, level.FirstKeyLength));
        }

        throw new InvalidDataException("A B+Tree built bottom-up ended without a root.");
    }

    private void FlushLeaf()
    {
        var number = Write(_leaf, PageType.BTreeLeaf);
        Emit(0, number, _leafFirstKey.AsSpan(0, _leafFirstKeyLength));
        BTreeNode.Initialize(_leaf, leaf: true);
    }

    private PageNumber Write(ReadOnlySpan<byte> node, PageType type)
    {
        var page = _transaction.Allocate(type, out var number);
        node.CopyTo(page.Span);
        return number;
    }

    /// <summary>Hangs a finished page from the level above it. <paramref name="firstKey"/> is the lowest key under that page.</summary>
    private void Emit(int index, PageNumber child, ReadOnlySpan<byte> firstKey)
    {
        if (index == _levels.Count)
        {
            _levels.Add(new Level(_leaf.Length, _maxKeySize));
        }

        var level = _levels[index];
        if (level.Pending.IsNone)
        {
            level.Pending = child;
            level.SetFirstKey(firstKey);
            return;
        }

        // The child before this one gets its cell now that the key that follows it is known.
        if (!BTreeNode.TryInsertInternal(level.Node, BTreeNode.Count(level.Node), level.Pending, firstKey))
        {
            // Full. It closes with the pending child as its rightmost, and waits until the node that
            // follows has a key of its own, in case that one has to borrow from it.
            FlushHeld(index);
            level.Hold();
            level.SetFirstKey(firstKey);
        }
        else if (BTreeNode.Count(level.Node) == 1)
        {
            FlushHeld(index);
        }

        level.Pending = child;
    }

    private void FlushHeld(int index)
    {
        var level = _levels[index];
        if (!level.HasHeld)
        {
            return;
        }

        level.HasHeld = false;
        BTreeNode.SetChild(level.Held, BTreeNode.Count(level.Held), level.HeldRight);
        var number = Write(level.Held, PageType.BTreeInternal);
        Emit(index + 1, number, level.HeldFirstKey.AsSpan(0, level.HeldFirstKeyLength));
    }

    /// <summary>One level of internal nodes: the node being filled and, sometimes, the full one before it.</summary>
    private sealed class Level
    {
        public Level(int payload, int maxKey)
        {
            Node = new byte[payload];
            Held = new byte[payload];
            FirstKey = new byte[maxKey];
            HeldFirstKey = new byte[maxKey];
            BTreeNode.Initialize(Node, leaf: false);
        }

        public byte[] Node { get; private set; }

        /// <summary>Gets or sets the last child received, which has no cell yet: its cell needs the key that follows it.</summary>
        public PageNumber Pending { get; set; }

        /// <summary>Gets the lowest key under the node being filled.</summary>
        public byte[] FirstKey { get; private set; }

        public int FirstKeyLength { get; set; }

        public bool HasHeld { get; set; }

        public byte[] Held { get; private set; }

        public PageNumber HeldRight { get; set; }

        public byte[] HeldFirstKey { get; private set; }

        public int HeldFirstKeyLength { get; private set; }

        public void SetFirstKey(ReadOnlySpan<byte> key)
        {
            key.CopyTo(FirstKey);
            FirstKeyLength = key.Length;
        }

        /// <summary>Sets the full node aside, with its pending child as its rightmost, and starts an empty one.</summary>
        public void Hold()
        {
            (Node, Held) = (Held, Node);
            (FirstKey, HeldFirstKey) = (HeldFirstKey, FirstKey);
            HeldFirstKeyLength = FirstKeyLength;
            HeldRight = Pending;
            HasHeld = true;
            BTreeNode.Initialize(Node, leaf: false);
        }
    }
}
