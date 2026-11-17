// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Caching;

/// <summary>An entry of the cache, as the table <c>tekium_cache</c> keeps it. Times are UTC ticks.</summary>
[TkTable("tekium_cache")]
internal sealed partial class CacheEntry
{
    /// <summary>Gets or sets the key of the entry.</summary>
    [TkKey]
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the cached bytes.</summary>
    public byte[] Value { get; set; } = [];

    /// <summary>Gets or sets when the entry stops being returned. Indexed, to find the expired ones without reading the rest.</summary>
    [TkIndex]
    public long ExpiresAt { get; set; }

    /// <summary>Gets or sets the moment the entry expires whatever is read; <see cref="long.MaxValue"/> for never.</summary>
    public long AbsoluteExpiration { get; set; }

    /// <summary>Gets or sets how long the entry lives after its last read; zero if it does not slide.</summary>
    public long SlidingTicks { get; set; }
}
