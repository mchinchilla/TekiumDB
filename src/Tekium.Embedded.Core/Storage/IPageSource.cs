// Copyright (c) Tekium. Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace Tekium.Embedded.Storage;

/// <summary>Where a tree reads its pages from: the committed state or a write transaction.</summary>
internal interface IPageSource
{
    /// <summary>Synchronous fast path: succeeds when the page is already in memory.</summary>
    bool TryGetCachedPage(PageNumber number, [NotNullWhen(true)] out byte[]? page);

    ValueTask<byte[]> GetPageAsync(PageNumber number, CancellationToken cancellationToken);
}
