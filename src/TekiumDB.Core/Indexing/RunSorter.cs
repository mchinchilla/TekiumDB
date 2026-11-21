// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using TekiumDB.Storage;

namespace TekiumDB.Indexing;

/// <summary>
/// Sorts more entries than fit in memory. They pile up in a buffer; when it is full they are sorted and
/// written out as a run, and at the end the runs are merged. The runs live in scratch pages of the
/// database file itself, written through the pager like any other page: an encrypted database keeps
/// them encrypted, a database in memory needs no disk, and a crash leaves them as unused space.
/// </summary>
/// <remarks>
/// Entries with the same key come out in the order they went in. Writing a run spills the pages of the
/// transaction, so nobody may be holding one of them meanwhile.
/// </remarks>
internal sealed class RunSorter
{
    private const int Header = 2 * sizeof(int);

    private readonly WriteTransaction _transaction;
    private readonly int _budget;
    private readonly List<Run> _runs = [];
    private readonly ArenaOrder _order;
    private byte[] _arena;
    private int[] _offsets = new int[256];
    private int _used;
    private int _count;

    // The merge: the run each entry comes from, as a heap ordered by the entry each one is at.
    private int[] _heap = [];
    private int _heapSize;
    private int _current = -1;
    private int _memoryPosition;
    private bool _sealed;

    /// <summary>Initializes a new instance of the <see cref="RunSorter"/> class.</summary>
    /// <param name="transaction">The transaction whose pages the runs are written to.</param>
    /// <param name="budget">How many bytes of entries are kept in memory before a run is written.</param>
    public RunSorter(WriteTransaction transaction, int budget)
    {
        _transaction = transaction;
        _budget = budget;
        _arena = new byte[Math.Min(budget, 1 << 16)];
        _order = new ArenaOrder(this);
    }

    /// <summary>Gets the number of entries added.</summary>
    public long Count { get; private set; }

    /// <summary>Gets a value indicating whether the buffer is full: time to call <see cref="WriteRunAsync"/>.</summary>
    public bool IsFull => _used >= _budget;

    /// <summary>Gets the key of the entry the merge is at.</summary>
    public ReadOnlySpan<byte> Key => _current == _runs.Count ? MemoryKey(_offsets[_memoryPosition]) : _runs[_current].Key;

    /// <summary>Gets the value of the entry the merge is at.</summary>
    public ReadOnlySpan<byte> Value => _current == _runs.Count ? MemoryValue(_offsets[_memoryPosition]) : _runs[_current].Value;

    public void Add(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        if (_sealed)
        {
            throw new InvalidOperationException("The sorter is already being read.");
        }

        var size = Header + key.Length + value.Length;
        if (_arena.Length - _used < size)
        {
            Array.Resize(ref _arena, checked((int)Math.Max((long)_used + size, Math.Min((long)_arena.Length * 2, Math.Max(_budget, _arena.Length)))));
        }

        if (_count == _offsets.Length)
        {
            Array.Resize(ref _offsets, _count * 2);
        }

        var entry = _arena.AsSpan(_used, size);
        BinaryPrimitives.WriteInt32LittleEndian(entry, key.Length);
        BinaryPrimitives.WriteInt32LittleEndian(entry[sizeof(int)..], value.Length);
        key.CopyTo(entry[Header..]);
        value.CopyTo(entry[(Header + key.Length)..]);
        _offsets[_count++] = _used;
        _used += size;
        Count++;
    }

