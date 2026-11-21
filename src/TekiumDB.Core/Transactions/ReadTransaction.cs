// Copyright (c) Tekium. Licensed under the MIT License.

using TekiumDB.Storage;

namespace TekiumDB.Transactions;

/// <summary>
/// A consistent view of the database as of one commit, for as many reads as the caller wants.
/// It never blocks the writer and the writer never blocks it.
/// </summary>
/// <remarks>
/// While it lives, the pages of its state are not reused, so a forgotten one makes the file grow:
/// dispose it. Unlike the bare <see cref="ReadSnapshot"/> it wraps, disposing it twice is harmless and
/// reading through it afterwards is refused, because by then the writer may have reused its pages.
/// </remarks>
internal sealed class ReadTransaction : IPageSource, IAsyncDisposable
{
    private readonly Pager _pager;
    private readonly ReadSnapshot _snapshot;
    private int _disposed;

    private ReadTransaction(Pager pager, ReadSnapshot snapshot)
    {
        _pager = pager;
        _snapshot = snapshot;
    }

    /// <summary>Gets the transaction whose commit produced the state this one reads.</summary>
    public TransactionId Id => Live.Snapshot.TransactionId;

    /// <summary>Gets the root of the tree as of that commit.</summary>
    public PageNumber Root => Live.Root;

    private ReadSnapshot Live
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _snapshot;
        }
    }

    /// <summary>Starts reading the latest committed state. Never waits.</summary>
    public static ReadTransaction Begin(Pager pager) => new(pager, pager.BeginRead());

    public bool TryPin(PageNumber number, out PageLease lease)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _pager.TryPin(number, out lease);
    }

    public ValueTask<PageLease> PinAsync(PageNumber number, CancellationToken cancellationToken) =>
        Volatile.Read(ref _disposed) != 0
            ? ValueTask.FromException<PageLease>(new ObjectDisposedException(nameof(ReadTransaction)))
            : _pager.PinAsync(number, cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _snapshot.Dispose();
        }

        return default;
    }
}
