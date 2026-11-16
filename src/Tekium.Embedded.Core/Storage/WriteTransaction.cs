// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Storage;

/// <summary>
/// The single writer. Pages it creates live only in memory until <see cref="CommitAsync"/>;
/// disposing without committing discards them, which is the whole rollback.
/// </summary>
internal sealed class WriteTransaction : IPageSource, IAsyncDisposable
{
    private readonly Pager _pager;
    private readonly long _horizon;
    private FreeList.Cursor _cursor;
    private bool _finished;
    private bool _disposed;

    internal WriteTransaction(
        Pager pager, Snapshot basis, TransactionId id, long horizon, Dictionary<uint, byte[]> dirty, List<uint> freed)
    {
        _pager = pager;
        _horizon = horizon;
        _cursor = pager.FreeList.Begin();
        Dirty = dirty;
        Freed = freed;
        PageCount = basis.PageCount;
        Root = basis.Root;
        Id = id;
        Basis = basis;
    }

    /// <summary>Gets the committed state the transaction started from.</summary>
    public Snapshot Basis { get; }

    public TransactionId Id { get; }

    public PageSize PageSize => _pager.PageSize;

    /// <summary>Gets or sets the root the commit will publish.</summary>
    public PageNumber Root { get; set; }

    /// <summary>Gets the pages this transaction wrote, by number. They are private to it until the commit.</summary>
    internal Dictionary<uint, byte[]> Dirty { get; }

    /// <summary>Gets the committed pages this transaction no longer reaches.</summary>
    internal List<uint> Freed { get; }

    /// <summary>Gets or sets a value indicating whether pages of the transaction are already in the file, yet to be made durable.</summary>
    internal bool Spilled { get; set; }

    /// <summary>Gets the high-water mark of the file after this transaction.</summary>
    internal uint PageCount { get; private set; }

    internal FreeList.Cursor Cursor => _cursor;

    /// <summary>Creates a zeroed page and returns its payload.</summary>
    public Memory<byte> Allocate(PageType type, out PageNumber number)
    {
        ThrowIfFinished();
        number = AllocateNumber();
        return CreatePage(number, type);
    }

    /// <summary>Returns the payload of a page this transaction created, which may be modified in place.</summary>
    public bool TryGetWritable(PageNumber number, out Memory<byte> payload)
    {
        if (Dirty.TryGetValue(number.Value, out var page))
        {
            payload = PageLayout.Payload(page);
            return true;
        }

        payload = default;
        return false;
    }

    /// <summary>
    /// Copy-on-write: returns the payload of a private copy of a committed page, under a new number,
    /// and frees the original.
    /// </summary>
    public Memory<byte> CopyForWrite(in PageLease committed, PageNumber original, out PageNumber number)
    {
        ThrowIfFinished();
        number = AllocateNumber();
        var page = _pager.RentBuffer();
        committed.CopyPageTo(page);
        PageLayout.Restamp(page, number, Id);
        Dirty.Add(number.Value, page);
        Freed.Add(original.Value);
        return PageLayout.Payload(page);
    }

    /// <summary>Declares that the tree no longer reaches a page.</summary>
    public void Free(PageNumber number)
    {
        ThrowIfFinished();
        if (Dirty.Remove(number.Value, out var page))
        {
            _pager.ReturnBuffer(page);
        }

        Freed.Add(number.Value);
    }

    public bool TryPin(PageNumber number, out PageLease lease)
    {
        if (Dirty.TryGetValue(number.Value, out var page))
        {
            lease = new PageLease(page);
            return true;
        }

        return _pager.TryPin(number, out lease);
    }

    public ValueTask<PageLease> PinAsync(PageNumber number, CancellationToken cancellationToken) =>
        Dirty.TryGetValue(number.Value, out var page)
            ? new ValueTask<PageLease>(new PageLease(page))
            : _pager.PinAsync(number, cancellationToken);

    /// <summary>
    /// Writes to the file every page the transaction has created so far, and lets go of them: from now on
    /// they are read back from the file, and changing one copies it like a committed page. A transaction
    /// that builds more than fits in memory does this as it goes.
    /// </summary>
    /// <remarks>
    /// Nothing reaches those pages until the commit, so writing them early changes no state anybody can
    /// see, and a rollback or a crash leaves them as unused space. Nobody may be holding a page of the
    /// transaction while this runs: not a scan of it, nor a payload yet to be filled.
    /// </remarks>
    public ValueTask SpillAsync(CancellationToken cancellationToken)
    {
        ThrowIfFinished();
        return Dirty.Count == 0 ? default : _pager.SpillAsync(this, cancellationToken);
    }

    public async ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        ThrowIfFinished();

        // Whatever happens, the transaction is over: a failed commit cannot be retried.
        _finished = true;
        await _pager.CommitAsync(this, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _finished = true;
            _pager.EndWrite(this);
        }

        return default;
    }

    internal PageNumber AllocateNumber() =>
        _pager.FreeList.TryTake(_horizon, ref _cursor, out var reused)
            ? reused
            : new PageNumber(checked(PageCount++));

    internal Memory<byte> CreatePage(PageNumber number, PageType type)
    {
        var page = _pager.RentBuffer();
        page.AsSpan().Clear();
        PageLayout.Stamp(page, type, number, Id);
        Dirty.Add(number.Value, page);
        return PageLayout.Payload(page);
    }

    private void ThrowIfFinished()
    {
        if (_finished)
        {
            throw new InvalidOperationException("The transaction has already been committed or rolled back.");
        }
    }
}
