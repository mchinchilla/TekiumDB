// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Tekium.Embedded.Storage;

/// <summary>
/// Owns the file as an array of fixed-size pages. Committed pages are never overwritten:
/// a write transaction appends new pages and the commit publishes them by writing a root slot.
/// </summary>
/// <remarks>
/// Phase 0.5 spike. Deliberately missing: free-list (the file only grows), cache eviction
/// (every page read stays in memory) and per-page checksums. Phase 1 replaces all three.
/// </remarks>
internal sealed class Pager : IPageSource, IAsyncDisposable
{
    private const long GrowthStepLimit = 64L * 1024 * 1024;
    private const int CommitBufferBytes = 1 << 20;

    private readonly IStorageDevice _device;
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private readonly byte[] _slotBuffer = new byte[FileHeader.SlotSize];
    private byte[]?[] _cache;
    private Snapshot _current;

    private Pager(IStorageDevice device, PageSize pageSize, Snapshot current)
    {
        _device = device;
        PageSize = pageSize;
        _current = current;
        _cache = new byte[]?[Math.Max(64, checked((int)current.PageCount))];
    }

    public PageSize PageSize { get; }

    /// <summary>Gets the latest committed state. Readers pin it by holding the reference.</summary>
    public Snapshot Current => Volatile.Read(ref _current);

    public static async ValueTask<Pager> CreateAsync(
        IStorageDevice device, PageSize pageSize, CancellationToken cancellationToken)
    {
        if (device.Length != 0)
        {
            throw new IOException("Cannot create a database over a file that already has data.");
        }

        var initial = new Snapshot(PageNumber.None, PageCount: 1, new TransactionId(0));
        var header = new byte[pageSize.Bytes];
        FileHeader.Write(header, pageSize, initial);
        await device.WriteAsync(0, header, cancellationToken).ConfigureAwait(false);
        await device.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new Pager(device, pageSize, initial);
    }

    public static async ValueTask<Pager> OpenAsync(IStorageDevice device, CancellationToken cancellationToken)
    {
        if (device.Length < FileHeader.FixedSize)
        {
            throw new InvalidDataException("The file is not a TekiumDB database.");
        }

        var header = ArrayPool<byte>.Shared.Rent(FileHeader.FixedSize);
        try
        {
            await device.ReadAsync(0, header.AsMemory(0, FileHeader.FixedSize), cancellationToken)
                .ConfigureAwait(false);
            var pageSize = FileHeader.ReadPageSize(header);
            var current = FileHeader.ReadCurrentSnapshot(header);
            if (current.PageCount == 0 || (long)current.PageCount * pageSize.Bytes > device.Length)
            {
                throw new InvalidDataException("The database header points past the end of the file.");
            }

            return new Pager(device, pageSize, current);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }
    }

    public bool TryGetCachedPage(PageNumber number, [NotNullWhen(true)] out byte[]? page)
    {
        var cache = Volatile.Read(ref _cache);
        if (number.Value < (uint)cache.Length)
        {
            page = Volatile.Read(ref cache[number.Value]);
            return page is not null;
        }

        page = null;
        return false;
    }

    public ValueTask<byte[]> GetPageAsync(PageNumber number, CancellationToken cancellationToken) =>
        TryGetCachedPage(number, out var page) ? new ValueTask<byte[]>(page) : LoadAsync(number, cancellationToken);

    /// <summary>Waits for the single writer slot. Dispose the transaction to release it.</summary>
    public async ValueTask<WriteTransaction> BeginWriteAsync(CancellationToken cancellationToken)
    {
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new WriteTransaction(this, Current);
    }

    public async ValueTask DisposeAsync()
    {
        _writerGate.Dispose();
        await _device.DisposeAsync().ConfigureAwait(false);
    }

    internal void ReleaseWriter() => _writerGate.Release();

    /// <summary>
    /// The commit: new pages, flush, root slot, flush. The second flush is the durability point;
    /// a failure anywhere before it leaves the previous state intact on disk and in memory.
    /// </summary>
    internal async ValueTask CommitAsync(List<byte[]> newPages, PageNumber root, CancellationToken cancellationToken)
    {
        var current = Current;
        if (newPages.Count == 0 && root == current.Root)
        {
            return;
        }

        var pageBytes = PageSize.Bytes;
        var next = new Snapshot(
            root, checked(current.PageCount + (uint)newPages.Count), current.TransactionId.Next());

        if (newPages.Count > 0)
        {
            var required = (long)next.PageCount * pageBytes;
            if (_device.Length < required)
            {
                _device.SetLength(GrowTo(_device.Length, required));
            }

            // New pages are contiguous at the end of the used area: copy them into one buffer per write.
            var buffer = ArrayPool<byte>.Shared.Rent(
                (int)Math.Max(pageBytes, Math.Min(CommitBufferBytes, (long)newPages.Count * pageBytes)));
            try
            {
                var perWrite = buffer.Length / pageBytes;
                for (var written = 0; written < newPages.Count;)
                {
                    var batch = Math.Min(perWrite, newPages.Count - written);
                    for (var i = 0; i < batch; i++)
                    {
                        newPages[written + i].CopyTo(buffer.AsSpan(i * pageBytes));
                    }

                    await _device.WriteAsync(
                        (current.PageCount + written) * (long)pageBytes,
                        buffer.AsMemory(0, batch * pageBytes),
                        cancellationToken).ConfigureAwait(false);
                    written += batch;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await _device.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        FileHeader.EncodeSlot(_slotBuffer, next);
        await _device.WriteAsync(FileHeader.SlotOffset(next.TransactionId), _slotBuffer, cancellationToken)
            .ConfigureAwait(false);
        await _device.FlushAsync(cancellationToken).ConfigureAwait(false);

        Publish(newPages, current.PageCount, next);
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

    private void Publish(List<byte[]> newPages, uint firstNumber, Snapshot next)
    {
        var cache = _cache;
        if (cache.Length < next.PageCount)
        {
            var grown = new byte[]?[Math.Max(checked((int)next.PageCount), cache.Length * 2)];
            cache.CopyTo(grown, 0);
            cache = grown;
        }

        for (var i = 0; i < newPages.Count; i++)
        {
            cache[firstNumber + i] = newPages[i];
        }

        Volatile.Write(ref _cache, cache);
        Volatile.Write(ref _current, next);
    }

    private async ValueTask<byte[]> LoadAsync(PageNumber number, CancellationToken cancellationToken)
    {
        if (number.IsNone || number.Value >= Current.PageCount)
        {
            throw new InvalidDataException($"Page {number.Value} is referenced but is not part of the database.");
        }

        var page = GC.AllocateUninitializedArray<byte>(PageSize.Bytes);
        await _device.ReadAsync(number.Value * (long)PageSize.Bytes, page, cancellationToken).ConfigureAwait(false);
        var cache = Volatile.Read(ref _cache);
        return Interlocked.CompareExchange(ref cache[number.Value], page, null) ?? page;
    }
}
