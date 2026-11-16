// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers;
using System.Buffers.Binary;
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
    private readonly IndexDefinition[] _indexes;

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
        foreach (var index in _indexes)
        {
            _whereScratch = Math.Max(_whereScratch, IndexDefinition.ScratchSize(index.Where));
        }
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
            if (_indexes.Length > 0)
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
            if (_indexes.Length > 0)
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

            try
            {
                if (changes is not null)
                {
                    await ApplyAsync(transaction, state, changes, cancellationToken).ConfigureAwait(false);
                }

                state.Root = await tree.PutAsync(
                    transaction.Inner, state.Root, key, rowBuffer.AsMemory(0, rowLength), cancellationToken)
                    .ConfigureAwait(false);
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
        IndexDefinition index, T row, ReadOnlySpan<byte> primaryKey, Span<byte> destination, Span<byte> scratch, out int prefix)
    {
        prefix = 0;
        if (!Matches(row, index.Where, scratch))
        {
            return -1;
        }

        var writer = new TkKeyWriter(destination);
        foreach (var field in index.Fields)
        {
            T.WriteField(ref writer, row, field);
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
                changes.OldLength(i) = old is null
                    ? -1
                    : IndexKey(_indexes[i], old, primaryKey, changes.Old(i, maxKey).Span, scratch, out _);
                changes.NewLength(i) = -1;
                if (replacement is not null)
                {
                    changes.NewLength(i) = IndexKey(_indexes[i], replacement, primaryKey, changes.New(i, maxKey).Span, scratch, out var prefix);
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
        TkWriteTransaction transaction, TkWriteTransaction.TableState state, KeyChanges changes, CancellationToken cancellationToken)
    {
        var tree = _database.Tree;
        for (var i = 0; i < _indexes.Length; i++)
        {
            var entry = state.Bound[i];
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
                    out var prefix);
                if (length >= 0)
                {
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
            else if (choice.Access == TkAccess.IndexScan)
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
            if (!states[i].Usable || !Implies(terms, index.Where))
            {
                continue;
            }

            var pinned = 0;
            while (pinned < index.Fields.Length && Find(terms, index.Fields[pinned]) is { IsPoint: true })
            {
                pinned++;
            }

            var ranged = pinned < index.Fields.Length && Find(terms, index.Fields[pinned]) is not null;
            var score = (pinned * 4) + (ranged ? 2 : 0) + (index.Where.Length > 0 ? 1 : 0);
            if (score > bestScore)
            {
                (best, bestScore, bestFixed) = (i, score, pinned);
            }
        }

        if (best >= 0 && (bestFixed > 0 || key is null))
        {
            return IndexRange(best, bestFixed, terms);
        }

        if (key is null)
        {
            return new Choice(TkAccess.TableScan, -1, default, default);
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

    /// <summary>The index entries one write removes and adds, in one rented buffer: for each index, the old key, the new key and the value of the new entry.</summary>
    private sealed class KeyChanges : IDisposable
    {
        private readonly byte[] _buffer;
        private readonly int[] _lengths;
        private readonly int _maxKey;
        private readonly int _scratch;
        private readonly int _count;

        public KeyChanges(int count, int maxKey, int scratch)
        {
            _count = count;
            _maxKey = maxKey;
            _scratch = scratch;
            _lengths = new int[count * 2];
            _buffer = ArrayPool<byte>.Shared.Rent((count * Slot) + scratch);
        }

        public Span<byte> Scratch => _buffer.AsSpan(_count * Slot, _scratch);

        private int Slot => (_maxKey * 2) + sizeof(ushort);

        public ref int OldLength(int index) => ref _lengths[index * 2];

        public ref int NewLength(int index) => ref _lengths[(index * 2) + 1];

        public Memory<byte> Old(int index, int length) => _buffer.AsMemory(index * Slot, length);

        public Memory<byte> New(int index, int length) => _buffer.AsMemory((index * Slot) + _maxKey, length);

        public Memory<byte> Value(int index) => _buffer.AsMemory((index * Slot) + (_maxKey * 2), sizeof(ushort));

        public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);
    }
}
