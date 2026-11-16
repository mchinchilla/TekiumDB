// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Storage;

internal sealed record PagerOptions
{
    /// <summary>Gets the page size of a new database. Ignored when opening: the file knows its own.</summary>
    public PageSize PageSize { get; init; } = PageSize.Default;

    /// <summary>Gets how many pages the cache keeps in memory. 32 768 pages of 8 KiB are 256 MiB.</summary>
    public int CachePages { get; init; } = 32_768;
}