    /// <summary>Sorts what is in memory and writes it to the file as one run, leaving the buffer empty.</summary>
    public async ValueTask WriteRunAsync(CancellationToken cancellationToken)
    {
        if (_count == 0)
        {
            return;
        }

        Array.Sort(_offsets, 0, _count, _order);
        var run = new Run();
        Memory<byte> page = default;
        var at = 0;
        for (var i = 0; i < _count; i++)
        {
            var entry = _arena.AsSpan(_offsets[i], EntrySize(_offsets[i]));
            run.Length += entry.Length;
            while (!entry.IsEmpty)
            {
                if (at == page.Length)
                {
                    page = _transaction.Allocate(PageType.SortRun, out var number);
                    run.Pages.Add(number);
                    at = 0;
                }

                var count = Math.Min(entry.Length, page.Length - at);
                entry[..count].CopyTo(page.Span[at..]);
                entry = entry[count..];
                at += count;
            }
        }

        _runs.Add(run);
        _count = 0;
        _used = 0;

        // The run is read back from the file: once it is there, it need not be in memory too.
        await _transaction.SpillAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves to the next entry in key order. The first call starts the merge; after the last entry the
    /// scratch pages are given back.
    /// </summary>
    public async ValueTask<bool> NextAsync(CancellationToken cancellationToken)
    {
        if (!_sealed)
        {
            _sealed = true;
            Array.Sort(_offsets, 0, _count, _order);
            _heap = new int[_runs.Count + 1];
            for (var i = 0; i < _runs.Count; i++)
            {
                if (await _runs[i].AdvanceAsync(_transaction, cancellationToken).ConfigureAwait(false))
                {
                    Push(i);
                }
            }

            if (_count > 0)
            {
                Push(_runs.Count);
            }
        }
        else if (_current >= 0)
        {
            // The source the last entry came from moves on, and goes back into the heap if it has more.
            var more = _current == _runs.Count
                ? ++_memoryPosition < _count
                : await _runs[_current].AdvanceAsync(_transaction, cancellationToken).ConfigureAwait(false);
            if (more)
            {
                Push(_current);
            }
        }

        if (_heapSize == 0)
        {
            _current = -1;
            foreach (var run in _runs)
            {
                foreach (var page in run.Pages)
                {
                    _transaction.Free(page);
                }

                run.Pages.Clear();
            }

            return false;
        }

        _current = Pop();
        return true;
    }

    private int EntrySize(int offset) =>
        Header + BinaryPrimitives.ReadInt32LittleEndian(_arena.AsSpan(offset)) + BinaryPrimitives.ReadInt32LittleEndian(_arena.AsSpan(offset + sizeof(int)));

    private ReadOnlySpan<byte> MemoryKey(int offset) =>
        _arena.AsSpan(offset + Header, BinaryPrimitives.ReadInt32LittleEndian(_arena.AsSpan(offset)));

    private ReadOnlySpan<byte> MemoryValue(int offset)
    {
        var key = BinaryPrimitives.ReadInt32LittleEndian(_arena.AsSpan(offset));
        return _arena.AsSpan(offset + Header + key, BinaryPrimitives.ReadInt32LittleEndian(_arena.AsSpan(offset + sizeof(int))));
    }

    private ReadOnlySpan<byte> KeyAt(int source) => source == _runs.Count ? MemoryKey(_offsets[_memoryPosition]) : _runs[source].Key;

    /// <summary>Whether the entry one source is at goes before that of another. On equal keys, the older source first.</summary>
    private bool Before(int left, int right)
    {
        var order = KeyAt(left).SequenceCompareTo(KeyAt(right));
        return order < 0 || (order == 0 && left < right);
    }

    private void Push(int source)
    {
        var at = _heapSize++;
        while (at > 0 && Before(source, _heap[(at - 1) / 2]))
        {
            _heap[at] = _heap[(at - 1) / 2];
            at = (at - 1) / 2;
        }

        _heap[at] = source;
    }

    private int Pop()
    {
        var top = _heap[0];
        var last = _heap[--_heapSize];
        var at = 0;
        while (true)
        {
            var child = (at * 2) + 1;
            if (child >= _heapSize)
            {
                break;
            }

            if (child + 1 < _heapSize && Before(_heap[child + 1], _heap[child]))
            {
                child++;
            }

            if (!Before(_heap[child], last))
            {
                break;
            }

            _heap[at] = _heap[child];
            at = child;
        }

        _heap[at] = last;
        return top;
    }

    /// <summary>Orders entries in the buffer by key, and those with the same key by when they were added.</summary>
    private sealed class ArenaOrder(RunSorter sorter) : IComparer<int>
    {
        public int Compare(int x, int y)
        {
            var order = sorter.MemoryKey(x).SequenceCompareTo(sorter.MemoryKey(y));
            return order != 0 ? order : x.CompareTo(y);
        }
    }

    /// <summary>A sorted run in scratch pages: its entries back to back, running from one page into the next.</summary>
    private sealed class Run
    {
        private byte[] _entry = new byte[256];
        private int _keyLength;
        private int _valueLength;
        private int _page = -1;
        private int _at;
        private long _read;

        public List<PageNumber> Pages { get; } = [];

        public long Length { get; set; }

        public ReadOnlySpan<byte> Key => _entry.AsSpan(Header, _keyLength);

        public ReadOnlySpan<byte> Value => _entry.AsSpan(Header + _keyLength, _valueLength);

        /// <summary>Reads the next entry of the run. False at its end.</summary>
        public async ValueTask<bool> AdvanceAsync(WriteTransaction transaction, CancellationToken cancellationToken)
        {
            if (_read == Length)
            {
                return false;
            }

            await FillAsync(transaction, 0, Header, cancellationToken).ConfigureAwait(false);
            _keyLength = BinaryPrimitives.ReadInt32LittleEndian(_entry);
            _valueLength = BinaryPrimitives.ReadInt32LittleEndian(_entry.AsSpan(sizeof(int)));
            if (_keyLength < 0 || _valueLength < 0 || (long)_keyLength + _valueLength > Length - _read)
            {
                throw new InvalidDataException("A sort run in the scratch pages of the transaction is damaged.");
            }

            var size = Header + _keyLength + _valueLength;
            if (_entry.Length < size)
            {
                Array.Resize(ref _entry, Math.Max(size, _entry.Length * 2));
            }

            await FillAsync(transaction, Header, size - Header, cancellationToken).ConfigureAwait(false);
            return true;
        }

        private async ValueTask FillAsync(WriteTransaction transaction, int offset, int count, CancellationToken cancellationToken)
        {
            while (count > 0)
            {
                var number = Pages[Math.Max(_page, 0)];
                if (_page < 0 || _at == PageLayout.PayloadSize(transaction.PageSize))
                {
                    number = Pages[++_page];
                    _at = 0;
                }

                // Pinned only while copying: the pages of a run were spilled, so this is the copy the pager holds.
                if (!transaction.TryPin(number, out var page))
                {
                    page = await transaction.PinAsync(number, cancellationToken).ConfigureAwait(false);
                }

                using (page)
                {
                    if (page.Type != PageType.SortRun)
                    {
                        throw new InvalidDataException($"Page {number.Value} is not the scratch page of a sort.");
                    }

                    var take = Math.Min(count, page.Span.Length - _at);
                    page.Span.Slice(_at, take).CopyTo(_entry.AsSpan(offset));
                    _at += take;
                    offset += take;
                    count -= take;
                    _read += take;
                }
            }
        }
    }
}
