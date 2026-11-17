// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Maintenance;

/// <summary>Reads the pages of an open database straight from its file, leaving its cache alone.</summary>
internal sealed class DirectPageSource(Pager pager) : IPageSource
{
    public bool TryPin(PageNumber number, out PageLease lease)
    {
        lease = default;
        return false;
    }

    public ValueTask<PageLease> PinAsync(PageNumber number, CancellationToken cancellationToken) =>
        pager.ReadDirectAsync(number, cancellationToken);
}
