// Copyright (c) Tekium. Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Tekium.Embedded.Catalog;
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

    // The retained commits, oldest first. Only the writer touches it, and only after its commit is durable.
    private readonly Queue<ulong> _history = new();
    private readonly int _retention;

    // Whether the catalog says the history does not go back to the creation of the database.
    private bool _historyIncomplete;

    // Whether the catalog holds a history nobody keeps any more, to be removed by the next commit.
    private bool _historyAbandoned;

    private TaskCompletionSource _committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _watchers;

    private TekiumDb(Pager pager, int retention, TkDiagnostics? diagnostics)
    {
        Diagnostics = diagnostics;
        Pager = pager;
        Tree = new BTree(pager);
        _retention = retention;
    }

    /// <summary>The name of the <see cref="System.Diagnostics.Metrics.Meter"/> a database opened with <see cref="DbOptions.Diagnostics"/> publishes its counters through.</summary>
    public const string MeterName = "Tekium.Embedded";

    /// <summary>Marks a <see cref="Table{T}"/> so that tables of every row type fit in one dictionary.</summary>
    internal interface ITable;

    /// <summary>Gets a value indicating whether the database is encrypted.</summary>
    public bool IsEncrypted => Pager.IsEncrypted;

    /// <summary>Gets a value indicating whether the pages of the database are compressed.</summary>
    public bool IsCompressed => Pager.IsCompressed;

    /// <summary>
    /// Gets the id of the latest transaction that committed a change. A consumer that passes it to
    /// <see cref="WatchAsync{T}"/> gets the changes made from now on.
    /// </summary>
    public ulong LastTransactionId => Pager.Current.TransactionId.Value;

    internal Pager Pager { get; }

    /// <summary>Gets the counters of the database, if it was opened with them.</summary>
    internal TkDiagnostics? Diagnostics { get; }

    /// <summary>Gets or sets how many finished pages a bulk load keeps in memory before writing them to the file.</summary>
    internal int BulkSpillPages { get; set; } = 2048;

    /// <summary>Gets or sets how many bytes of entries a bulk load sorts in memory before writing them out as a run.</summary>
    internal int BulkSortBudget { get; set; } = 64 << 20;

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
            ArgumentOutOfRangeException.ThrowIfNegative(options.ChangeRetention, nameof(options));
            var counters = options.Diagnostics ? new PagerDiagnostics() : null;
            var pagerOptions = new PagerOptions
            {
                Diagnostics = counters,
                PageSize = pageSize,
                CachePages = (int)Math.Clamp(options.CacheSizeMb * 1048576L / pageSize.Bytes, MinimumCachePages, int.MaxValue),
                KeyDerivation = options.KeyDerivation,
                Compression = options.Compression,
                CompressionLevel = options.CompressionLevel,
            };

            var pager = owned.Length == 0
                ? await Pager.CreateAsync(owned, pagerOptions, options.Password.Span, cancellationToken).ConfigureAwait(false)
                : await Pager.OpenAsync(owned, pagerOptions, options.Password.Span, cancellationToken).ConfigureAwait(false);
            owned = null;
            var database = new TekiumDb(
                pager,
                options.ChangeRetention,
                counters is null ? null : new TkDiagnostics(options.DiagnosticsName ?? options.Path ?? "memory", counters));
            try
            {
                await database.LoadHistoryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await pager.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return database;
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

    /// <summary>
    /// Reports what every commit after a transaction did to the rows of a table: one change per row
    /// and commit, in commit order and, within a commit, in key order. Once it has caught up it waits
    /// for the next commit; it ends only by cancellation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It runs after the commit and outside the writer: a slow consumer does not slow writes down. A
    /// consumer that records the <see cref="TkChange{T}.TransactionId"/> of the last commit it fully
    /// handled can resume from there, also after the database was closed and reopened.
    /// </para>
    /// <para>
    /// Only the last <see cref="DbOptions.ChangeRetention"/> commits are kept. A consumer further
    /// behind is cut off with <see cref="TkChangeFeedException"/>. The changes of one commit are read
    /// into memory before the first is handed over.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The row type of the table.</typeparam>
    /// <param name="sinceTransaction">The last transaction already seen. Zero is the creation of the database.</param>
    /// <param name="cancellationToken">Ends the feed.</param>
    /// <returns>The changes.</returns>
    /// <exception cref="InvalidOperationException">The database was opened without <see cref="DbOptions.ChangeRetention"/>.</exception>
    public IAsyncEnumerable<TkChange<T>> WatchAsync<T>(ulong sinceTransaction, CancellationToken cancellationToken = default)
        where T : class, ITkRow<T>
    {
        if (_retention == 0)
        {
            throw new InvalidOperationException(
                "The change feed is off: open the database with DbOptions.ChangeRetention set to the number of commits to keep.");
        }

        return WatchCoreAsync(Table<T>(), sinceTransaction, cancellationToken);
    }

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

    /// <summary>
    /// Writes a copy of the database as it is at this moment, while it stays open and in use. The copy
    /// holds exactly what the last commit left: whatever is written while the backup runs is not in it,
    /// and nothing is ever half in it. Writers are not blocked and readers are not slowed: the backup
    /// reads the file directly, past the cache.
    /// </summary>
    /// <remarks>
    /// While it runs, the pages of the state being copied are not reused, so a database under heavy
    /// writing grows for as long as the backup takes. The copy is compact (every page full, none free),
    /// has a file key of its own and does not carry the retained history of changes.
    /// </remarks>
    /// <param name="destination">The file to create. It must not exist.</param>
    /// <param name="options">The password of the copy, if any.</param>
    /// <param name="cancellationToken">Cancels the backup. The destination is removed.</param>
    /// <returns>What was copied, and as of which transaction.</returns>
    /// <exception cref="ArgumentException">The database is encrypted and the options do not say how to protect the copy.</exception>
    /// <exception cref="InvalidDataException">A damaged page was found: there is no backup.</exception>
    public async ValueTask<TkBackupReport> BackupAsync(
        string destination, TkBackupOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(destination);
        var created = false;
        try
        {
            return await BackupAsync(
                _ =>
                {
                    var device = FileStorageDevice.Open(destination, FileMode.CreateNew);
                    created = true;
                    return device;
                },
                options,
                null,
                cancellationToken).ConfigureAwait(false);
        }
        catch when (created)
        {
            // A backup that did not finish is not left behind to be mistaken for one that did.
            File.Delete(destination);
            throw;
        }
    }

    /// <summary>
    /// Starts reading the database as it was when a past transaction committed. Nothing that happened
    /// afterwards is seen. If no commit has that id (a transaction that was rolled back, or a number
    /// between two commits), the state is that of the last commit before it.
    /// </summary>
    /// <remarks>
    /// Past states are kept only as far back as <see cref="DbOptions.ChangeRetention"/> says: that many
    /// commits before the current one. While the transaction returned lives, the state it reads is not
    /// let go of, however far the writer moves on: dispose it.
    /// </remarks>
    /// <param name="transactionId">
    /// The transaction, as given by <see cref="TkWriteTransaction.TransactionId"/>,
    /// <see cref="LastTransactionId"/> or the changes of <see cref="WatchAsync{T}"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A read transaction over that state. <see cref="TkReadTransaction.TransactionId"/> says which commit it is.</returns>
    /// <exception cref="TkStateUnavailableException">The state is older than the database keeps.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The transaction has not happened yet.</exception>
    public async ValueTask<TkReadTransaction> AsOfAsync(ulong transactionId, CancellationToken cancellationToken = default)
    {
        var read = ReadTransaction.Begin(Pager);
        try
        {
            var current = read.Id.Value;
            ArgumentOutOfRangeException.ThrowIfGreaterThan(transactionId, current);
            if (transactionId == current)
            {
                return new TkReadTransaction(this, read);
            }

            // Each commit wrote down where the state before it was: the one in force at that transaction
            // is the last of those at or before it.
            var found = _retention == 0
                ? default
                : await HistoryStore.FindAsync(read, read.Root, transactionId, cancellationToken).ConfigureAwait(false);

            // Held by itself, so that the writer may trim the history past it without taking its pages.
            // The state from before the first commit is in the history too: the empty database.
            if (!found.HasBasis || !Pager.TryRetain(found.BasisTransaction, out var pin))
            {
                throw new TkStateUnavailableException(
                    $"The state as of transaction {transactionId} is no longer kept: the database keeps the last {_retention} commits before the current one (DbOptions.ChangeRetention).");
            }

            return new TkReadTransaction(this, read, found.Basis, found.BasisTransaction, pin);
        }
        catch
        {
            await read.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Checks the database as its last commit left it, while it stays open and in use: every page in use
    /// is read from the file and checked (its checksum, or its authentication tag if the database is
    /// encrypted), every tree is checked to be well formed, and the list of free pages is checked not to
    /// offer a page in use nor to miss one. What is wrong is reported with its page and what it belongs to.
    /// </summary>
    /// <remarks>
    /// It reads the file, not the cache: a page damaged on disk is found even if the application still
    /// has a good copy in memory. Writers are not blocked; like any reader, it keeps the pages of the
    /// state it checks from being reused until it is done.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the verification.</param>
    /// <returns>What was found.</returns>
    public ValueTask<TkVerification> VerifyAsync(CancellationToken cancellationToken = default) => VerifyAsync(null, cancellationToken);

    /// <summary>Verifies the latest commit. <paramref name="afterSnapshot"/> runs once the state to check is fixed.</summary>
    internal async ValueTask<TkVerification> VerifyAsync(Func<ValueTask>? afterSnapshot, CancellationToken cancellationToken)
    {
        var snapshot = Pager.BeginRead();
        try
        {
            var state = snapshot.Snapshot;
            if (afterSnapshot is not null)
            {
                await afterSnapshot().ConfigureAwait(false);
            }

            var findings = new List<TkFinding>();
            var trees = new List<TkTreeSummary>();
            var source = new Maintenance.DirectPageSource(Pager);
            var walker = new Maintenance.TreeWalker(source, state.PageCount, findings, cancellationToken);
            await Maintenance.DatabaseWalk.TreesAsync(walker, state.Root, findings, trees, null).ConfigureAwait(false);
            var free = await Maintenance.FreeListCheck.RunAsync(source, Pager.PageSize, state, walker.Visited, findings, cancellationToken)
                .ConfigureAwait(false);
            return new TkVerification
            {
                TransactionId = state.TransactionId.Value,
                PagesInUse = walker.Visited.Count,
                PagesFree = free,
                Trees = trees,
                Findings = findings,
            };
        }
        finally
        {
            snapshot.Dispose();
        }
    }

    /// <summary>Backs up onto the device <paramref name="destination"/> returns. <paramref name="afterSnapshot"/> runs once the state to copy is fixed.</summary>
    internal async ValueTask<TkBackupReport> BackupAsync(
        Func<DbOptions, IStorageDevice> destination, TkBackupOptions? options, Func<ValueTask>? afterSnapshot, CancellationToken cancellationToken)
    {
        options ??= new TkBackupOptions();
        if (options.Unencrypted ? !options.Password.IsEmpty : IsEncrypted && options.Password.IsEmpty)
        {
            throw new ArgumentException(
                options.Unencrypted
                    ? "A backup cannot be both unencrypted and protected by a password."
                    : "The database is encrypted: give the backup a password, or say explicitly that it is to be left unencrypted.",
                nameof(options));
        }

        // The snapshot keeps every page of this state from being reused until the copy is done.
        var snapshot = ReadTransaction.Begin(Pager);
        await using (snapshot.ConfigureAwait(false))
        {
            var transaction = snapshot.Id.Value;
            var (entries, bytes, _) = await TkMaintenance.CopyTreesAsync(
                new Maintenance.DirectPageSource(Pager), snapshot.Root, Pager.Current.PageCount, Pager.PageSize, IsCompressed, [], destination,
                options.Password, salvage: false, afterSnapshot, cancellationToken).ConfigureAwait(false);
            return new TkBackupReport { TransactionId = transaction, EntriesCopied = entries, Bytes = bytes };
        }
    }

    /// <summary>Closes the database.</summary>
    /// <returns>A task that completes when the file is closed.</returns>
    public ValueTask DisposeAsync()
    {
        Diagnostics?.Dispose();
        return Pager.DisposeAsync();
    }

    /// <summary>
    /// Writes into the catalog of a transaction about to commit where the state before it was, and
    /// drops the entries that fall out of the retention. Nothing is remembered here until the commit
    /// is durable: see <see cref="Committed"/>.
    /// </summary>
    internal async ValueTask<HistoryChange> RecordHistoryAsync(WriteTransaction transaction, CancellationToken cancellationToken)
    {
        var basis = transaction.Basis;
        if (transaction.Root == basis.Root && transaction.Dirty.Count == 0)
        {
            // Nothing changed: there will be no commit, and no state to remember.
            return default;
        }

        if (_retention == 0)
        {
            if (_historyAbandoned)
            {
                // A history with a gap would report changes that never happened as one: better none.
                var (stale, _) = await HistoryStore.LoadAsync(transaction, transaction.Root, cancellationToken).ConfigureAwait(false);
                foreach (var old in stale)
                {
                    await Tree.DeleteAsync(transaction, HistoryStore.Key(old), cancellationToken).ConfigureAwait(false);
                }

                await Tree.DeleteAsync(transaction, HistoryStore.Marker, cancellationToken).ConfigureAwait(false);
            }

            return new HistoryChange(true, null, 0, false);
        }

        var id = basis.TransactionId.Value;
        await Tree.PutAsync(transaction, HistoryStore.Key(id), HistoryStore.Value(basis.Root), cancellationToken).ConfigureAwait(false);
        var trimmed = Math.Max(0, _history.Count + 1 - _retention);
        var position = 0;
        foreach (var old in _history)
        {
            if (position++ == trimmed)
            {
                break;
            }

            await Tree.DeleteAsync(transaction, HistoryStore.Key(old), cancellationToken).ConfigureAwait(false);
        }

        // From here on the history no longer starts at the creation of the database.
        var incomplete = !_historyIncomplete && (trimmed > 0 || (_history.Count == 0 && !basis.Root.IsNone));
        if (incomplete)
        {
            await Tree.PutAsync(transaction, HistoryStore.Marker, new byte[1], cancellationToken).ConfigureAwait(false);
        }

        return new HistoryChange(true, id, trimmed, incomplete);
    }

    /// <summary>After a commit is durable: remembers its history, lets go of what fell out, and wakes the watchers.</summary>
    internal void Committed(HistoryChange change)
    {
        if (!change.Happened)
        {
            return;
        }

        _historyAbandoned = false;
        if (change.Added is { } added)
        {
            _history.Enqueue(added);
            for (var i = 0; i < change.Trimmed; i++)
            {
                _history.Dequeue();
            }

            _historyIncomplete |= change.Incomplete;
            Pager.SetRetentionFloor(_history.Peek());
        }

        // Swapped only if somebody is waiting. A watcher counts itself before looking at the state, so
        // either it is counted here or it sees this commit.
        if (Volatile.Read(ref _watchers) > 0)
        {
            Interlocked.Exchange(ref _committed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();
        }
    }

    private async ValueTask LoadHistoryAsync(CancellationToken cancellationToken)
    {
        using var read = Pager.BeginRead();
        var (transactions, incomplete) = await HistoryStore.LoadAsync(Pager, read.Root, cancellationToken).ConfigureAwait(false);
        if (_retention == 0)
        {
            _historyAbandoned = transactions.Count > 0 || incomplete;
            return;
        }

        // Before the first write: the states the history names stay readable in this session too.
        _historyIncomplete = incomplete;
        foreach (var transaction in transactions)
        {
            _history.Enqueue(transaction);
        }

        if (transactions.Count > 0)
        {
            Pager.SetRetentionFloor(transactions[0]);
        }
    }

    private async IAsyncEnumerable<TkChange<T>> WatchCoreAsync<T>(
        Table<T> table, ulong position, [EnumeratorCancellation] CancellationToken cancellationToken)
        where T : class, ITkRow<T>
    {
        var changes = new List<TkChange<T>>();
        Interlocked.Increment(ref _watchers);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Taken before looking: a commit that lands in between completes this one.
                var committed = Volatile.Read(ref _committed).Task;
                changes.Clear();
                var next = await NextCommitAsync(table, position, changes, cancellationToken).ConfigureAwait(false);
                if (next is null)
                {
                    await committed.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                position = next.Value;
                foreach (var change in changes)
                {
                    yield return change;
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _watchers);
        }
    }

    /// <summary>
    /// Reads what the commit after <paramref name="position"/> did to a table. Returns that commit, or
    /// <see langword="null"/> if there is none yet.
    /// </summary>
    private async ValueTask<ulong?> NextCommitAsync<T>(
        Table<T> table, ulong position, List<TkChange<T>> changes, CancellationToken cancellationToken)
        where T : class, ITkRow<T>
    {
        ulong next;
        PageNumber basis;
        PageNumber nextRoot;
        long pin;
        using (var read = Pager.BeginRead())
        {
            var current = read.Snapshot.TransactionId.Value;
            if (position >= current)
            {
                return null;
            }

            var found = await HistoryStore.FindAsync(Pager, read.Root, position, cancellationToken).ConfigureAwait(false);
            (basis, next, nextRoot) = found.HasNext ? (found.Basis, found.Next, found.NextRoot) : (found.Basis, current, read.Root);

            // The two states are compared after letting go of the latest one. The older of them is held by
            // itself, so that the writer may move on meanwhile without taking its pages; if it is the
            // empty database from before the first commit, there is nothing of it to hold.
            if (found.Lost || !Pager.TryRetain(found.HasBasis ? found.BasisTransaction : next, out pin))
            {
                throw new TkChangeFeedException(
                    $"The changes after transaction {position} are no longer retained: the database keeps the last {_retention} commits. " +
                    "Start again from the current state of the table.");
            }
        }

        try
        {
            await table.CollectChangesAsync(basis, nextRoot, next, changes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Pager.Release(pin);
        }

        return next;
    }

    /// <summary>What a commit does to the history, to apply once it is durable.</summary>
    /// <param name="Happened">Whether there is a commit at all.</param>
    /// <param name="Added">The transaction whose state the commit recorded, if the history is kept.</param>
    /// <param name="Trimmed">How many of the oldest retained transactions it dropped.</param>
    /// <param name="Incomplete">Whether it marked the history as not going back to the creation.</param>
    internal readonly record struct HistoryChange(bool Happened, ulong? Added, int Trimmed, bool Incomplete);
}
