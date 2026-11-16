// Copyright (c) Tekium. Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace Tekium.Embedded.Storage;

/// <summary>
/// The single writer. Pages it creates live only in memory until <see cref="CommitAsync"/>;
/// disposing without committing discards them, which is the whole rollback.
/// </summary>
internal sealed class WriteTransaction : IPageSource, IAsyncDisposable
{
    private readonly Pager _pager;
    private readonly uint _firstNewPage;
    private readonly List<byte[]> _newPages = [];
    private bool _finished;
    private bool _disposed;

    internal WriteTransaction(Pager pager, Snapshot basis)
    {
        _pager = pager;
        _firstNewPage = basis.PageCount;
        Root = basis.Root;
        Id = basis.TransactionId.Next();
    }

    public TransactionId Id { get; }

    public PageSize PageSize => _pager.PageSize;

    /// <summary>Gets or sets the root the commit will publish.</summary>
    public PageNumber Root { get; set; }

    /// <summary>Indicates whether the page was created by this transaction and may be modified in place.</summary>
    public bool IsWritable(PageNumber number) => number.Value >= _firstNewPage;

    /// <summary>Creates a zeroed page.</summary>
    public byte[] Allocate(out PageNumber number)
    {
        ThrowIfFinished();
        var page = new byte[_pager.PageSize.Bytes];
        number = new PageNumber(checked(_firstNewPage + (uint)_newPages.Count));
        _newPages.Add(page);
        return page;
    }

    /// <summary>Copy-on-write: returns a private copy of a committed page under a new number.</summary>
    public byte[] CopyForWrite(byte[] committed, out PageNumber number)
    {
        ThrowIfFinished();
        var page = GC.AllocateUninitializedArray<byte>(committed.Length);
        committed.CopyTo(page.AsSpan());
        number = new PageNumber(checked(_firstNewPage + (uint)_newPages.Count));
        _newPages.Add(page);
        return page;
    }

    public bool TryGetCachedPage(PageNumber number, [NotNullWhen(true)] out byte[]? page)
    {
        if (IsWritable(number))
        {
            page = _newPages[(int)(number.Value - _firstNewPage)];
            return true;
        }

        return _pager.TryGetCachedPage(number, out page);
    }

    public ValueTask<byte[]> GetPageAsync(PageNumber number, CancellationToken cancellationToken) =>
        IsWritable(number)
            ? new ValueTask<byte[]>(_newPages[(int)(number.Value - _firstNewPage)])
            : _pager.GetPageAsync(number, cancellationToken);

    public async ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        ThrowIfFinished();
        await _pager.CommitAsync(_newPages, Root, cancellationToken).ConfigureAwait(false);
        _finished = true;
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _finished = true;
            _pager.ReleaseWriter();
        }

        return default;
    }

    private void ThrowIfFinished()
    {
        if (_finished)
        {
            throw new InvalidOperationException("The transaction has already been committed or rolled back.");
        }
    }
}
