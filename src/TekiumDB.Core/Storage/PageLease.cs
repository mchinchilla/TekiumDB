// Copyright (c) Tekium. Licensed under the MIT License.

namespace TekiumDB.Storage;

/// <summary>
/// A page held in memory for reading. While the lease lives the bytes do not move or change;
/// disposing it lets the cache evict the page. Dispose exactly once.
/// </summary>
internal readonly struct PageLease : IDisposable
{
    private readonly PageCache? _cache;
    private readonly PageFrame? _frame;
    private readonly byte[] _page;

    internal PageLease(PageCache cache, PageFrame frame)
    {
        _cache = cache;
        _frame = frame;
        _page = frame.Buffer!;
    }

    /// <summary>Initializes a lease over a page owned by a write transaction, which needs no pin.</summary>
    internal PageLease(byte[] page)
    {
        _cache = null;
        _frame = null;
        _page = page;
    }

    public PageType Type => PageLayout.Type(_page);

    public ReadOnlySpan<byte> Span => _page.AsSpan(PageLayout.HeaderSize, _page.Length - PageLayout.Overhead);

    public ReadOnlyMemory<byte> Payload => new(_page, PageLayout.HeaderSize, _page.Length - PageLayout.Overhead);

    /// <summary>Copies the whole page, header included.</summary>
    public void CopyPageTo(Span<byte> destination) => _page.CopyTo(destination);

    public void Dispose()
    {
        if (_frame is not null)
        {
            _cache!.Release(_frame);
        }
    }
}
