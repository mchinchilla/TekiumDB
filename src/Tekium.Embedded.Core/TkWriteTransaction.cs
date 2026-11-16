// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers;
using Tekium.Embedded.Catalog;
using Tekium.Embedded.Storage;

namespace Tekium.Embedded;

/// <summary>
/// The single writer. Its changes are visible only to itself until <see cref="CommitAsync"/>, which
/// makes all of them durable at once or none. Leaving the scope without committing rolls back.
/// </summary>
public sealed class TkWriteTransaction : TkTransaction
{
    private readonly WriteTransaction _inner;
    private readonly Dictionary<string, TableState> _tables = new(StringComparer.Ordinal);

    internal TkWriteTransaction(TekiumDb database, WriteTransaction inner)
        : base(database) => _inner = inner;

    internal WriteTransaction Inner => _inner;

    internal override IPageSource Source => _inner;

    internal override PageNumber CatalogRoot => _inner.Root;

    /// <summary>Makes every change of the transaction durable, atomically. The transaction is over afterwards.</summary>
    /// <param name="cancellationToken">Cancels the commit while that still leaves the database untouched.</param>
    /// <returns>A task that completes when the changes are on stable storage.</returns>
    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        // Where each changed table now is goes into the catalog, and the catalog root into the root slot.
        var entry = ArrayPool<byte>.Shared.Rent(TableInfo.EncodedSize);
        try
        {
            foreach (var state in _tables.Values)
            {
                if (state.Changed)
                {
                    new TableInfo(state.Root, state.Schema!.Version).Encode(entry);
                    await Database.Tree.PutAsync(
                        _inner, state.TableKey, entry.AsMemory(0, TableInfo.EncodedSize), cancellationToken)
                        .ConfigureAwait(false);
                    state.Changed = false;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(entry);
        }

        await _inner.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Ends the transaction. Without a commit, everything it did is discarded.</summary>
    /// <returns>A task that is already complete.</returns>
    public override ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return _inner.DisposeAsync();
    }

    /// <summary>What this transaction knows about a table: loaded from the catalog on first use, then kept here.</summary>
    internal async ValueTask<TableState> TableAsync(string name, byte[] tableKey, CancellationToken cancellationToken)
    {
        if (_tables.TryGetValue(name, out var state))
        {
            return state;
        }

        state = new TableState(tableKey);
        var info = await CatalogStore.FindTableAsync(_inner, _inner.Root, tableKey, cancellationToken)
            .ConfigureAwait(false);
        if (info is { } found)
        {
            state.Root = found.Root;
            state.Schema = await CatalogStore.LoadSchemaAsync(
                _inner, _inner.Root, tableKey, found.SchemaVersion, cancellationToken)
                .ConfigureAwait(false);
        }

        _tables.Add(name, state);
        return state;
    }

    /// <summary>
    /// Makes the table writable with the columns of a type: creates it if it does not exist, and
    /// registers a new schema version if the type no longer matches the latest one.
    /// </summary>
    internal async ValueTask<TableState> TableForWriteAsync<T>(Table<T> table, CancellationToken cancellationToken)
        where T : class, ITkRow<T>
    {
        var state = await TableAsync(table.Name, table.TableKey, cancellationToken).ConfigureAwait(false);
        if (state.Schema is { } current && state.WritesAs == typeof(T))
        {
            return state;
        }

        if (state.Schema is null || !state.Schema.Matches(T.Columns, T.KeyColumn))
        {
            var version = 1;
            if (state.Schema is { } previous)
            {
                // Refuses what cannot be read back before anything is written with the new columns.
                ReadPlan.Build(table.Name, previous, T.Columns, T.KeyColumn);
                version = previous.Version + 1;
                if (version > byte.MaxValue)
                {
                    throw new TkSchemaException(
                        $"Table '{table.Name}' has used all {byte.MaxValue} schema versions a table may have.");
                }
            }

            var schema = new TableSchema((byte)version, T.Columns.ToArray(), T.KeyColumn);
            var size = schema.EncodedSize();
            var buffer = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                schema.Encode(buffer);
                await Database.Tree.PutAsync(
                    _inner, CatalogStore.SchemaKey(table.TableKey, schema.Version), buffer.AsMemory(0, size), cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            state.Schema = schema;
            state.UncommittedVersion = schema.Version;
            state.Changed = true;
        }

        state.WritesAs = typeof(T);
        return state;
    }

    /// <summary>A table as this transaction sees it.</summary>
    internal sealed class TableState(byte[] tableKey)
    {
        public byte[] TableKey { get; } = tableKey;

        /// <summary>Gets or sets the root of the rows. <see cref="PageNumber.None"/> while the table is empty or does not exist.</summary>
        public PageNumber Root { get; set; }

        /// <summary>Gets or sets the schema rows are written with. <see langword="null"/> while the table does not exist.</summary>
        public TableSchema? Schema { get; set; }

        /// <summary>Gets or sets the schema version this transaction registered, which nobody else can see yet. Zero if none.</summary>
        public byte UncommittedVersion { get; set; }

        /// <summary>Gets or sets the type whose columns were last checked against <see cref="Schema"/>.</summary>
        public Type? WritesAs { get; set; }

        /// <summary>Gets or sets a value indicating whether the catalog entry must be rewritten at commit.</summary>
        public bool Changed { get; set; }
    }
}
