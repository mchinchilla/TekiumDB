// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers;
using System.Runtime.CompilerServices;
using Tekium.Embedded.Catalog;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Storage;

namespace Tekium.Embedded;

/// <summary>
/// The rows of one type, kept in key order. Reads see the latest commit unless given a transaction;
/// writes always go through the write transaction.
/// </summary>
/// <typeparam name="T">The row type, a class marked with <see cref="TkTableAttribute"/>.</typeparam>
public sealed class Table<T> : TekiumDb.ITable
    where T : class, ITkRow<T>
{
    private readonly TekiumDb _database;
    private readonly TkColumnType _keyType;

    // How to read rows of each committed schema version. A committed version never changes, so an entry is set once.
    private readonly ReadPlan?[] _plans = new ReadPlan?[byte.MaxValue + 1];

    // Where the rows were as of the last commit a read went through.
    private Location? _latest;

    internal Table(TekiumDb database)
    {
        _database = database;
        Name = T.TableName;
        TableKey = CatalogStore.TableKey(Name);

        var columns = T.Columns;
        if (columns.IsEmpty || columns.Length > TableSchema.MaxColumns || (uint)T.KeyColumn >= (uint)columns.Length)
        {
            throw new TkSchemaException($"Table '{Name}' must have between 1 and {TableSchema.MaxColumns} columns, one of them the key.");
        }

        var key = columns[T.KeyColumn];
        if (key.IsNullable || !TkKey.IsSupported(key.Type))
        {
            throw new TkSchemaException($"Table '{Name}': column '{key.Name}' cannot be the key. A key is a non-nullable Guid, int, long, uint, ulong or string.");
        }

        for (var i = 1; i < columns.Length; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (string.Equals(columns[i].Name, columns[j].Name, StringComparison.Ordinal))
                {
                    throw new TkSchemaException($"Table '{Name}' declares column '{columns[i].Name}' twice.");
                }
            }
        }

        _keyType = key.Type;
    }

    private enum WriteMode
    {
        Insert,
        Update,
        Upsert,
    }

    /// <summary>Gets the name of the table in the database.</summary>
    public string Name { get; }

    internal byte[] TableKey { get; }

    /// <summary>Reads the row with this key from the latest commit.</summary>
    /// <typeparam name="TKey">The type of the key column.</typeparam>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The row, or <see langword="null"/> if there is none.</returns>
    public async ValueTask<T?> GetAsync<TKey>(TKey key, CancellationToken cancellationToken = default)
    {
        using var snapshot = _database.Pager.BeginRead();
        return await GetCoreAsync(
            _database.Pager, snapshot.Root, snapshot.Snapshot.TransactionId.Value, null, key, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Reads the row with this key as the transaction sees it.</summary>
    /// <typeparam name="TKey">The type of the key column.</typeparam>
    /// <param name="transaction">A read transaction, or the write transaction to see its own changes.</param>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The row, or <see langword="null"/> if there is none.</returns>
    public ValueTask<T?> GetAsync<TKey>(TkTransaction transaction, TKey key, CancellationToken cancellationToken = default)
    {
        Check(transaction);
        return GetCoreAsync(
            transaction.Source,
            transaction.CatalogRoot,
            (transaction as TkReadTransaction)?.SnapshotId ?? 0,
            transaction as TkWriteTransaction,
            key,
            cancellationToken);
    }

    /// <summary>Reads every row of the latest commit, in key order.</summary>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>The rows. The commit stays pinned until the enumeration ends.</returns>
    public async IAsyncEnumerable<T> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var snapshot = _database.Pager.BeginRead();
        var rows = ScanCoreAsync(
            _database.Pager, snapshot.Root, snapshot.Snapshot.TransactionId.Value, null, cancellationToken);
        await foreach (var row in rows.ConfigureAwait(false))
        {
            yield return row;
        }
    }

    /// <summary>Reads every row the transaction sees, in key order.</summary>
    /// <param name="transaction">A read transaction, or the write transaction. Do not change the table while enumerating it.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>The rows.</returns>
    public IAsyncEnumerable<T> ScanAsync(TkTransaction transaction, CancellationToken cancellationToken = default)
    {
        Check(transaction);
        return ScanCoreAsync(
            transaction.Source,
            transaction.CatalogRoot,
            (transaction as TkReadTransaction)?.SnapshotId ?? 0,
            transaction as TkWriteTransaction,
            cancellationToken);
    }

    /// <summary>Adds a row.</summary>
    /// <param name="transaction">The write transaction.</param>
    /// <param name="row">The row.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the row is part of the transaction.</returns>
    /// <exception cref="TkDuplicateKeyException">The table already has a row with that key.</exception>
    public async ValueTask InsertAsync(TkWriteTransaction transaction, T row, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await WriteAsync(transaction, row, WriteMode.Insert, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces the row that has the same key.</summary>
    /// <param name="transaction">The write transaction.</param>
    /// <param name="row">The row.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="false"/> if there was no row with that key, in which case nothing changes.</returns>
    public ValueTask<bool> UpdateAsync(TkWriteTransaction transaction, T row, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return WriteAsync(transaction, row, WriteMode.Update, cancellationToken);
    }

    /// <summary>Adds the row, or replaces the one that has the same key.</summary>
    /// <param name="transaction">The write transaction.</param>
    /// <param name="row">The row.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the row is part of the transaction.</returns>
    public async ValueTask UpsertAsync(TkWriteTransaction transaction, T row, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await WriteAsync(transaction, row, WriteMode.Upsert, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes the row with this key.</summary>
    /// <typeparam name="TKey">The type of the key column.</typeparam>
    /// <param name="transaction">The write transaction.</param>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="false"/> if there was no row with that key.</returns>
    public async ValueTask<bool> DeleteAsync<TKey>(
        TkWriteTransaction transaction, TKey key, CancellationToken cancellationToken = default)
    {
        Check(transaction);
        var tree = _database.Tree;
        var buffer = ArrayPool<byte>.Shared.Rent(tree.MaxKeySize);
        try
        {
            var keyLength = TkKey.Write(key, _keyType, buffer.AsSpan(0, tree.MaxKeySize));
            var state = await transaction.TableAsync(Name, TableKey, cancellationToken).ConfigureAwait(false);
            var (removed, root) = await tree.DeleteAsync(
                transaction.Inner, state.Root, buffer.AsMemory(0, keyLength), cancellationToken).ConfigureAwait(false);
            if (removed)
            {
                state.Root = root;
                state.Changed = true;
            }

            return removed;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static T Decode(ReadOnlySpan<byte> row, ReadPlan plan)
    {
        var reader = new TkRowReader(row, plan.Stored, plan.Members);
        return T.Read(ref reader);
    }

    private int Encode(T row, byte version, Span<byte> destination)
    {
        try
        {
            var writer = new TkRowWriter(destination, version, T.Columns.Length);
            T.Write(ref writer, row);
            return writer.Length;
        }
        catch (TkRowTooLargeException)
        {
            throw new TkRowTooLargeException(
                $"Table '{Name}': the row does not fit in {destination.Length} bytes, the most a row with that key may take with this page size.");
        }
    }

    private void Check(TkTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Database, _database))
        {
            throw new ArgumentException("The transaction belongs to another database.", nameof(transaction));
        }
    }

    private async ValueTask<bool> WriteAsync(
        TkWriteTransaction transaction, T row, WriteMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        Check(transaction);
        var state = await transaction.TableForWriteAsync(this, cancellationToken).ConfigureAwait(false);

        // One buffer: the key, then the row, then room to read the row already there.
        var tree = _database.Tree;
        var maxKey = tree.MaxKeySize;
        var maxEntry = tree.MaxEntrySize;
        var buffer = ArrayPool<byte>.Shared.Rent(maxKey + (2 * maxEntry));
        try
        {
            var keyLength = T.WriteKey(row, buffer.AsSpan(0, maxKey));
            var key = buffer.AsMemory(0, keyLength);
            var rowLength = Encode(row, state.Schema!.Version, buffer.AsSpan(maxKey, maxEntry - keyLength));
            if (mode != WriteMode.Upsert)
            {
                var existing = await BTree.GetAsync(
                    transaction.Inner, state.Root, key, buffer.AsMemory(maxKey + maxEntry, maxEntry), cancellationToken)
                    .ConfigureAwait(false);
                if (mode == WriteMode.Update && existing == BTree.NotFound)
                {
                    return false;
                }

                if (mode == WriteMode.Insert && existing != BTree.NotFound)
                {
                    throw new TkDuplicateKeyException($"Table '{Name}' already has a row with that key.");
                }
            }

            state.Root = await tree.PutAsync(
                transaction.Inner, state.Root, key, buffer.AsMemory(maxKey, rowLength), cancellationToken)
                .ConfigureAwait(false);
            state.Changed = true;
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask<T?> GetCoreAsync<TKey>(
        IPageSource source,
        PageNumber catalogRoot,
        ulong snapshotId,
        TkWriteTransaction? writer,
        TKey key,
        CancellationToken cancellationToken)
    {
        var tree = _database.Tree;
        var maxKey = tree.MaxKeySize;
        var buffer = ArrayPool<byte>.Shared.Rent(maxKey + tree.MaxEntrySize);
        try
        {
            var keyLength = TkKey.Write(key, _keyType, buffer.AsSpan(0, maxKey));
            var root = await RootAsync(source, catalogRoot, snapshotId, writer, cancellationToken).ConfigureAwait(false);
            var length = await BTree.GetAsync(
                source, root, buffer.AsMemory(0, keyLength), buffer.AsMemory(maxKey, tree.MaxEntrySize), cancellationToken)
                .ConfigureAwait(false);
            if (length == BTree.NotFound)
            {
                return null;
            }

            var row = buffer.AsMemory(maxKey, length);
            var plan = await PlanAsync(row, source, catalogRoot, writer, cancellationToken).ConfigureAwait(false);
            return Decode(row.Span, plan);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async IAsyncEnumerable<T> ScanCoreAsync(
        IPageSource source,
        PageNumber catalogRoot,
        ulong snapshotId,
        TkWriteTransaction? writer,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var root = await RootAsync(source, catalogRoot, snapshotId, writer, cancellationToken).ConfigureAwait(false);
        var entries = BTree.RangeAsync(source, root, default, default, cancellationToken);
        await foreach (var entry in entries.ConfigureAwait(false))
        {
            // The entry points into its page, which stays put until the scan moves on.
            var plan = await PlanAsync(entry.Value, source, catalogRoot, writer, cancellationToken).ConfigureAwait(false);
            yield return Decode(entry.Value.Span, plan);
        }
    }

    /// <summary>Where the rows are in the state being read. <see cref="PageNumber.None"/> if the table is empty or does not exist.</summary>
    private ValueTask<PageNumber> RootAsync(
        IPageSource source, PageNumber catalogRoot, ulong snapshotId, TkWriteTransaction? writer, CancellationToken cancellationToken)
    {
        if (writer is null && Volatile.Read(ref _latest) is { } latest && latest.SnapshotId == snapshotId)
        {
            return new ValueTask<PageNumber>(latest.Root);
        }

        return LookUpAsync();

        async ValueTask<PageNumber> LookUpAsync()
        {
            if (writer is not null)
            {
                return (await writer.TableAsync(Name, TableKey, cancellationToken).ConfigureAwait(false)).Root;
            }

            var info = await CatalogStore.FindTableAsync(source, catalogRoot, TableKey, cancellationToken)
                .ConfigureAwait(false);
            var root = info?.Root ?? PageNumber.None;

            // Remember it for the readers of the same commit, unless a newer one is already remembered.
            var known = Volatile.Read(ref _latest);
            if (known is null || known.SnapshotId < snapshotId)
            {
                Volatile.Write(ref _latest, new Location(snapshotId, root));
            }

            return root;
        }
    }

    /// <summary>How to read a stored row, according to the schema version in its first byte.</summary>
    private ValueTask<ReadPlan> PlanAsync(
        ReadOnlyMemory<byte> row,
        IPageSource source,
        PageNumber catalogRoot,
        TkWriteTransaction? writer,
        CancellationToken cancellationToken)
    {
        if (row.IsEmpty || row.Span[0] == 0)
        {
            return ValueTask.FromException<ReadPlan>(
                new InvalidDataException($"Table '{Name}': a stored row has no schema version."));
        }

        var version = row.Span[0];
        return Volatile.Read(ref _plans[version]) is { } plan
            ? new ValueTask<ReadPlan>(plan)
            : BuildAsync();

        async ValueTask<ReadPlan> BuildAsync()
        {
            // A version the write transaction registered is not in the catalog of anyone else, and may
            // never be: it is read from the transaction and not remembered here.
            if (writer is not null)
            {
                var state = await writer.TableAsync(Name, TableKey, cancellationToken).ConfigureAwait(false);
                if (state.UncommittedVersion == version)
                {
                    return ReadPlan.Build(Name, state.Schema!, T.Columns, T.KeyColumn);
                }
            }

            var schema = await CatalogStore.LoadSchemaAsync(
                source, catalogRoot, TableKey, version, _database.Tree.MaxEntrySize, cancellationToken)
                .ConfigureAwait(false);
            var built = ReadPlan.Build(Name, schema, T.Columns, T.KeyColumn);
            Volatile.Write(ref _plans[version], built);
            return built;
        }
    }

    private sealed record Location(ulong SnapshotId, PageNumber Root);
}
