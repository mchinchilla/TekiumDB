// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers;

namespace Tekium.Embedded.Storage;

/// <summary>
/// Owns the file as an array of fixed-size pages. Committed pages are never overwritten while anything
/// can still reach them: a write transaction puts its pages where nothing lives and the commit publishes
/// them by writing a root slot.
/// </summary>
internal sealed class Pager : IPageSource, IAsyncDisposable
{
    private const long GrowthStepLimit = 64L * 1024 * 1024;
    private const int CommitBufferBytes = 1 << 20;
    private const int MaxSpareBuffers = 1024;

    private readonly IStorageDevice _device;
    private readonly PageCache _cache;
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private readonly byte[] _slotBuffer = new byte[FileHeader.SlotSize];

    // State of the single writer, reused from one transaction to the next.
    private readonly Stack<byte[]> _spareBuffers = new();
    private readonly Dictionary<uint, byte[]> _dirty = [];
    private readonly List<uint> _freed = [];
    private readonly List<SnapshotState> _retained = [];
    private SnapshotState _current;

    private Pager(IStorageDevice device, PageSize pageSize, PagerOptions options, Snapshot current)
    {
        _device = device;
        _cache = new PageCache(options.CachePages);
        _current = new SnapshotState(current);
        PageSize = pageSize;
        FreeList = FreeList.Empty(pageSize);
    }

    public PageSize PageSize { get; }

    /// <summary>Gets the latest committed state. To read from it safely, hold a <see cref="BeginRead"/>.</summary>
    public Snapshot Current => Volatile.Read(ref _current).Snapshot;

    /// <summary>Gets the number of free pages in the file, reusable or not yet.</summary>
    public int FreePageCount => FreeList.Count;

    internal FreeList FreeList { get; private set; }

    public static async ValueTask<Pager> CreateAsync(
        IStorageDevice device, PagerOptions options, CancellationToken cancellationToken)
    {
        if (device.Length != 0)
        {
            throw new IOException("Cannot create a database over a file that already has data.");
        }

        var initial = new Snapshot(PageNumber.None, FileHeader.MetaPages, new TransactionId(0), PageNumber.None);
        var meta = new byte[options.PageSize.Bytes * (int)FileHeader.MetaPages];
        FileHeader.Write(meta, options.PageSize, initial);
        await device.WriteAsync(0, meta, cancellationToken).ConfigureAwait(false);
        await device.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new Pager(device, options.PageSize, options, initial);
    }

    public static async ValueTask<Pager> OpenAsync(
        IStorageDevice device, PagerOptions options, CancellationToken cancellationToken)
    {
        if (device.Length < FileHeader.FixedSize)
        {
            throw new InvalidDataException("The file is not a TekiumDB database.");
        }

        var header = ArrayPool<byte>.Shared.Rent(FileHeader.FixedSize * 2);
        Pager pager;
        try
        {
            var first = header.AsMemory(0, FileHeader.FixedSize);
            var second = header.AsMemory(FileHeader.FixedSize, FileHeader.FixedSize);
            await device.ReadAsync(0, first, cancellationToken).ConfigureAwait(false);
            var pageSize = FileHeader.ReadPageSize(first.Span);
            if (device.Length < pageSize.Bytes * (long)FileHeader.MetaPages)
            {
                throw new InvalidDataException("The database header is truncated.");
            }

            await device.ReadAsync(pageSize.Bytes, second, cancellationToken).ConfigureAwait(false);
            var current = FileHeader.ReadCurrentSnapshot(first.Span, second.Span);
            if (current.PageCount < FileHeader.MetaPages || (long)current.PageCount * pageSize.Bytes > device.Length)
            {
                throw new InvalidDataException("The database header points past the end of the file.");
            }

            pager = new Pager(device, pageSize, options, current);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }

        pager.FreeList = await FreeList.LoadAsync(pager, pager.PageSize, pager.Current.FreeList, cancellationToken)
            .ConfigureAwait(false);
        return pager;
    }

    /// <summary>
    /// Pins the latest committed state. Never blocks and never allocates: while the reader holds it,
    /// the writer will not reuse any page that state reaches.
    /// </summary>
    public ReadSnapshot BeginRead()
    {
        while (true)
        {
            var state = Volatile.Read(ref _current);
            state.AddReader();

            // Registered before re-checking: either the writer already saw this reader, or the state was
            // still current after registering, and then nothing it reaches has been released yet.
            if (ReferenceEquals(state, Volatile.Read(ref _current)))
            {
                return new ReadSnapshot(state);
            }

            state.RemoveReader();
        }
    }

    public bool TryPin(PageNumber number, out PageLease lease) => _cache.TryPin(number, out lease);

    public ValueTask<PageLease> PinAsync(PageNumber number, CancellationToken cancellationToken) =>
        _cache.TryPin(number, out var lease)
            ? new ValueTask<PageLease>(lease)
            : LoadAsync(number, cancellationToken);

    /// <summary>Waits for the single writer slot. Dispose the transaction to release it.</summary>
    public async ValueTask<WriteTransaction> BeginWriteAsync(CancellationToken cancellationToken)
    {
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var state = _current;

        // A page freed by transaction F belongs to every state older than F. It may be reused only when
        // (a) no reader holds a state older than F, and (b) F is not the latest commit, so that the
        // previous state stays intact on disk as the fallback of the alternate root slot.
        var horizon = (long)state.Snapshot.TransactionId.Value - 1;
        for (var i = _retained.Count - 1; i >= 0; i--)
        {
            // A state that is no longer current and has no readers can never get one again.
            if (_retained[i].Readers == 0)
            {
                _retained.RemoveAt(i);
            }
            else
            {
                horizon = Math.Min(horizon, (long)_retained[i].Snapshot.TransactionId.Value);
            }
        }

        return new WriteTransaction(this, state.Snapshot, horizon, _dirty, _freed);
    }

