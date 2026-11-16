// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Storage;

/// <summary>Where a tree reads its pages from: the committed state or a write transaction.</summary>
internal interface IPageSource
{
    /// <summary>Synchronous fast path: succeeds when the page is already in memory.</summary>
    bool TryPin(PageNumber number, out PageLease lease);

    ValueTask<PageLease> PinAsync(PageNumber number, CancellationToken cancellationToken);
}
