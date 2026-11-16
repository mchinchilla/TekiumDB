// Copyright (c) Tekium. Licensed under the MIT License.

using System.Collections.Concurrent;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Storage;
using Tekium.Embedded.Transactions;

namespace Tekium.Embedded;

/// <summary>
/// An open database: one file, or memory. Any number of readers and one writer at a time, all in this
/// process. Dispose it to close the file.
/// </summary>
public sealed class TekiumDb : IAsyncDisposable
{
    private const int MinimumCachePages = 16;

    private readonly ConcurrentDictionary<Type, ITable> _tables = new();

    private TekiumDb(Pager pager)
    {
        Pager = pager;
        Tree = new BTree(pager);
    }

    /// <summary>Marks a <see cref="Table{T}"/> so that tables of every row type fit in one dictionary.</summary>
    internal interface ITable;

    /// <summary>Gets a value indicating whether the database is encrypted.</summary>
    public bool IsEncrypted => Pager.IsEncrypted;

    internal Pager Pager { get; }

    /// <summary>Gets the tree operations of the single writer. Only whoever holds the write transaction may use it.</summary>
    internal BTree Tree { get; }

    /// <summary>Opens the database, creating it if the file does not exist or is empty.</summary>
    /// <param name="options">Where the database is and how to open it.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The open database.</returns>
    public static ValueTask<TekiumDb> OpenAsync(DbOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.InMemory == options.Path is not null)
        {
            return ValueTask.FromException<TekiumDb>(new ArgumentException(
                "Set either Path, for a database in a file, or InMemory, for one that lives in memory.", nameof(options)));
        }

        return OpenAsync(
            static options => options.InMemory
                ? new MemoryStorageDevice()
                : FileStorageDevice.Open(options.Path!, FileMode.OpenOrCreate),
            options,
            cancellationToken);
    }

    /// <summary>Opens the database over the device <paramref name="open"/> returns, which the database then owns.</summary>
    internal static async ValueTask<TekiumDb> OpenAsync(
        Func<DbOptions, IStorageDevice> open, DbOptions options, CancellationToken cancellationToken)
    {
        // Owned here until a pager takes it; on any failure before that, it is closed.
        var owned = open(options);
        try
        {
            if (!PageSize.TryFromBytes(options.PageSize, out var pageSize))
            {
                throw new ArgumentException("The page size must be 4096, 8192, 16384 or 32768.", nameof(options));
            }

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.CacheSizeMb, nameof(options));
            var pagerOptions = new PagerOptions
            {
                PageSize = pageSize,
                CachePages = (int)Math.Clamp(options.CacheSizeMb * 1048576L / pageSize.Bytes, MinimumCachePages, int.MaxValue),
                KeyDerivation = options.KeyDerivation,
            };

            var pager = owned.Length == 0
                ? await Pager.CreateAsync(owned, pagerOptions, options.Password.Span, cancellationToken).ConfigureAwait(false)
                : await Pager.OpenAsync(owned, pagerOptions, options.Password.Span, cancellationToken).ConfigureAwait(false);
            owned = null;
            return new TekiumDb(pager);
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Gets the table that stores rows of type <typeparamref name="T"/>. Costs nothing: the table is created on its first write.</summary>
    /// <typeparam name="T">A class marked with <see cref="TkTableAttribute"/>.</typeparam>
    /// <returns>The table.</returns>
    public Table<T> Table<T>()
        where T : class, ITkRow<T> =>
        (Table<T>)_tables.GetOrAdd(typeof(T), static (_, database) => new Table<T>(database), this);

    /// <summary>Starts the write transaction, waiting for the current one to finish if there is one.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The transaction. Dispose it; without a commit that rolls back.</returns>
    public async ValueTask<TkWriteTransaction> BeginWriteAsync(CancellationToken cancellationToken = default) =>
        new(this, await Pager.BeginWriteAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Starts reading the latest committed state. Never waits.</summary>
    /// <returns>The transaction. Dispose it.</returns>
    public TkReadTransaction BeginRead() => new(this, ReadTransaction.Begin(Pager));

    /// <summary>Changes the password of an encrypted database. Only the header is rewritten.</summary>
    /// <param name="newPassword">The new password. Not kept.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the new password is the one that opens the database.</returns>
    public ValueTask ChangePasswordAsync(ReadOnlyMemory<char> newPassword, CancellationToken cancellationToken = default) =>
        Pager.ChangePasswordAsync(newPassword.Span, cancellationToken);

    /// <summary>Closes the database.</summary>
    /// <returns>A task that completes when the file is closed.</returns>
    public ValueTask DisposeAsync() => Pager.DisposeAsync();
}
