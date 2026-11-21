// Copyright (c) Tekium. Licensed under the MIT License.

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace TekiumDB.Caching;

/// <summary>How a <see cref="TkDistributedCache"/> behaves.</summary>
public sealed class TkCacheOptions
{
    /// <summary>
    /// Gets or sets how often expired entries are deleted from the file. There is no background thread:
    /// the deletion happens inside a write (<c>SetAsync</c>) once this long has passed since the last one.
    /// An expired entry is never returned, deleted or not. Thirty minutes by default.
    /// </summary>
    public TimeSpan ExpiredItemsDeletionInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Gets or sets how much of a sliding window must have passed before a read renews it, from 0 to 1.
    /// Renewing is a write, and a write is a durable commit; with 0 every read of a sliding entry is one.
    /// With the default, 0.1, a read in the first tenth of the window renews nothing, so an entry lives
    /// between 90 % and 100 % of its window after its last read.
    /// </summary>
    public double SlidingRenewalThreshold { get; set; } = 0.1;

    /// <summary>Gets or sets where the time comes from. The system clock by default.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}

/// <summary>
/// An <see cref="IDistributedCache"/> kept in a TekiumDB database: a cache that survives restarts, in a
/// local file, with no server. Only its asynchronous methods work: TekiumDB does no synchronous I/O, so
/// <c>Get</c>, <c>Set</c>, <c>Refresh</c> and <c>Remove</c> throw <see cref="NotSupportedException"/>.
/// </summary>
public sealed class TkDistributedCache : IDistributedCache
{
    private const string AsyncOnly = "TekiumDB does no synchronous I/O: use the Async method.";

    private readonly TekiumDb _database;
    private readonly Table<CacheEntry> _entries;
    private readonly TkCacheOptions _options;
    private long _lastSweep;

    /// <summary>Initializes a new instance of the <see cref="TkDistributedCache"/> class.</summary>
    /// <param name="database">The database that holds the entries, in a table of its own (<c>tekium_cache</c>).</param>
    /// <param name="options">How the cache behaves. Null for the defaults.</param>
    public TkDistributedCache(TekiumDb database, TkCacheOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _options = options ?? new TkCacheOptions();
        if (_options.ExpiredItemsDeletionInterval < TimeSpan.Zero || _options.SlidingRenewalThreshold is not (>= 0 and <= 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "ExpiredItemsDeletionInterval cannot be negative, and SlidingRenewalThreshold goes from 0 to 1.");
        }

        _database = database;
        _entries = database.Table<CacheEntry>();
        _lastSweep = Now;
    }

    private long Now => _options.TimeProvider.GetUtcNow().UtcTicks;

    /// <inheritdoc/>
    public byte[]? Get(string key) => throw new NotSupportedException(AsyncOnly);

    /// <inheritdoc/>
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new NotSupportedException(AsyncOnly);

    /// <inheritdoc/>
    public void Refresh(string key) => throw new NotSupportedException(AsyncOnly);

    /// <inheritdoc/>
    public void Remove(string key) => throw new NotSupportedException(AsyncOnly);

    /// <inheritdoc/>
    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var now = Now;
        var entry = await _entries.GetAsync(key, token).ConfigureAwait(false);
        if (entry is null || entry.ExpiresAt <= now)
        {
            return null;
        }

        if (NeedsRenewal(entry, now))
        {
            await RenewAsync(key, token).ConfigureAwait(false);
        }

        return entry.Value;
    }