    public async ValueTask DisposeAsync()
    {
        _writerGate.Dispose();
        await _device.DisposeAsync().ConfigureAwait(false);
    }

    internal byte[] RentBuffer() =>
        _spareBuffers.TryPop(out var buffer) ? buffer : GC.AllocateUninitializedArray<byte>(PageSize.Bytes);

    internal void ReturnBuffer(byte[] buffer)
    {
        if (_spareBuffers.Count < MaxSpareBuffers)
        {
            _spareBuffers.Push(buffer);
        }
    }

    internal void EndWrite(WriteTransaction transaction)
    {
        foreach (var page in transaction.Dirty.Values)
        {
            ReturnBuffer(page);
        }

        _dirty.Clear();
        _freed.Clear();
        _writerGate.Release();
    }

    /// <summary>
    /// The commit: new pages, flush, root slot, flush. The second flush is the durability point;
    /// a failure anywhere before it leaves the previous state intact on disk and in memory.
    /// </summary>
    internal async ValueTask CommitAsync(WriteTransaction transaction, CancellationToken cancellationToken)
    {
        var state = _current;
        var current = state.Snapshot;
        var freeList = FreeList.Prepare(transaction);
        var dirty = transaction.Dirty;
        if (dirty.Count == 0 && freeList is null && transaction.Root == current.Root)
        {
            return;
        }

        var next = new Snapshot(
            transaction.Root,
            transaction.PageCount,
            current.TransactionId.Next(),
            freeList is null ? current.FreeList : freeList.Head);

        if (dirty.Count > 0)
        {
            await WritePagesAsync(dirty, next.PageCount, cancellationToken).ConfigureAwait(false);
            await _device.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        FileHeader.EncodeSlot(_slotBuffer, next);
        await _device.WriteAsync(FileHeader.SlotOffset(next.TransactionId, PageSize), _slotBuffer, cancellationToken)
            .ConfigureAwait(false);
        await _device.FlushAsync(cancellationToken).ConfigureAwait(false);

        // Durable. From here on nothing can fail: publish to readers.
        foreach (var (number, page) in dirty)
        {
            if (_cache.TryClaim(out var frame))
            {
                var replaced = frame.Buffer;
                frame.Buffer = page;
                _cache.PublishCommitted(frame, new PageNumber(number));
                if (replaced is not null)
                {
                    ReturnBuffer(replaced);
                }
            }
            else
            {
                // No room: at least make sure no stale copy of a reused page number stays cached.
                _cache.Invalidate(new PageNumber(number));
                ReturnBuffer(page);
            }
        }

        dirty.Clear();
        if (freeList is not null)
        {
            FreeList.Apply(freeList);
        }

        _retained.Add(state);
        Volatile.Write(ref _current, new SnapshotState(next));
    }

    private static long GrowTo(long length, long required)
    {
        length = Math.Max(length, 1);
        while (length < required)
        {
            length += Math.Min(length, GrowthStepLimit);
        }

        return length;
    }

    /// <summary>Seals and writes the pages in file order, one write per run of consecutive pages.</summary>
    private async ValueTask WritePagesAsync(
        Dictionary<uint, byte[]> pages, uint pageCount, CancellationToken cancellationToken)
    {
        var pageBytes = PageSize.Bytes;
        var required = (long)pageCount * pageBytes;
        if (_device.Length < required)
        {
            _device.SetLength(GrowTo(_device.Length, required));
        }

        var count = pages.Count;
        var numbers = ArrayPool<uint>.Shared.Rent(count);
        var buffer = ArrayPool<byte>.Shared.Rent(
            (int)Math.Max(pageBytes, Math.Min(CommitBufferBytes, (long)count * pageBytes)));
        try
        {
            pages.Keys.CopyTo(numbers, 0);
            Array.Sort(numbers, 0, count);
            var perWrite = buffer.Length / pageBytes;
            for (var i = 0; i < count;)
            {
                var first = numbers[i];
                var filled = 0;
                do
                {
                    var page = pages[numbers[i]];
                    PageLayout.Seal(page);
                    page.CopyTo(buffer.AsSpan(filled * pageBytes));
                    filled++;
                    i++;
                }
                while (i < count && filled < perWrite && numbers[i] == numbers[i - 1] + 1);

                await _device.WriteAsync(first * (long)pageBytes, buffer.AsMemory(0, filled * pageBytes), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(numbers);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask<PageLease> LoadAsync(PageNumber number, CancellationToken cancellationToken)
    {
        if (number.Value < FileHeader.MetaPages || number.Value >= Current.PageCount)
        {
            throw new InvalidDataException($"Page {number.Value} is referenced but is not part of the database.");
        }

        var frame = _cache.ClaimForLoad();
        try
        {
            frame.Buffer ??= GC.AllocateUninitializedArray<byte>(PageSize.Bytes);
            await _device.ReadAsync(number.Value * (long)PageSize.Bytes, frame.Buffer, cancellationToken)
                .ConfigureAwait(false);
            PageLayout.Verify(frame.Buffer, number);
        }
        catch
        {
            PageCache.Abandon(frame);
            throw;
        }

        return _cache.PublishLoaded(frame, number);
    }
}
