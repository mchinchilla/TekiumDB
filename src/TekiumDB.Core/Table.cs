// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using TekiumDB.Catalog;
using TekiumDB.Indexing;
using TekiumDB.Storage;

namespace TekiumDB;

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
    private readonly IndexDefinition[] _indexes;

    // How many of them hold an entry per row, as opposed to zone maps, which do not.
    private readonly int _entryIndexes;
    private readonly ConstraintSet _constraints;

    // The catalog key of the table each FOREIGN KEY refers to.
    private readonly byte[][] _parentKeys;

    // Room to evaluate the condition of any partial index of the table.
    private readonly int _whereScratch;

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
        _indexes = IndexDefinition.Build<T>(Name, TableKey);
        _constraints = ConstraintSet.Build<T>(Name, _indexes);
        _parentKeys = Array.ConvertAll(_constraints.ForeignKeys, static key => CatalogStore.TableKey(key.Table));
        foreach (var index in _indexes)
        {
            _whereScratch = Math.Max(_whereScratch, IndexDefinition.ScratchSize(index.Where));
            _entryIndexes += index.IsZoneMap ? 0 : 1;
        }
    }

    // What a bulk load keeps with each index entry while sorting: where its primary key starts, and whether it has a field with no value.
    private const int BulkEntryMark = sizeof(ushort) + 1;
    private const int NoValue = -1;
    private const int NoSuchKey = -2;

    private enum WriteMode
    {
        Insert,
        Update,
        Upsert,
    }

    /// <summary>Gets the name of the table in the database.</summary>
    public string Name { get; }

    internal byte[] TableKey { get; }

    internal IndexDefinition[] Indexes => _indexes;

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

    /// <summary>Reads the rows of the latest commit that meet a filter, through an index when one fits.</summary>
    /// <param name="filter">The conditions, built from the <c>Fields</c> of the row type.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>
    /// The rows, in the order of whatever was used to find them: the index, or else the key. The commit
    /// stays pinned until the enumeration ends.
    /// </returns>
    public IAsyncEnumerable<T> QueryAsync(TkFilter<T> filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return QueryLatestAsync(filter, cancellationToken);
    }

    /// <summary>Reads the rows the transaction sees that meet a filter, through an index when one fits.</summary>
    /// <param name="transaction">A read transaction, or the write transaction. Do not change the table while enumerating it.</param>
    /// <param name="filter">The conditions, built from the <c>Fields</c> of the row type.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The rows, in the order of whatever was used to find them: the index, or else the key.</returns>
    public IAsyncEnumerable<T> QueryAsync(
        TkTransaction transaction, TkFilter<T> filter, CancellationToken cancellationToken = default)
    {
        Check(transaction);
        ArgumentNullException.ThrowIfNull(filter);
        return QueryCoreAsync(
            transaction.Source,
            transaction.CatalogRoot,
            (transaction as TkReadTransaction)?.SnapshotId ?? 0,
            transaction as TkWriteTransaction,
            filter,
            cancellationToken);
    }

    /// <summary>Says how a query with this filter would reach its rows in the latest commit.</summary>
    /// <param name="filter">The conditions.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The plan. An index is used only if it exists as the row type declares it, and, if it is partial, only if the filter implies its condition.</returns>
    public async ValueTask<TkPlan> ExplainAsync(TkFilter<T> filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        using var snapshot = _database.Pager.BeginRead();
        var id = snapshot.Snapshot.TransactionId.Value;
        await RootAsync(_database.Pager, snapshot.Root, id, null, cancellationToken).ConfigureAwait(false);
        var states = await IndexStatesAsync(_database.Pager, snapshot.Root, id, null, cancellationToken).ConfigureAwait(false);
        var choice = Choose(filter.Normalize(), states);
        return new TkPlan(choice.Access, choice.Index >= 0 ? _indexes[choice.Index].Name : null);
    }

    /// <summary>
    /// Makes the table in the file what this type declares, without writing a row: what the first write of
    /// the type would do. Creates the table if it does not exist, registers a new version of its schema if
    /// the type has other columns than the last one written (the rows are not rewritten), and builds, builds
    /// again or drops its indexes and zone maps, and its constraints, which are checked against the rows the
    /// table has. Afterwards SQL sees the table, its columns and its indexes as this type declares them.
    /// </summary>
    /// <remarks>
    /// Meant for when the application starts, before SQL reads a table the application has not written yet.
    /// A table that is already as the type declares it is not changed.
    /// </remarks>
    /// <param name="transaction">The write transaction.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the table is part of the transaction as the type declares it.</returns>
    /// <exception cref="TkSchemaException">The rows the table has cannot be read with the columns of this type.</exception>
    /// <exception cref="TkConstraintException">A row the table has violates a constraint of this type. The transaction can only be rolled back.</exception>
    public async ValueTask EnsureAsync(TkWriteTransaction transaction, CancellationToken cancellationToken = default)
    {
        Check(transaction);
        await transaction.TableForWriteAsync(this, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Adds many rows at once, much faster than one by one: the table and its indexes are built from the
    /// bottom up, each page written once. Fastest when the rows arrive in key order; if they do not,
    /// they are sorted first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Meant for loading a table: an initial import, or restoring a backup. A table that already has rows
    /// is rewritten whole, merged with the new ones. Memory use does not grow with the number of rows:
    /// finished pages go to the file as the load advances, and are part of the database only at commit.
    /// </para>
    /// <para>
    /// The rows are added or none is: a duplicate key, a violated constraint or any other failure leaves
    /// the transaction such that it can only be rolled back.
    /// </para>
    /// </remarks>
    /// <param name="transaction">The write transaction.</param>
    /// <param name="rows">The rows. Read once, as the load advances.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The number of rows added.</returns>
    /// <exception cref="TkDuplicateKeyException">Two rows have the same key, or the table already has a row with the key of one of them.</exception>
    /// <exception cref="TkConstraintException">A row violates a constraint.</exception>
    /// <exception cref="NotSupportedException">The table has a FOREIGN KEY to itself.</exception>
    public ValueTask<long> BulkInsertAsync(
        TkWriteTransaction transaction, IAsyncEnumerable<T> rows, CancellationToken cancellationToken = default)
    {
        Check(transaction);
        ArgumentNullException.ThrowIfNull(rows);
        return BulkInsertCoreAsync(transaction, rows, null, cancellationToken);
    }

    /// <inheritdoc cref="BulkInsertAsync(TkWriteTransaction, IAsyncEnumerable{T}, CancellationToken)"/>
    public ValueTask<long> BulkInsertAsync(
        TkWriteTransaction transaction, IEnumerable<T> rows, CancellationToken cancellationToken = default)
    {
        Check(transaction);
        ArgumentNullException.ThrowIfNull(rows);
        return BulkInsertCoreAsync(transaction, null, rows, cancellationToken);
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
        KeyChanges? changes = null;
        try
        {
            var stored = buffer.AsMemory(0, TkKey.Write(key, _keyType, buffer.AsSpan(0, tree.MaxKeySize)));
            var state = await transaction.TableAsync(Name, TableKey, cancellationToken).ConfigureAwait(false);
            if (state.Schema is null)
            {
                return false;
            }

            // Whatever indexes the table has must be the ones this type can keep, even if it declares none.
            await transaction.BindIndexesAsync(this, state, cancellationToken).ConfigureAwait(false);
            if (_entryIndexes > 0)
            {
                var old = await FetchAsync(
                    transaction.Inner, state.Root, stored, transaction.CatalogRoot, transaction, cancellationToken)
                    .ConfigureAwait(false);
                if (old is null)
                {
                    return false;
                }

                changes = Changes(old, null, stored.Span);
            }

            await EnsureNotReferredAsync(transaction, state, stored, cancellationToken).ConfigureAwait(false);
            try
            {
                if (changes is not null)
                {
                    await ApplyAsync(transaction, state, changes, cancellationToken).ConfigureAwait(false);
                }

                var (removed, root) = await tree.DeleteAsync(transaction.Inner, state.Root, stored, cancellationToken)
                    .ConfigureAwait(false);
                if (removed)
                {
                    state.Root = root;
                    state.Changed = true;
                }

                return removed;
            }
            catch
            {
                transaction.Fail();
                throw;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            changes?.Dispose();
        }
    }

    private static T Decode(ReadOnlySpan<byte> row, ReadPlan plan)
    {
        var reader = new TkRowReader(row, plan.Stored, plan.Members);
        return T.Read(ref reader);
    }

    /// <summary>
    /// Writes the row into a buffer rented from the shared pool, which the caller returns. Most rows fit
    /// in the first one; a large row is written again into a larger one.
    /// </summary>
    private byte[] Encode(T row, byte version, int expectedSize, out int length)
    {
        for (var size = expectedSize; ; size = (int)Math.Min((long)size * 4, BTree.MaxValueSize))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                var writer = new TkRowWriter(buffer.AsSpan(0, Math.Min(buffer.Length, BTree.MaxValueSize)), version, T.Columns.Length);
                T.Write(ref writer, row);
                length = writer.Length;
                return buffer;
            }
            catch (TkRowTooLargeException) when (size < BTree.MaxValueSize)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            catch (TkRowTooLargeException)
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw new TkRowTooLargeException(
                    $"Table '{Name}': the row is larger than {BTree.MaxValueSize} bytes, the most a row may take.");
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }
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

        var tree = _database.Tree;
        var keyBuffer = ArrayPool<byte>.Shared.Rent(tree.MaxKeySize);
        byte[]? rowBuffer = null;
        KeyChanges? changes = null;
        try
        {
            var key = keyBuffer.AsMemory(0, T.WriteKey(row, keyBuffer.AsSpan(0, tree.MaxKeySize)));
            rowBuffer = Encode(row, state.Schema!.Version, tree.MaxEntrySize, out var rowLength);
            var exists = false;
            if (_entryIndexes > 0)
            {
                // The row being replaced says which index entries to take out.
                var old = await FetchAsync(
                    transaction.Inner, state.Root, key, transaction.CatalogRoot, transaction, cancellationToken)
                    .ConfigureAwait(false);
                exists = old is not null;
                if (exists || mode != WriteMode.Update)
                {
                    changes = Changes(mode == WriteMode.Insert ? null : old, row, key.Span);
                }
            }
            else if (mode != WriteMode.Upsert)
            {
                // Only whether it is there matters: with no room for the value, the read says found or not.
                exists = await BTree.GetAsync(transaction.Inner, state.Root, key, Memory<byte>.Empty, cancellationToken)
                    .ConfigureAwait(false) != BTree.NotFound;
            }

            if (mode == WriteMode.Update && !exists)
            {
                return false;
            }

            if (mode == WriteMode.Insert && exists)
            {
                throw new TkDuplicateKeyException($"Table '{Name}' already has a row with that key.");
            }

            await EnforceAsync(transaction, state, row, key, changes, cancellationToken).ConfigureAwait(false);
            try
            {
                if (changes is not null)
                {
                    await ApplyAsync(transaction, state, changes, cancellationToken).ConfigureAwait(false);
                }

                state.Root = await tree.PutAsync(
                    transaction.Inner, state.Root, key, rowBuffer.AsMemory(0, rowLength), cancellationToken)
                    .ConfigureAwait(false);
                if (_entryIndexes < _indexes.Length)
                {
                    NoteZones(state, row, key.Span);
                }
            }
            catch
            {
                // A row and its index entries are several trees: past this point they may not agree.
                transaction.Fail();
                throw;
            }

            state.Changed = true;
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(keyBuffer);
            changes?.Dispose();
            if (rowBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(rowBuffer);
            }
        }
    }

    /// <summary>Whether a row meets every condition. Each field is written, as far as it matters, and compared.</summary>
    private static bool Matches(T row, ReadOnlySpan<FilterTerm> terms, Span<byte> scratch)
    {
        foreach (var term in terms)
        {
            var writer = new TkKeyWriter(scratch);
            T.WriteField(ref writer, row, term.Field);
            if (!term.Range.Contains(writer.Written))
            {
                return false;
            }
        }

        return true;
    }

    private static KeyRange? Find(ReadOnlySpan<FilterTerm> terms, int field)
    {
        foreach (var term in terms)
        {
            if (term.Field == field)
            {
                return term.Range;
            }
        }

        return null;
    }

    /// <summary>The smallest key above every key that starts with <paramref name="prefix"/>, or nothing if there is none.</summary>
    private static byte[]? PrefixEnd(ReadOnlySpan<byte> prefix)
    {
        var end = prefix.TrimEnd((byte)0xFF).ToArray();
        if (end.Length == 0)
        {
            return null;
        }

        end[^1]++;
        return end;
    }

    /// <summary>
    /// The key of a row in an index: its indexed fields, then its primary key, which tells apart the rows
    /// with the same values. Returns -1 if the index is partial and the row is not in it.
    /// </summary>
    private int IndexKey(
        IndexDefinition index,
        T row,
        ReadOnlySpan<byte> primaryKey,
        Span<byte> destination,
        Span<byte> scratch,
        out int prefix,
        out bool hasNull)
    {
        prefix = 0;
        hasNull = false;
        if (!Matches(row, index.Where, scratch))
        {
            return -1;
        }

        var writer = new TkKeyWriter(destination);
        for (var i = 0; i < index.Fields.Length; i++)
        {
            var start = writer.Length;
            T.WriteField(ref writer, row, index.Fields[i]);

            // A nullable field starts with the mark of whether it has a value.
            hasNull |= index.Nullable[i] && writer.Length > start && destination[start] == 0;
        }

        prefix = writer.Length;
        writer.WriteRaw(primaryKey);
        return writer.IsTruncated
            ? throw new TkIndexKeyTooLargeException(
                $"Index '{index.Name}' of table '{Name}': the indexed values of the row and its key take more than {destination.Length} bytes, the most an index entry may.")
            : writer.Length;
    }

    /// <summary>
    /// The entries a write takes out of each index and puts into it, worked out before anything changes:
    /// a row that cannot be indexed is refused while the transaction is still intact.
    /// </summary>
    private KeyChanges Changes(T? old, T? replacement, ReadOnlySpan<byte> primaryKey)
    {
        var maxKey = _database.Tree.MaxKeySize;
        var changes = new KeyChanges(_indexes.Length, maxKey, _whereScratch);
        try
        {
            var scratch = changes.Scratch;
            for (var i = 0; i < _indexes.Length; i++)
            {
                if (_indexes[i].IsZoneMap)
                {
                    // A zone map has no entry per row.
                    changes.OldLength(i) = changes.NewLength(i) = -1;
                    continue;
                }

                changes.OldLength(i) = old is null
                    ? -1
                    : IndexKey(_indexes[i], old, primaryKey, changes.Old(i, maxKey).Span, scratch, out _, out _);
                changes.NewLength(i) = -1;
                if (replacement is not null)
                {
                    changes.NewLength(i) = IndexKey(
                        _indexes[i], replacement, primaryKey, changes.New(i, maxKey).Span, scratch, out var prefix, out changes.HasNull(i));
                    BinaryPrimitives.WriteUInt16LittleEndian(changes.Value(i).Span, (ushort)prefix);
                }
            }

            return changes;
        }
        catch
        {
            changes.Dispose();
            throw;
        }
    }

    private async ValueTask ApplyAsync(
        TkWriteTransaction transaction,
        TkWriteTransaction.TableState state,
        KeyChanges changes,
        CancellationToken cancellationToken)
    {
        var tree = _database.Tree;
        for (var i = 0; i < _indexes.Length; i++)
        {
            var entry = state.Bound[i];
            if (_indexes[i].IsZoneMap)
            {
                continue;
            }

            var oldLength = changes.OldLength(i);
            var newLength = changes.NewLength(i);
            if (oldLength >= 0 && newLength >= 0 && changes.Old(i, oldLength).Span.SequenceEqual(changes.New(i, newLength).Span))
            {
                continue;
            }

            if (oldLength >= 0)
            {
                var (removed, root) = await tree.DeleteAsync(transaction.Inner, entry.Root, changes.Old(i, oldLength), cancellationToken)
                    .ConfigureAwait(false);
                if (!removed)
                {
                    throw new InvalidDataException($"Index '{_indexes[i].Name}' of table '{Name}' lacks the entry of a row.");
                }

                entry.Root = root;
                entry.Dirty = true;
            }

            if (newLength >= 0)
            {
                entry.Root = await tree.PutAsync(
                    transaction.Inner, entry.Root, changes.New(i, newLength), changes.Value(i), cancellationToken)
                    .ConfigureAwait(false);
                entry.Dirty = true;
            }
        }
    }

    /// <summary>
    /// Builds the error of a violated constraint, and aborts the transaction: after a violation it can
    /// only be rolled back.
    /// </summary>
    private TkConstraintException Violation(
        TkWriteTransaction transaction, string kind, string constraint, ReadOnlySpan<byte> key, string why)
    {
        transaction.Fail();
        var text = TkKey.Format(key, _keyType);
        return new TkConstraintException(
            $"Table '{Name}': the row with key {text} violates {kind} constraint '{constraint}': {why}. The transaction is aborted.",
            Name,
            constraint,
            text);
    }

    /// <summary>Whether a unique index already has an entry with the indexed values of this key, for another row.</summary>
    private static async ValueTask<bool> TakenAsync(
        IPageSource source, PageNumber root, ReadOnlyMemory<byte> key, int prefix, CancellationToken cancellationToken)
    {
        // Being unique, the index has at most one entry with those values: the first one decides.
        var values = key[..prefix];
        var entries = BTree.RangeAsync(source, root, values, PrefixEnd(values.Span), cancellationToken);
        await foreach (var entry in entries.ConfigureAwait(false))
        {
            return !entry.Key.Span.SequenceEqual(key.Span);
        }

        return false;
    }

    /// <summary>
    /// Checks a row about to be written against every constraint, before anything changes: CHECK first,
    /// which needs nothing but the row; then FOREIGN KEY; then UNIQUE.
    /// </summary>
    private async ValueTask EnforceAsync(
        TkWriteTransaction transaction,
        TkWriteTransaction.TableState state,
        T row,
        ReadOnlyMemory<byte> key,
        KeyChanges? changes,
        CancellationToken cancellationToken)
    {
        var checks = _constraints.Checks;
        for (var i = 0; i < checks.Length; i++)
        {
            if (!T.Check(row, i))
            {
                throw Violation(transaction, "CHECK", checks[i].Name, key.Span, $"({checks[i].Text}) is false");
            }
        }

        var foreignKeys = _constraints.ForeignKeys;
        if (foreignKeys.Length > 0)
        {
            var maxKey = _database.Tree.MaxKeySize;
            var buffer = ArrayPool<byte>.Shared.Rent(maxKey);
            try
            {
                for (var i = 0; i < foreignKeys.Length; i++)
                {
                    var length = ForeignKey(row, i, buffer.AsSpan(0, maxKey));
                    var found = length == NoValue;
                    if (length >= 0)
                    {
                        // A row may refer to itself, also while it is being inserted.
                        var parent = await transaction.TableAsync(foreignKeys[i].Table, _parentKeys[i], cancellationToken)
                            .ConfigureAwait(false);
                        found = (ReferenceEquals(parent, state) && buffer.AsSpan(0, length).SequenceEqual(key.Span))
                            || await BTree.GetAsync(transaction.Inner, parent.Root, buffer.AsMemory(0, length), Memory<byte>.Empty, cancellationToken)
                                .ConfigureAwait(false) != BTree.NotFound;
                    }

                    if (!found)
                    {
                        throw Violation(
                            transaction, "FOREIGN KEY", foreignKeys[i].Name, key.Span, $"no row of '{foreignKeys[i].Table}' has the key it refers to");
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        if (changes is null)
        {
            return;
        }

        for (var i = 0; i < _indexes.Length; i++)
        {
            var length = changes.NewLength(i);
            if (_indexes[i].IsUnique
                && length >= 0
                && !changes.HasNull(i)
                && await TakenAsync(
                    transaction.Inner,
                    state.Bound[i].Root,
                    changes.New(i, length),
                    BinaryPrimitives.ReadUInt16LittleEndian(changes.Value(i).Span),
                    cancellationToken).ConfigureAwait(false))
            {
                throw Violation(transaction, "UNIQUE", _indexes[i].Name, key.Span, "another row already has the same values");
            }
        }
    }

    /// <summary>
    /// The key a row refers to through a FOREIGN KEY, as the other table stores it: its length,
    /// <see cref="NoValue"/> if the column has none, or <see cref="NoSuchKey"/> if no key can be that value.
    /// </summary>
    private static int ForeignKey(T row, int foreignKey, Span<byte> destination)
    {
        try
        {
            return T.WriteForeignKey(row, foreignKey, destination) is var length and >= 0 ? length : NoValue;
        }
        catch (ArgumentException)
        {
            // Too long to be a key, or empty: nothing in the other table can have it.
            return NoSuchKey;
        }
    }

    /// <summary>Refuses to delete a row while rows of this or another table refer to it through a FOREIGN KEY.</summary>
    private async ValueTask EnsureNotReferredAsync(
        TkWriteTransaction transaction, TkWriteTransaction.TableState state, ReadOnlyMemory<byte> key, CancellationToken cancellationToken)
    {
        var referrers = await transaction.ReferrersAsync(state, cancellationToken).ConfigureAwait(false);
        if (referrers.Count == 0)
        {
            return;
        }

        var maxKey = _database.Tree.MaxKeySize;
        var buffer = ArrayPool<byte>.Shared.Rent(maxKey);
        try
        {
            foreach (var referrer in referrers)
            {
                var referring = await transaction.TableAsync(referrer.Table, CatalogStore.TableKey(referrer.Table), cancellationToken)
                    .ConfigureAwait(false);
                var indexes = await transaction.IndexesAsync(referring, cancellationToken).ConfigureAwait(false);
                if (!indexes.TryGetValue(referrer.Index, out var index))
                {
                    throw new InvalidDataException(
                        $"Table '{referrer.Table}' lacks the index of its FOREIGN KEY '{referrer.Name}' to table '{Name}'.");
                }

                // The entries of the rows that refer to this one start with its key, as an index holds it.
                var probe = buffer.AsMemory(0, ReferenceProbe(key.Span, referrer.IsNullable, buffer.AsSpan(0, maxKey)));
                var entries = BTree.RangeAsync(transaction.Inner, index.Root, probe, PrefixEnd(probe.Span), cancellationToken);
                await foreach (var entry in entries.ConfigureAwait(false))
                {
                    // A row that refers to itself does not keep itself from being deleted.
                    if (ReferenceEquals(referring, state) && IsEntryOf(entry, key.Span))
                    {
                        continue;
                    }

                    transaction.Fail();
                    var text = TkKey.Format(key.Span, _keyType);
                    throw new TkConstraintException(
                        $"Table '{referrer.Table}': FOREIGN KEY constraint '{referrer.Name}' is violated: the row of '{Name}' with key {text} cannot be deleted while rows of '{referrer.Table}' refer to it. The transaction is aborted.",
                        referrer.Table,
                        referrer.Name,
                        text);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>A stored key of this table as the first field of an index of a table that refers to it.</summary>
    private int ReferenceProbe(ReadOnlySpan<byte> key, bool isNullable, Span<byte> destination)
    {
        var writer = new TkKeyWriter(destination);
        if (isNullable)
        {
            writer.WritePresent();
        }

        if (_keyType == TkColumnType.Text)
        {
            // In an index, text has its zeros escaped and an end mark; in the table it is the whole key.
            writer.Write(key);
        }
        else
        {
            writer.WriteRaw(key);
        }

        return writer.IsTruncated
            ? throw new InvalidDataException($"Table '{Name}': a stored key is too long to be looked up in an index.")
            : writer.Length;
    }

    private static bool IsEntryOf(BTreeEntry entry, ReadOnlySpan<byte> primaryKey) =>
        entry.Value.Length == sizeof(ushort)
        && BinaryPrimitives.ReadUInt16LittleEndian(entry.Value.Span) is var prefix
        && prefix <= entry.Key.Length
        && entry.Key.Span[prefix..].SequenceEqual(primaryKey);

    /// <summary>
    /// Makes the CHECK and FOREIGN KEY constraints of the table those the type declares. The ones that
    /// are new are checked against the rows the table already has, as adding a constraint does.
    /// </summary>
    internal async ValueTask BindConstraintsAsync(
        TkWriteTransaction transaction, TkWriteTransaction.TableState state, CancellationToken cancellationToken)
    {
        var stored = await transaction.ConstraintsAsync(state, cancellationToken).ConfigureAwait(false);
        if (stored.AsSpan().SequenceEqual(_constraints.Encoded))
        {
            return;
        }

        var old = ConstraintSet.Decode(stored);
        var added = _constraints.Checks.Any(check => !old.Checks.Contains(check))
            || _constraints.ForeignKeys.Any(key => !old.ForeignKeys.Any(
                known => string.Equals(known.Name, key.Name, StringComparison.Ordinal) && string.Equals(known.Table, key.Table, StringComparison.Ordinal)));
        if (added && !state.Root.IsNone)
        {
            var maxKey = _database.Tree.MaxKeySize;
            var key = ArrayPool<byte>.Shared.Rent(maxKey);
            try
            {
                var rows = BTree.RangeAsync(transaction.Inner, state.Root, default, default, cancellationToken);
                await foreach (var entry in rows.ConfigureAwait(false))
                {
                    // The entry points into its page: what the checks need is taken before reading anything else.
                    var length = entry.Key.Length;
                    entry.Key.Span.CopyTo(key);
                    var plan = await PlanAsync(entry.Value, transaction.Inner, transaction.CatalogRoot, transaction, cancellationToken)
                        .ConfigureAwait(false);
                    var row = Decode(entry.Value.Span, plan);
                    await EnforceAsync(transaction, state, row, key.AsMemory(0, length), null, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(key);
            }
        }

        // The tables referred to must know who to ask before deleting a row.
        var parents = old.ForeignKeys.Select(static key => key.Table)
            .Concat(_constraints.ForeignKeys.Select(static key => key.Table))
            .Distinct(StringComparer.Ordinal);
        foreach (var parent in parents)
        {
            var foreignKeys = _constraints.ForeignKeys
                .Where(key => string.Equals(key.Table, parent, StringComparison.Ordinal))
                .Select(key => new Referrer(Name, key.Name, key.Index, key.IsNullable));
            await transaction.SetReferrersAsync(parent, Name, foreignKeys, cancellationToken).ConfigureAwait(false);
        }

        await transaction.SetConstraintsAsync(state, _constraints.Encoded, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<long> BulkInsertCoreAsync(
        TkWriteTransaction transaction, IAsyncEnumerable<T>? asyncRows, IEnumerable<T>? rows, CancellationToken cancellationToken)
    {
        var state = await transaction.TableForWriteAsync(this, cancellationToken).ConfigureAwait(false);
        if (_constraints.ForeignKeys.Any(key => string.Equals(key.Table, Name, StringComparison.Ordinal)))
        {
            throw new NotSupportedException(
                $"Table '{Name}' has a FOREIGN KEY to itself: a row may refer to one that comes later in the load, so its rows cannot be loaded in bulk.");
        }

        var inner = transaction.Inner;
        var checksRows = _constraints.Checks.Length > 0 || _constraints.ForeignKeys.Length > 0;
        try
        {
            // From here on pages of the transaction go to the file while others are being read: whatever
            // it has changed so far goes first, so that no page still in memory is ever being read.
            await inner.SpillAsync(cancellationToken).ConfigureAwait(false);
            var load = new BulkLoad(this, inner, state);

            // Each row is taken in without waiting for anything; only now and then is there something to
            // write, and only then does the load wait.
            if (rows is not null)
            {
                foreach (var row in rows)
                {
                    if (checksRows)
                    {
                        await CheckBulkRowAsync(transaction, state, row, cancellationToken).ConfigureAwait(false);
                    }

                    if (load.Add(row))
                    {
                        await load.WriteAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                await foreach (var row in asyncRows!.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    if (checksRows)
                    {
                        await CheckBulkRowAsync(transaction, state, row, cancellationToken).ConfigureAwait(false);
                    }

                    if (load.Add(row))
                    {
                        await load.WriteAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (load.Count == 0)
            {
                return 0;
            }

            if (load.Builder is not null)
            {
                state.Root = load.Builder.Complete();
            }
            else
            {
                state.Root = await MergeRowsAsync(inner, load.Existing, load.Sorter!, cancellationToken).ConfigureAwait(false);
                await BTree.DropAsync(inner, load.Existing, cancellationToken).ConfigureAwait(false);
            }

            state.Changed = true;
            for (var i = 0; i < _indexes.Length; i++)
            {
                var index = state.Bound[i];
                if (load.Zones[i] is { } zone)
                {
                    await WidenZonesAsync(inner, index, zone, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var old = index.Root;
                index.Root = await MergeEntriesAsync(transaction, _indexes[i], old, load.Entries[i], cancellationToken).ConfigureAwait(false);
                index.Dirty = true;
                await BTree.DropAsync(inner, old, cancellationToken).ConfigureAwait(false);
            }

            return load.Count;
        }
        catch
        {
            // Trees half built and pages already in the file: nothing of this transaction can be kept.
            transaction.Fail();
            throw;
        }
    }

    /// <summary>The constraints that depend on nothing but the row and the tables it refers to, for a row of a bulk load.</summary>
    private async ValueTask CheckBulkRowAsync(
        TkWriteTransaction transaction, TkWriteTransaction.TableState state, T row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        var maxKey = _database.Tree.MaxKeySize;
        var key = ArrayPool<byte>.Shared.Rent(maxKey);
        try
        {
            var length = T.WriteKey(row, key.AsSpan(0, maxKey));
            await EnforceAsync(transaction, state, row, key.AsMemory(0, length), null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(key);
        }
    }

    private TkDuplicateKeyException Duplicate(ReadOnlySpan<byte> key) =>
        new($"Table '{Name}': more than one row has the key {TkKey.Format(key, _keyType)}.");

    /// <summary>Builds the table anew from the rows it has and the sorted new ones, which must not repeat a key.</summary>
    private async ValueTask<PageNumber> MergeRowsAsync(
        WriteTransaction inner, PageNumber existing, RunSorter sorter, CancellationToken cancellationToken)
    {
        var builder = new BTreeBuilder(_database.Tree, inner, _database.BulkSpillPages);
        var old = BTree.RangeAsync(inner, existing, default, default, cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using (old.ConfigureAwait(false))
        {
            var hasOld = await old.MoveNextAsync().ConfigureAwait(false);
            var hasNew = await sorter.NextAsync(cancellationToken).ConfigureAwait(false);
            while (hasOld || hasNew)
            {
                var order = !hasOld ? 1 : !hasNew ? -1 : old.Current.Key.Span.SequenceCompareTo(sorter.Key);
                if (order == 0 || (order > 0 && builder.Count > 0 && sorter.Key.SequenceEqual(builder.LastKey)))
                {
                    throw Duplicate(sorter.Key);
                }

                if (order < 0)
                {
                    builder.Add(old.Current.Key.Span, old.Current.Value.Span);
                    hasOld = await old.MoveNextAsync().ConfigureAwait(false);
                }
                else
                {
                    builder.Add(sorter.Key, sorter.Value);
                    hasNew = await sorter.NextAsync(cancellationToken).ConfigureAwait(false);
                }

                if (builder.ShouldSpill)
                {
                    await builder.SpillAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return builder.Complete();
    }

    /// <summary>
    /// Builds an index anew from the entries it has and the sorted new ones. In a unique index two
    /// entries next to each other with the same values are a violation, unless they have a field with no value.
    /// </summary>
    private async ValueTask<PageNumber> MergeEntriesAsync(
        TkWriteTransaction transaction, IndexDefinition index, PageNumber existing, RunSorter sorter, CancellationToken cancellationToken)
    {
        var inner = transaction.Inner;
        var builder = new BTreeBuilder(_database.Tree, inner, _database.BulkSpillPages);
        var previousPrefix = -1;
        var previousExempt = false;
        var value = new byte[sizeof(ushort)];
        var old = BTree.RangeAsync(inner, existing, default, default, cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using (old.ConfigureAwait(false))
        {
            var hasOld = await old.MoveNextAsync().ConfigureAwait(false);
            var hasNew = await sorter.NextAsync(cancellationToken).ConfigureAwait(false);
            while (hasOld || hasNew)
            {
                var isNew = !hasOld || (hasNew && old.Current.Key.Span.SequenceCompareTo(sorter.Key) > 0);
                var key = isNew ? sorter.Key : old.Current.Key.Span;
                var mark = isNew ? sorter.Value : old.Current.Value.Span;
                var prefix = BinaryPrimitives.ReadUInt16LittleEndian(mark);

                // An entry already in the index says nothing of its fields. If it collides with a new one,
                // that one says it for both: they have the same values.
                var exempt = isNew ? mark[sizeof(ushort)] != 0 : (bool?)null;
                if (index.IsUnique
                    && prefix == previousPrefix
                    && builder.LastKey[..previousPrefix].SequenceEqual(key[..prefix])
                    && !(exempt ?? previousExempt))
                {
                    throw Violation(transaction, "UNIQUE", index.Name, key[prefix..], "another row already has the same values");
                }

                (previousPrefix, previousExempt) = (prefix, exempt ?? previousExempt);
                mark[..sizeof(ushort)].CopyTo(value);
                builder.Add(key, value);
                if (isNew)
                {
                    hasNew = await sorter.NextAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    hasOld = await old.MoveNextAsync().ConfigureAwait(false);
                }

                if (builder.ShouldSpill)
                {
                    await builder.SpillAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return builder.Complete();
    }

    /// <summary>
    /// What one commit did to the rows of the table: the difference between the table in the catalog
    /// before it and in the catalog after it. The caller keeps both states readable.
    /// </summary>
    internal async ValueTask CollectChangesAsync(
        PageNumber catalogBefore, PageNumber catalogAfter, ulong transaction, List<TkChange<T>> changes, CancellationToken cancellationToken)
    {
        var pager = _database.Pager;
        var before = (await CatalogStore.FindTableAsync(pager, catalogBefore, TableKey, cancellationToken).ConfigureAwait(false))?.Root
            ?? PageNumber.None;
        var after = (await CatalogStore.FindTableAsync(pager, catalogAfter, TableKey, cancellationToken).ConfigureAwait(false))?.Root
            ?? PageNumber.None;
        await foreach (var change in BTree.DiffAsync(pager, before, after, cancellationToken).ConfigureAwait(false))
        {
            // Schemas are only ever added, so the later catalog knows how to read the rows of both states.
            T? old = null;
            T? current = null;
            if (change.HasBefore)
            {
                var plan = await PlanAsync(change.Before, pager, catalogAfter, null, cancellationToken).ConfigureAwait(false);
                old = Decode(change.Before.Span, plan);
            }

            if (change.HasAfter)
            {
                var plan = await PlanAsync(change.After, pager, catalogAfter, null, cancellationToken).ConfigureAwait(false);
                current = Decode(change.After.Span, plan);
            }

            var kind = old is null ? TkChangeKind.Insert : current is null ? TkChangeKind.Delete : TkChangeKind.Update;
            changes.Add(new TkChange<T>(kind, old, current, transaction));
        }
    }

    /// <summary>
    /// Remembers, for every zone map, that the window of this row holds its key. It stays in memory until
    /// the commit, or until something needs to read the zone map: a transaction that writes a thousand
    /// rows of the same hour writes that hour once.
    /// </summary>
    private void NoteZones(TkWriteTransaction.TableState state, T row, ReadOnlySpan<byte> primaryKey)
    {
        var pending = state.PendingZones;
        if (pending is null)
        {
            state.PendingZones = pending = new ZoneWindows?[_indexes.Length];
            state.FlushZones = (transaction, cancellationToken) => FlushZonesAsync(transaction, state, cancellationToken);
        }

        for (var i = 0; i < _indexes.Length; i++)
        {
            if (_indexes[i].IsZoneMap && TryWindow(_indexes[i], row, out var window))
            {
                (pending[i] ??= new ZoneWindows()).Add(window, primaryKey);
            }
        }
    }

    /// <summary>Writes to the zone maps what the transaction has learnt about them.</summary>
    private async ValueTask FlushZonesAsync(TkWriteTransaction transaction, TkWriteTransaction.TableState state, CancellationToken cancellationToken)
    {
        var pending = state.PendingZones;
        state.PendingZones = null;
        state.FlushZones = null;
        if (pending is null)
        {
            return;
        }

        try
        {
            for (var i = 0; i < pending.Length; i++)
            {
                if (pending[i] is { } windows)
                {
                    await WidenZonesAsync(transaction.Inner, state.Bound[i], windows, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            transaction.Fail();
            throw;
        }
    }

    /// <summary>The window of a zone map the date of a row falls in. False if the row has no date.</summary>
    private static bool TryWindow(IndexDefinition zone, T row, out long window)
    {
        Span<byte> encoded = stackalloc byte[16];
        var writer = new TkKeyWriter(encoded);
        T.WriteField(ref writer, row, zone.Fields[0]);
        return TryWindow(zone, writer.Written, out window);
    }

    /// <summary>
    /// The window a date falls in, from the date as a key holds it. False if those bytes are not a date:
    /// no value, or one of the marks that bound the values of a nullable field.
    /// </summary>
    private static bool TryWindow(IndexDefinition zone, ReadOnlySpan<byte> encoded, out long window)
    {
        window = 0;
        if (zone.Nullable[0])
        {
            if (encoded.IsEmpty || encoded[0] != 1)
            {
                return false;
            }

            encoded = encoded[1..];
        }

        long value;
        if (zone.ZoneInDays)
        {
            if (encoded.Length != sizeof(int))
            {
                return false;
            }

            value = (int)(BinaryPrimitives.ReadUInt32BigEndian(encoded) ^ 0x8000_0000);
        }
        else
        {
            if (encoded.Length != sizeof(long))
            {
                return false;
            }

            value = (long)(BinaryPrimitives.ReadUInt64BigEndian(encoded) ^ 0x8000_0000_0000_0000);
        }

        window = Math.DivRem(value, zone.ZoneWidth, out var rest);
        if (rest < 0)
        {
            window--;
        }

        return true;
    }

    /// <summary>The key of a window in the tree of a zone map: it sorts like the window.</summary>
    private static void ZoneKey(long window, Span<byte> destination) =>
        BinaryPrimitives.WriteUInt64BigEndian(destination, (ulong)window ^ 0x8000_0000_0000_0000);

    /// <summary>
    /// Makes a window of a zone map take in a range of keys: the lowest and highest key of the rows whose
    /// date falls in the window only ever move outwards.
    /// </summary>
    private async ValueTask WidenZoneAsync(
        WriteTransaction inner,
        TkWriteTransaction.IndexEntry entry,
        long window,
        ReadOnlyMemory<byte> lowest,
        ReadOnlyMemory<byte> highest,
        CancellationToken cancellationToken)
    {
        var tree = _database.Tree;
        var room = sizeof(ushort) + (2 * tree.MaxKeySize);
        var buffer = ArrayPool<byte>.Shared.Rent(sizeof(long) + (2 * room));
        try
        {
            var key = buffer.AsMemory(0, sizeof(long));
            ZoneKey(window, key.Span);
            var stored = buffer.AsMemory(sizeof(long), room);
            var length = await BTree.GetAsync(inner, entry.Root, key, stored, cancellationToken).ConfigureAwait(false);
            if (length != BTree.NotFound)
            {
                var (low, high) = ZoneRange(stored.Span[..length]);
                var below = lowest.Span.SequenceCompareTo(low) < 0;
                var above = highest.Span.SequenceCompareTo(high) > 0;
                if (!below && !above)
                {
                    return;
                }

                lowest = below ? lowest : low.ToArray();
                highest = above ? highest : high.ToArray();
            }

            var widened = buffer.AsMemory(sizeof(long) + room, sizeof(ushort) + lowest.Length + highest.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(widened.Span, (ushort)lowest.Length);
            lowest.Span.CopyTo(widened.Span[sizeof(ushort)..]);
            highest.Span.CopyTo(widened.Span[(sizeof(ushort) + lowest.Length)..]);
            entry.Root = await tree.PutAsync(inner, entry.Root, key, widened, cancellationToken).ConfigureAwait(false);
            entry.Dirty = true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>The lowest and highest key a window of a zone map holds, from its entry.</summary>
    private ReadOnlySpan<byte> ZoneRange(ReadOnlySpan<byte> entry, out ReadOnlySpan<byte> high)
    {
        if (entry.Length < sizeof(ushort) || BinaryPrimitives.ReadUInt16LittleEndian(entry) is var length && length > entry.Length - sizeof(ushort))
        {
            throw new InvalidDataException($"A zone map of table '{Name}' has a damaged entry.");
        }

        high = entry[(sizeof(ushort) + length)..];
        return entry.Slice(sizeof(ushort), length);
    }

    private (byte[] Low, byte[] High) ZoneRange(ReadOnlySpan<byte> entry)
    {
        var low = ZoneRange(entry, out var high);
        return (low.ToArray(), high.ToArray());
    }

    /// <summary>Writes into a zone map the windows a build or a bulk load worked out in memory.</summary>
    private async ValueTask WidenZonesAsync(
        WriteTransaction inner, TkWriteTransaction.IndexEntry entry, ZoneWindows windows, CancellationToken cancellationToken)
    {
        foreach (var (window, range) in windows.Ranges)
        {
            await WidenZoneAsync(inner, entry, window, range.Low, range.High, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Fills an index that did not exist with the rows the table already has.</summary>
    internal async ValueTask BuildIndexAsync(
        TkWriteTransaction transaction,
        TkWriteTransaction.TableState state,
        IndexDefinition index,
        TkWriteTransaction.IndexEntry entry,
        CancellationToken cancellationToken)
    {
        if (state.Root.IsNone)
        {
            return;
        }

        if (index.IsZoneMap)
        {
            // Few windows, many rows: they are worked out in memory and written at the end.
            var windows = new ZoneWindows();
            var all = BTree.RangeAsync(transaction.Inner, state.Root, default, default, cancellationToken);
            await foreach (var row in all.ConfigureAwait(false))
            {
                var plan = await PlanAsync(row.Value, transaction.Inner, transaction.CatalogRoot, transaction, cancellationToken)
                    .ConfigureAwait(false);
                if (TryWindow(index, Decode(row.Value.Span, plan), out var window))
                {
                    windows.Add(window, row.Key.Span);
                }
            }

            await WidenZonesAsync(transaction.Inner, entry, windows, cancellationToken).ConfigureAwait(false);
            return;
        }

        var tree = _database.Tree;
        var maxKey = tree.MaxKeySize;
        var scratchSize = IndexDefinition.ScratchSize(index.Where);
        var buffer = ArrayPool<byte>.Shared.Rent(maxKey + sizeof(ushort) + scratchSize);
        try
        {
            // The scan reads the tree of the rows and the writes go to another: neither moves under the other.
            var rows = BTree.RangeAsync(transaction.Inner, state.Root, default, default, cancellationToken);
            await foreach (var row in rows.ConfigureAwait(false))
            {
                var plan = await PlanAsync(row.Value, transaction.Inner, transaction.CatalogRoot, transaction, cancellationToken)
                    .ConfigureAwait(false);
                var length = IndexKey(
                    index,
                    Decode(row.Value.Span, plan),
                    row.Key.Span,
                    buffer.AsSpan(0, maxKey),
                    buffer.AsSpan(maxKey + sizeof(ushort), scratchSize),
                    out var prefix,
                    out var hasNull);
                if (length >= 0)
                {
                    if (index.IsUnique
                        && !hasNull
                        && await TakenAsync(transaction.Inner, entry.Root, buffer.AsMemory(0, length), prefix, cancellationToken).ConfigureAwait(false))
                    {
                        throw Violation(
                            transaction, "UNIQUE", index.Name, buffer.AsSpan(prefix, length - prefix), "another row already has the same values");
                    }

                    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(maxKey), (ushort)prefix);
                    entry.Root = await tree.PutAsync(
                        transaction.Inner, entry.Root, buffer.AsMemory(0, length), buffer.AsMemory(maxKey, sizeof(ushort)), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Reads and decodes the row with this stored key, whatever its size.</summary>
    private async ValueTask<T?> FetchAsync(
        IPageSource source,
        PageNumber root,
        ReadOnlyMemory<byte> key,
        PageNumber catalogRoot,
        TkWriteTransaction? writer,
        CancellationToken cancellationToken)
    {
        var (buffer, length) = await BTree.GetRentedAsync(source, root, key, _database.Tree.MaxEntrySize, cancellationToken)
            .ConfigureAwait(false);
        if (buffer is null)
        {
            return null;
        }

        try
        {
            var row = buffer.AsMemory(0, length);
            var plan = await PlanAsync(row, source, catalogRoot, writer, cancellationToken).ConfigureAwait(false);
            return Decode(row.Span, plan);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async IAsyncEnumerable<T> QueryLatestAsync(
        TkFilter<T> filter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var snapshot = _database.Pager.BeginRead();
        var rows = QueryCoreAsync(
            _database.Pager, snapshot.Root, snapshot.Snapshot.TransactionId.Value, null, filter, cancellationToken);
        await foreach (var row in rows.ConfigureAwait(false))
        {
            yield return row;
        }
    }

    private async IAsyncEnumerable<T> QueryCoreAsync(
        IPageSource source,
        PageNumber catalogRoot,
        ulong snapshotId,
        TkWriteTransaction? writer,
        TkFilter<T> filter,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var terms = filter.Normalize();
        var root = await RootAsync(source, catalogRoot, snapshotId, writer, cancellationToken).ConfigureAwait(false);
        if (root.IsNone)
        {
            yield break;
        }

        if (writer is not null && _entryIndexes < _indexes.Length)
        {
            // What the transaction has learnt about its zone maps is in memory: a query needs it in them.
            await writer.FlushZonesAsync(Name, cancellationToken).ConfigureAwait(false);
        }

        var states = await IndexStatesAsync(source, catalogRoot, snapshotId, writer, cancellationToken).ConfigureAwait(false);
        var choice = Choose(terms, states);
        if (choice.Access == TkAccess.Nothing)
        {
            yield break;
        }

        // However the rows are reached, every condition is checked on each: the plan only narrows the search.
        var scratch = ArrayPool<byte>.Shared.Rent(IndexDefinition.ScratchSize(terms));
        byte[]? primaryKey = null;
        try
        {
            if (choice.Access == TkAccess.KeyLookup)
            {
                var row = await FetchAsync(source, root, choice.From, catalogRoot, writer, cancellationToken).ConfigureAwait(false);
                if (row is not null && Matches(row, terms, scratch))
                {
                    yield return row;
                }
            }
            else if (choice.Access == TkAccess.ZoneScan)
            {
                // The stretches of keys the windows hold, joined where they touch, and each read in key order.
                var stretches = new List<(byte[] Low, byte[] High)>();
                var entries = BTree.RangeAsync(source, states[choice.Index].Root, choice.From, choice.To, cancellationToken);
                await foreach (var entry in entries.ConfigureAwait(false))
                {
                    stretches.Add(ZoneRange(entry.Value.Span));
                }

                stretches.Sort(static (a, b) => a.Low.AsSpan().SequenceCompareTo(b.Low));
                for (var i = 0; i < stretches.Count;)
                {
                    var (low, high) = stretches[i++];
                    while (i < stretches.Count && stretches[i].Low.AsSpan().SequenceCompareTo(high) <= 0)
                    {
                        high = stretches[i].High.AsSpan().SequenceCompareTo(high) > 0 ? stretches[i].High : high;
                        i++;
                    }

                    byte[] past = [.. high, 0];
                    var rows = BTree.RangeAsync(source, root, low, past, cancellationToken);
                    await foreach (var entry in rows.ConfigureAwait(false))
                    {
                        var plan = await PlanAsync(entry.Value, source, catalogRoot, writer, cancellationToken).ConfigureAwait(false);
                        var row = Decode(entry.Value.Span, plan);
                        if (Matches(row, terms, scratch))
                        {
                            yield return row;
                        }
                    }
                }
            }
            else if (choice.Access is TkAccess.IndexScan or TkAccess.UniqueLookup)
            {
                primaryKey = ArrayPool<byte>.Shared.Rent(_database.Tree.MaxKeySize);
                var entries = BTree.RangeAsync(source, states[choice.Index].Root, choice.From, choice.To, cancellationToken);
                await foreach (var entry in entries.ConfigureAwait(false))
                {
                    // The entry points into its page: the key of the row is copied before reading anything else.
                    var length = CopyPrimaryKey(choice.Index, entry, primaryKey);
                    var row = await FetchAsync(source, root, primaryKey.AsMemory(0, length), catalogRoot, writer, cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new InvalidDataException(
                            $"Index '{_indexes[choice.Index].Name}' of table '{Name}' has an entry for a row that does not exist.");
                    if (Matches(row, terms, scratch))
                    {
                        yield return row;
                    }

                    if (choice.Access == TkAccess.UniqueLookup)
                    {
                        // The constraint says there is no other.
                        break;
                    }
                }
            }
            else
            {
                var entries = BTree.RangeAsync(source, root, choice.From, choice.To, cancellationToken);
                await foreach (var entry in entries.ConfigureAwait(false))
                {
                    var plan = await PlanAsync(entry.Value, source, catalogRoot, writer, cancellationToken).ConfigureAwait(false);
                    var row = Decode(entry.Value.Span, plan);
                    if (Matches(row, terms, scratch))
                    {
                        yield return row;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
            if (primaryKey is not null)
            {
                ArrayPool<byte>.Shared.Return(primaryKey);
            }
        }
    }

    /// <summary>The key of the row an index entry is for: what follows the indexed values, whose length the entry holds.</summary>
    private int CopyPrimaryKey(int index, BTreeEntry entry, Span<byte> destination)
    {
        var key = entry.Key.Span;
        if (entry.Value.Length != sizeof(ushort)
            || BinaryPrimitives.ReadUInt16LittleEndian(entry.Value.Span) is var prefix && prefix >= key.Length)
        {
            throw new InvalidDataException($"Index '{_indexes[index].Name}' of table '{Name}' has a damaged entry.");
        }

        key[prefix..].CopyTo(destination);
        return key.Length - prefix;
    }

    /// <summary>
    /// Which indexes a reader of this state can use: those the catalog has exactly as the type declares
    /// them. The others would give the wrong rows, so they are left alone.
    /// </summary>
    private async ValueTask<IndexState[]> IndexStatesAsync(
        IPageSource source, PageNumber catalogRoot, ulong snapshotId, TkWriteTransaction? writer, CancellationToken cancellationToken)
    {
        if (_indexes.Length == 0)
        {
            return [];
        }

        var states = new IndexState[_indexes.Length];
        if (writer is not null)
        {
            var state = await writer.TableAsync(Name, TableKey, cancellationToken).ConfigureAwait(false);
            var known = await writer.IndexesAsync(state, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < states.Length; i++)
            {
                if (known.TryGetValue(_indexes[i].Name, out var entry) && entry.Definition.AsSpan().SequenceEqual(_indexes[i].Encoded))
                {
                    states[i] = new IndexState(true, entry.Root);
                }
            }

            return states;
        }

        var latest = Volatile.Read(ref _latest);
        if (latest is not null && latest.SnapshotId == snapshotId && Volatile.Read(ref latest.Indexes) is { } remembered)
        {
            return remembered;
        }

        for (var i = 0; i < states.Length; i++)
        {
            var (found, root) = await CatalogStore.FindIndexAsync(
                source, catalogRoot, _indexes[i].CatalogKey, _indexes[i].Encoded, cancellationToken)
                .ConfigureAwait(false);
            states[i] = new IndexState(found, root);
        }

        if (latest is not null && latest.SnapshotId == snapshotId)
        {
            Volatile.Write(ref latest.Indexes, states);
        }

        return states;
    }

    /// <summary>
    /// Chooses how to reach the rows of a filter, by rule: the exact key first; then the index with the
    /// most leading fields fixed to one value; then a range of the key; then an index with a range on
    /// its first field, or a partial index, which is smaller than the table; and failing all, the table.
    /// </summary>
    /// <remarks>
    /// A partial index is considered only when every one of its conditions is on a field the filter
    /// also restricts, to a range inside the range of the index: that is proof that every row the
    /// filter accepts is in the index. Without that proof it is ignored.
    /// </remarks>
    private Choice Choose(FilterTerm[] terms, IndexState[] states)
    {
        KeyRange? key = null;
        foreach (var term in terms)
        {
            if (term.Range.IsEmpty)
            {
                return new Choice(TkAccess.Nothing, -1, default, default);
            }

            if (term.Field == T.KeyColumn)
            {
                key = term.Range;
            }
        }

        if (key is { IsPoint: true })
        {
            var stored = ToPrimaryKey(key.Low!);
            return new Choice(stored.Length == 0 ? TkAccess.Nothing : TkAccess.KeyLookup, -1, stored, default);
        }

        var best = -1;
        var bestScore = 0;
        var bestFixed = 0;
        for (var i = 0; i < _indexes.Length; i++)
        {
            var index = _indexes[i];
            if (!states[i].Usable || index.IsZoneMap || !Implies(terms, index.Where))
            {
                continue;
            }

            var pinned = 0;
            while (pinned < index.Fields.Length && Find(terms, index.Fields[pinned]) is { IsPoint: true })
            {
                pinned++;
            }

            if (index.IsUnique && pinned == index.Fields.Length && !PinsNoValue(index, terms))
            {
                // Every field of a unique index fixed to a value: one row at most, and nothing beats that.
                return IndexRange(i, pinned, terms) with { Access = TkAccess.UniqueLookup };
            }

            var ranged = pinned < index.Fields.Length && Find(terms, index.Fields[pinned]) is not null;
            var score = (pinned * 4) + (ranged ? 2 : 0) + (index.Where.Length > 0 ? 1 : 0);
            if (score > bestScore)
            {
                (best, bestScore, bestFixed) = (i, score, pinned);
            }
        }

        if (best >= 0 && bestFixed > 0)
        {
            return IndexRange(best, bestFixed, terms);
        }

        if (key is null)
        {
            // A zone map reads whole stretches of the table, in order: for a range of several windows that
            // beats going to the table once per index entry. For less than that, an index finds exactly its rows.
            var zone = ZoneRange(terms, states, out var windows, out var several);
            return zone >= 0 && (best < 0 || several)
                ? new Choice(TkAccess.ZoneScan, zone, windows.From, windows.To)
                : best >= 0
                    ? IndexRange(best, bestFixed, terms)
                    : new Choice(TkAccess.TableScan, -1, default, default);
        }

        // The stored key sorts like the encoded one, so the bounds carry over; one byte more steps past a key.
        ReadOnlyMemory<byte> from = default;
        ReadOnlyMemory<byte> to = default;
        if (key.Low is not null)
        {
            from = key.LowInclusive ? ToPrimaryKey(key.Low) : [.. ToPrimaryKey(key.Low), 0];
        }

        if (key.High is not null)
        {
            to = key.HighInclusive ? [.. ToPrimaryKey(key.High), 0] : ToPrimaryKey(key.High);
            if (to.IsEmpty)
            {
                // Below the empty key there is nothing, and an empty bound would mean no bound.
                return new Choice(TkAccess.Nothing, -1, default, default);
            }
        }

        return new Choice(TkAccess.KeyRange, -1, from, to);
    }

    /// <summary>
    /// The zone map that can serve a filter, if any: one on a field the filter bounds. Gives the windows
    /// to look at, as a range of keys of its tree, and whether they are known to be more than one.
    /// </summary>
    private int ZoneRange(
        ReadOnlySpan<FilterTerm> terms, IndexState[] states, out (ReadOnlyMemory<byte> From, ReadOnlyMemory<byte> To) windows, out bool several)
    {
        windows = default;
        several = false;
        for (var i = 0; i < _indexes.Length; i++)
        {
            var zone = _indexes[i];
            if (!zone.IsZoneMap || !states[i].Usable || Find(terms, zone.Fields[0]) is not { } range)
            {
                continue;
            }

            var hasFirst = TryWindow(zone, range.Low, out var first);
            var hasLast = TryWindow(zone, range.High, out var last);
            if (!hasFirst && !hasLast)
            {
                // Only "has a date" or "has none": every window, or none of them, and neither is worth a zone map.
                continue;
            }

            byte[] from = [];
            byte[] to = [];
            if (hasFirst)
            {
                from = new byte[sizeof(long)];
                ZoneKey(first, from);
            }

            if (hasLast && last < long.MaxValue)
            {
                to = new byte[sizeof(long)];
                ZoneKey(last + 1, to);
            }

            windows = (from, to);
            several = !hasFirst || !hasLast || last > first;
            return i;
        }

        return -1;
    }

    /// <summary>Whether the filter fixes a field of the index to having no value: such rows are exempt from uniqueness.</summary>
    private static bool PinsNoValue(IndexDefinition index, ReadOnlySpan<FilterTerm> terms)
    {
        for (var i = 0; i < index.Fields.Length; i++)
        {
            if (index.Nullable[i] && Find(terms, index.Fields[i]) is { Low: [0] })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether every row the filter accepts meets the conditions of a partial index.</summary>
    private static bool Implies(ReadOnlySpan<FilterTerm> terms, ReadOnlySpan<FilterTerm> where)
    {
        foreach (var condition in where)
        {
            if (Find(terms, condition.Field) is not { } restricted || !condition.Range.Covers(restricted))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The part of an index a filter needs: the entries that start with the values its leading fields
    /// are fixed to, narrowed by the range on the next field if there is one.
    /// </summary>
    private Choice IndexRange(int index, int pinned, FilterTerm[] terms)
    {
        var fields = _indexes[index].Fields;
        var prefix = new List<byte>();
        for (var i = 0; i < pinned; i++)
        {
            prefix.AddRange(Find(terms, fields[i])!.Low!);
        }

        byte[] start = [.. prefix];
        var from = start;
        var to = PrefixEnd(start);
        if (pinned < fields.Length && Find(terms, fields[pinned]) is { } range)
        {
            if (range.Low is not null)
            {
                // No encoded value is the beginning of another: past every key that starts with a value is past the value.
                byte[] low = [.. start, .. range.Low];
                var first = range.LowInclusive ? low : PrefixEnd(low);
                if (first is null)
                {
                    return new Choice(TkAccess.Nothing, -1, default, default);
                }

                from = first;
            }

            if (range.High is not null)
            {
                byte[] high = [.. start, .. range.High];
                to = range.HighInclusive ? PrefixEnd(high) : high;
            }
        }

        return new Choice(TkAccess.IndexScan, index, from, to);
    }

    /// <summary>
    /// A key as the table stores it, from the same key as an index would hold it. Only text differs: in
    /// the table it is the whole key, so it carries no end mark and its zeros are not escaped.
    /// </summary>
    private byte[] ToPrimaryKey(byte[] encoded)
    {
        if (_keyType != TkColumnType.Text)
        {
            return encoded;
        }

        var stored = new List<byte>(encoded.Length);
        for (var i = 0; i < encoded.Length; i++)
        {
            if (encoded[i] == 0)
            {
                // 00 FF is a zero of the text; 00 00 is its end.
                if (i + 1 >= encoded.Length || encoded[i + 1] != 0xFF)
                {
                    break;
                }

                stored.Add(0);
                i++;
                continue;
            }

            stored.Add(encoded[i]);
        }

        return [.. stored];
    }

    private async ValueTask<T?> GetCoreAsync<TKey>(
        IPageSource source,
        PageNumber catalogRoot,
        ulong snapshotId,
        TkWriteTransaction? writer,
        TKey key,
        CancellationToken cancellationToken)
    {
        // One buffer for the key and, after it, the row: a row that fits in a leaf needs nothing else.
        var tree = _database.Tree;
        var maxKey = tree.MaxKeySize;
        var buffer = ArrayPool<byte>.Shared.Rent(maxKey + tree.MaxEntrySize);
        byte[]? large = null;
        try
        {
            var keyLength = TkKey.Write(key, _keyType, buffer.AsSpan(0, maxKey));
            var root = await RootAsync(source, catalogRoot, snapshotId, writer, cancellationToken).ConfigureAwait(false);
            var row = buffer.AsMemory(maxKey, tree.MaxEntrySize);
            var length = await BTree.GetAsync(source, root, buffer.AsMemory(0, keyLength), row, cancellationToken)
                .ConfigureAwait(false);
            if (BTree.IsTooSmall(length, out var required))
            {
                // The row lives in overflow pages: read it again now that its size is known.
                large = ArrayPool<byte>.Shared.Rent(required);
                row = large;
                length = await BTree.GetAsync(source, root, buffer.AsMemory(0, keyLength), row, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (length < 0)
            {
                return null;
            }

            row = row[..length];
            var plan = await PlanAsync(row, source, catalogRoot, writer, cancellationToken).ConfigureAwait(false);
            return Decode(row.Span, plan);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (large is not null)
            {
                ArrayPool<byte>.Shared.Return(large);
            }
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
                source, catalogRoot, TableKey, version, cancellationToken)
                .ConfigureAwait(false);
            var built = ReadPlan.Build(Name, schema, T.Columns, T.KeyColumn);
            Volatile.Write(ref _plans[version], built);
            return built;
        }
    }

    private readonly record struct IndexState(bool Usable, PageNumber Root);

    private readonly record struct Choice(TkAccess Access, int Index, ReadOnlyMemory<byte> From, ReadOnlyMemory<byte> To);

    private sealed class Location(ulong snapshotId, PageNumber root)
    {
        // Which indexes a reader of this commit can use: looked up by the first query, then kept.
        public IndexState[]? Indexes;

        public ulong SnapshotId { get; } = snapshotId;

        public PageNumber Root { get; } = root;
    }

    /// <summary>
    /// A bulk load in progress: where each row goes as it arrives. While the rows come in key order into
    /// an empty table they go straight into the tree; otherwise they are sorted. The entries of every
    /// index are always sorted, since they do not come in their order.
    /// </summary>
    private sealed class BulkLoad
    {
        private readonly Table<T> _table;
        private readonly WriteTransaction _inner;
        private readonly TkWriteTransaction.TableState _state;
        private readonly int _maxKey;
        private readonly int _budget;
        private readonly byte[] _key;
        private readonly byte[] _entry;
        private readonly byte[] _mark = new byte[BulkEntryMark];
        private byte[] _row;
        private bool _spill;

        public BulkLoad(Table<T> table, WriteTransaction inner, TkWriteTransaction.TableState state)
        {
            var database = table._database;
            _table = table;
            _inner = inner;
            _state = state;
            _maxKey = database.Tree.MaxKeySize;
            _budget = Math.Max(1, database.BulkSortBudget / (table._indexes.Length + 1));
            _key = new byte[_maxKey];
            _entry = new byte[_maxKey + 1 + table._whereScratch];
            _row = new byte[database.Tree.MaxEntrySize];
            Existing = state.Root;
            Builder = Existing.IsNone ? new BTreeBuilder(database.Tree, inner, database.BulkSpillPages) : null;
            Entries = new RunSorter[table._indexes.Length];
            Zones = new ZoneWindows?[table._indexes.Length];
            for (var i = 0; i < Entries.Length; i++)
            {
                Entries[i] = new RunSorter(inner, _budget);
                Zones[i] = table._indexes[i].IsZoneMap ? new ZoneWindows() : null;
            }
        }

        public long Count { get; private set; }

        /// <summary>Gets the tree the rows are going straight into, while they can.</summary>
        public BTreeBuilder? Builder { get; private set; }

        /// <summary>Gets the rows set aside to be sorted, once any had to be.</summary>
        public RunSorter? Sorter { get; private set; }

        /// <summary>Gets the tree the sorted rows are merged with: the table as it was, or what was built before the rows lost their order.</summary>
        public PageNumber Existing { get; private set; }

        public RunSorter[] Entries { get; }

        /// <summary>Gets, for each zone map of the table, the windows of the rows loaded so far.</summary>
        public ZoneWindows?[] Zones { get; }

        /// <summary>Takes a row in. Returns whether something is due to be written: see <see cref="WriteAsync"/>.</summary>
        public bool Add(T row)
        {
            ArgumentNullException.ThrowIfNull(row);
            var table = _table;
            var key = _key.AsSpan(0, T.WriteKey(row, _key.AsSpan(0, _maxKey)));
            var encoded = Encode(row);
            var due = false;
            var indexes = table._indexes;
            for (var i = 0; i < indexes.Length; i++)
            {
                if (Zones[i] is { } zone)
                {
                    if (TryWindow(indexes[i], row, out var window))
                    {
                        zone.Add(window, key);
                    }

                    continue;
                }

                var length = table.IndexKey(
                    indexes[i], row, key, _entry.AsSpan(0, _maxKey), _entry.AsSpan(_maxKey), out var prefix, out var hasNull);
                if (length >= 0)
                {
                    // With the entry goes where its primary key starts, and whether one of its fields has no value.
                    BinaryPrimitives.WriteUInt16LittleEndian(_mark, (ushort)prefix);
                    _mark[sizeof(ushort)] = hasNull ? (byte)1 : (byte)0;
                    Entries[i].Add(_entry.AsSpan(0, length), _mark);
                    due |= Entries[i].IsFull;
                }
            }

            var builder = Builder;
            if (builder is not null && builder.Count > 0 && key.SequenceCompareTo(builder.LastKey) <= 0)
            {
                if (key.SequenceEqual(builder.LastKey))
                {
                    throw table.Duplicate(key);
                }

                // Out of order. What is built so far stands as the table, and the rest is sorted and merged
                // into it; its pages go to the file before anything reads them back.
                Existing = builder.Complete();
                Builder = builder = null;
                _spill = true;
                due = true;
            }

            if (builder is not null)
            {
                builder.Add(key, encoded);
                due |= builder.ShouldSpill;
            }
            else
            {
                Sorter ??= new RunSorter(_inner, _budget);
                Sorter.Add(key, encoded);
                due |= Sorter.IsFull;
            }

            Count++;
            return due;
        }

        /// <summary>Writes what is due: the finished pages of the tree, and the runs of the sorters that are full.</summary>
        public async ValueTask WriteAsync(CancellationToken cancellationToken)
        {
            if (_spill || Builder is { ShouldSpill: true })
            {
                _spill = false;
                await _inner.SpillAsync(cancellationToken).ConfigureAwait(false);
            }

            if (Sorter is { IsFull: true })
            {
                await Sorter.WriteRunAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var entries in Entries)
            {
                if (entries.IsFull)
                {
                    await entries.WriteRunAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>Writes the row into a buffer kept for the whole load, which grows when a row needs it to.</summary>
        private ReadOnlySpan<byte> Encode(T row)
        {
            while (true)
            {
                try
                {
                    var writer = new TkRowWriter(_row, _state.Schema!.Version, T.Columns.Length);
                    T.Write(ref writer, row);
                    return _row.AsSpan(0, writer.Length);
                }
                catch (TkRowTooLargeException) when (_row.Length < BTree.MaxValueSize)
                {
                    _row = new byte[(int)Math.Min((long)_row.Length * 4, BTree.MaxValueSize)];
                }
                catch (TkRowTooLargeException)
                {
                    throw new TkRowTooLargeException(
                        $"Table '{_table.Name}': the row is larger than {BTree.MaxValueSize} bytes, the most a row may take.");
                }
            }
        }
    }

    /// <summary>The index entries one write removes and adds, in one rented buffer: for each index, the old key, the new key and the value of the new entry.</summary>
    private sealed class KeyChanges : IDisposable
    {
        private readonly byte[] _buffer;
        private readonly int[] _lengths;
        private readonly bool[] _hasNull;

        private readonly int _maxKey;
        private readonly int _scratch;
        private readonly int _count;

        public KeyChanges(int count, int maxKey, int scratch)
        {
            _count = count;
            _maxKey = maxKey;
            _scratch = scratch;
            _lengths = new int[count * 2];
            _hasNull = new bool[count];

            _buffer = ArrayPool<byte>.Shared.Rent((count * Slot) + scratch);
        }

        public Span<byte> Scratch => _buffer.AsSpan(_count * Slot, _scratch);

        private int Slot => (_maxKey * 2) + sizeof(ushort);

        public ref int OldLength(int index) => ref _lengths[index * 2];

        public ref int NewLength(int index) => ref _lengths[(index * 2) + 1];

        /// <summary>Whether the new entry of an index has a field with no value, which exempts it from uniqueness.</summary>
        public ref bool HasNull(int index) => ref _hasNull[index];

        public Memory<byte> Old(int index, int length) => _buffer.AsMemory(index * Slot, length);

        public Memory<byte> New(int index, int length) => _buffer.AsMemory((index * Slot) + _maxKey, length);

        public Memory<byte> Value(int index) => _buffer.AsMemory((index * Slot) + (_maxKey * 2), sizeof(ushort));

        public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);
    }
}