    /// <inheritdoc/>
    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);
        var now = Now;
        var absolute = options.AbsoluteExpirationRelativeToNow is { } relative
            ? now + relative.Ticks
            : options.AbsoluteExpiration?.UtcTicks ?? long.MaxValue;
        if (absolute <= now)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The absolute expiration must be in the future.");
        }

        var sliding = options.SlidingExpiration?.Ticks ?? 0;
        if (options.SlidingExpiration is not null && sliding <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The sliding expiration must be positive.");
        }

        var entry = new CacheEntry
        {
            Key = key,
            Value = value,
            AbsoluteExpiration = absolute,
            SlidingTicks = sliding,
            ExpiresAt = sliding > 0 ? Math.Min(absolute, now + sliding) : absolute,
        };
        var writer = await _database.BeginWriteAsync(token).ConfigureAwait(false);
        await using (writer.ConfigureAwait(false))
        {
            await _entries.UpsertAsync(writer, entry, token).ConfigureAwait(false);
            if (_options.ExpiredItemsDeletionInterval > TimeSpan.Zero && now - Volatile.Read(ref _lastSweep) >= _options.ExpiredItemsDeletionInterval.Ticks)
            {
                await SweepAsync(writer, now, token).ConfigureAwait(false);
                Volatile.Write(ref _lastSweep, now);
            }

            await writer.CommitAsync(token).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var now = Now;
        if (await _entries.GetAsync(key, token).ConfigureAwait(false) is { } entry && entry.ExpiresAt > now && NeedsRenewal(entry, now))
        {
            await RenewAsync(key, token).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var writer = await _database.BeginWriteAsync(token).ConfigureAwait(false);
        await using (writer.ConfigureAwait(false))
        {
            if (await _entries.DeleteAsync(writer, key, token).ConfigureAwait(false))
            {
                await writer.CommitAsync(token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Deletes every expired entry from the file now, whatever <see cref="TkCacheOptions.ExpiredItemsDeletionInterval"/> says.</summary>
    /// <param name="cancellationToken">Cancels the deletion.</param>
    /// <returns>How many entries were deleted.</returns>
    public async ValueTask<int> RemoveExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = Now;
        var writer = await _database.BeginWriteAsync(cancellationToken).ConfigureAwait(false);
        await using (writer.ConfigureAwait(false))
        {
            var removed = await SweepAsync(writer, now, cancellationToken).ConfigureAwait(false);
            if (removed > 0)
            {
                await writer.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            Volatile.Write(ref _lastSweep, now);
            return removed;
        }
    }

    /// <summary>Whether a read at this moment has to push the expiration of a sliding entry forward.</summary>
    private bool NeedsRenewal(CacheEntry entry, long now)
    {
        if (entry.SlidingTicks == 0)
        {
            return false;
        }

        var renewed = Math.Min(entry.AbsoluteExpiration, now + entry.SlidingTicks);
        return renewed - entry.ExpiresAt > 0 && renewed - entry.ExpiresAt >= entry.SlidingTicks * _options.SlidingRenewalThreshold;
    }

    /// <summary>Pushes the expiration forward, on the entry as the writer sees it: it may have changed, or gone, since it was read.</summary>
    private async ValueTask RenewAsync(string key, CancellationToken cancellationToken)
    {
        var writer = await _database.BeginWriteAsync(cancellationToken).ConfigureAwait(false);
        await using (writer.ConfigureAwait(false))
        {
            var now = Now;
            if (await _entries.GetAsync(writer, key, cancellationToken).ConfigureAwait(false) is { SlidingTicks: > 0 } current && current.ExpiresAt > now)
            {
                current.ExpiresAt = Math.Min(current.AbsoluteExpiration, now + current.SlidingTicks);
                await _entries.UpsertAsync(writer, current, cancellationToken).ConfigureAwait(false);
                await writer.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<int> SweepAsync(TkWriteTransaction writer, long now, CancellationToken cancellationToken)
    {
        var expired = new List<string>();
        await foreach (var entry in _entries.QueryAsync(writer, CacheEntry.Fields.ExpiresAt.AtMost(now), cancellationToken).ConfigureAwait(false))
        {
            expired.Add(entry.Key);
        }

        foreach (var key in expired)
        {
            await _entries.DeleteAsync(writer, key, cancellationToken).ConfigureAwait(false);
        }

        return expired.Count;
    }
}

/// <summary>Registers a <see cref="TkDistributedCache"/> as the <see cref="IDistributedCache"/> of an application.</summary>
public static class TkCacheServiceCollectionExtensions
{
    /// <summary>Adds a distributed cache kept in a TekiumDB database.</summary>
    /// <param name="services">The services of the application.</param>
    /// <param name="database">Gives the database that holds the cache.</param>
    /// <param name="configure">Changes how the cache behaves.</param>
    /// <returns>The same services.</returns>
    public static IServiceCollection AddTekiumDistributedCache(
        this IServiceCollection services, Func<IServiceProvider, TekiumDb> database, Action<TkCacheOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(database);
        var options = new TkCacheOptions();
        configure?.Invoke(options);
        return services.AddSingleton<IDistributedCache>(provider => new TkDistributedCache(database(provider), options));
    }
}
