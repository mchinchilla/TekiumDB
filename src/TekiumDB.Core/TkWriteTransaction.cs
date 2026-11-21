// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers;
using TekiumDB.Catalog;
using TekiumDB.Storage;

namespace TekiumDB;

/// <summary>
/// The single writer. Its changes are visible only to itself until <see cref="CommitAsync"/>, which
/// makes all of them durable at once or none. Leaving the scope without committing rolls back.
/// </summary>
public sealed class TkWriteTransaction : TkTransaction
{
    private readonly WriteTransaction _inner;
    private readonly Dictionary<string, TableState> _tables = new(StringComparer.Ordinal);
    private Dictionary<string, ITkCommitParticipant>? _participants;
    private bool _failed;

    internal TkWriteTransaction(TekiumDb database, WriteTransaction inner)
        : base(database) => _inner = inner;

    /// <summary>
    /// Gets the id of this transaction: the one its changes carry in <see cref="TekiumDb.WatchAsync{T}"/>,
    /// and the one <see cref="TekiumDb.LastTransactionId"/> gives once it has committed something.
    /// </summary>
    public ulong TransactionId => _inner.Id.Value;

    internal WriteTransaction Inner => _inner;

    internal override IPageSource Source => _inner;

    internal override PageNumber CatalogRoot => _inner.Root;

    /// <summary>Makes every change of the transaction durable, atomically. The transaction is over afterwards.</summary>
    /// <param name="cancellationToken">Cancels the commit while that still leaves the database untouched.</param>
    /// <returns>A task that completes when the changes are on stable storage.</returns>
    /// <exception cref="InvalidOperationException">An operation of the transaction failed halfway: it can only be rolled back.</exception>
    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_failed)
        {
            throw new InvalidOperationException(
                "An operation of this transaction failed halfway, so what it holds is not consistent: it can only be rolled back.");
        }

        // What was learnt about the zone maps goes into them first: it moves their roots.
        foreach (var state in _tables.Values)
        {
            if (state.FlushZones is { } flush)
            {
                await flush(this, cancellationToken).ConfigureAwait(false);
            }
        }

        if (Database.Diagnostics is { } diagnostics)
        {
            // Only when someone asked for counters: how deep each tree this transaction changed is now.
            foreach (var (name, state) in _tables)
            {
                if (state.Changed)
                {
                    diagnostics.SetDepth($"table {name}", await Indexing.BTree.HeightAsync(_inner, state.Root, cancellationToken).ConfigureAwait(false));
                }

                foreach (var (index, changed) in state.Indexes ?? [])
                {
                    if (changed.Dirty)
                    {
                        diagnostics.SetDepth(
                            $"index {name}.{index}", await Indexing.BTree.HeightAsync(_inner, changed.Root, cancellationToken).ConfigureAwait(false));
                    }
                }
            }
        }

        // Whatever else keeps its own trees in this transaction says where they are now.
        if (_participants is not null)
        {
            foreach (var participant in _participants.Values)
            {
                await participant.FlushAsync(this, cancellationToken).ConfigureAwait(false);
            }
        }

        // Where each changed table and index now is goes into the catalog, and the catalog root into the root slot.
        var entry = ArrayPool<byte>.Shared.Rent(TableInfo.EncodedSize);
        try
        {
            foreach (var state in _tables.Values)
            {
                foreach (var dropped in state.DroppedIndexes)
                {
                    await Database.Tree.DeleteAsync(_inner, dropped, cancellationToken).ConfigureAwait(false);
                }

                state.DroppedIndexes.Clear();
                if (state.Indexes is { } indexes)
                {
                    foreach (var index in indexes.Values)
                    {
                        if (index.Dirty)
                        {
                            await Database.Tree.PutAsync(
                                _inner, index.CatalogKey, CatalogStore.EncodeIndex(index.Root, index.Definition), cancellationToken)
                                .ConfigureAwait(false);
                            index.Dirty = false;
                        }
                    }
                }

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

        var history = await Database.RecordHistoryAsync(_inner, cancellationToken).ConfigureAwait(false);
        await _inner.CommitAsync(cancellationToken).ConfigureAwait(false);
        Database.Committed(history);
    }

    /// <summary>Ends the transaction. Without a commit, everything it did is discarded.</summary>
    /// <returns>A task that is already complete.</returns>
    public override ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return _inner.DisposeAsync();
    }

    /// <summary>
    /// What a structure outside the tables (a vector index) keeps for the length of this transaction, under
    /// a name of its own. It is asked to write its catalog entry when the transaction commits.
    /// </summary>
    internal bool TryGetParticipant(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ITkCommitParticipant? participant)
    {
        participant = null;
        return _participants is not null && _participants.TryGetValue(name, out participant);
    }

    /// <summary>Keeps <paramref name="participant"/> for the length of this transaction.</summary>
    internal void AddParticipant(string name, ITkCommitParticipant participant) =>
        (_participants ??= new Dictionary<string, ITkCommitParticipant>(StringComparer.Ordinal)).Add(name, participant);

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
    /// Forgets what this transaction knows about a table, after its catalog entries were moved or written
    /// by hand: the next use loads it from the catalog again. The index entries it was to remove at commit
    /// are removed now, since nothing would remove them later.
    /// </summary>
    internal async ValueTask ForgetAsync(string name, CancellationToken cancellationToken)
    {
        if (_tables.Remove(name, out var state))
        {
            foreach (var dropped in state.DroppedIndexes)
            {
                await Database.Tree.DeleteAsync(_inner, dropped, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Records that an operation failed after it had begun to change the trees. A row and its index
    /// entries live in several trees, so what the transaction holds may no longer agree with itself.
    /// </summary>
    internal void Fail() => _failed = true;

    /// <summary>The CHECK and FOREIGN KEY constraints of a table as the catalog of this transaction has them. Empty if none.</summary>
    internal async ValueTask<byte[]> ConstraintsAsync(TableState state, CancellationToken cancellationToken) =>
        state.Constraints ??= await CatalogStore.ReadAsync(
            _inner, _inner.Root, CatalogStore.ConstraintsKey(state.TableKey), cancellationToken).ConfigureAwait(false);

    /// <summary>Replaces the constraints of a table in the catalog of this transaction.</summary>
    internal async ValueTask SetConstraintsAsync(TableState state, byte[] encoded, CancellationToken cancellationToken)
    {
        await SetAsync(CatalogStore.ConstraintsKey(state.TableKey), encoded, cancellationToken).ConfigureAwait(false);
        state.Constraints = encoded;
    }

    /// <summary>The foreign keys of other tables that refer to a table, as this transaction sees them.</summary>
    internal async ValueTask<List<Referrer>> ReferrersAsync(TableState state, CancellationToken cancellationToken) =>
        state.Referrers ??= ConstraintSet.DecodeReferrers(await CatalogStore.ReadAsync(
            _inner, _inner.Root, CatalogStore.ReferrersKey(state.TableKey), cancellationToken).ConfigureAwait(false));

    /// <summary>Replaces, among those who refer to a table, the foreign keys of one referring table.</summary>
    internal async ValueTask SetReferrersAsync(
        string table, string referring, IEnumerable<Referrer> foreignKeys, CancellationToken cancellationToken)
    {
        var state = await TableAsync(table, CatalogStore.TableKey(table), cancellationToken).ConfigureAwait(false);
        var referrers = await ReferrersAsync(state, cancellationToken).ConfigureAwait(false);
        referrers.RemoveAll(referrer => string.Equals(referrer.Table, referring, StringComparison.Ordinal));
        referrers.AddRange(foreignKeys);
        await SetAsync(CatalogStore.ReferrersKey(state.TableKey), ConstraintSet.EncodeReferrers(referrers), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Writes to the zone maps of a table what this transaction has learnt about them and still has in memory.</summary>
    internal ValueTask FlushZonesAsync(string table, CancellationToken cancellationToken) =>
        _tables.TryGetValue(table, out var state) && state.FlushZones is { } flush ? flush(this, cancellationToken) : default;

    /// <summary>The indexes of a table as this transaction sees them, by name: loaded from the catalog on first use.</summary>
    internal async ValueTask<Dictionary<string, IndexEntry>> IndexesAsync(TableState state, CancellationToken cancellationToken)
    {
        if (state.Indexes is { } known)
        {
            return known;
        }

        known = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        var from = CatalogStore.IndexKeyPrefix(state.TableKey);
        var to = (byte[])from.Clone();
        to[^1] = 1;
        var entries = Indexing.BTree.RangeAsync(_inner, _inner.Root, from, to, cancellationToken);
        await foreach (var entry in entries.ConfigureAwait(false))
        {
            var name = System.Text.Encoding.UTF8.GetString(entry.Key.Span[from.Length..]);
            known.Add(name, new IndexEntry(entry.Key.ToArray(), CatalogStore.IndexDefinition(entry.Value.Span).ToArray())
            {
                Root = CatalogStore.IndexRoot(entry.Value.Span),
            });
        }

        state.Indexes = known;
        return known;
    }

    /// <summary>
    /// Makes the indexes of the table those the type declares, so that a write can keep them: drops the
    /// ones it does not declare, or declares differently, and builds the ones that are missing.
    /// </summary>
    internal async ValueTask BindIndexesAsync<T>(Table<T> table, TableState state, CancellationToken cancellationToken)
        where T : class, ITkRow<T>
    {
        if (state.IndexedAs == typeof(T))
        {
            return;
        }

        // What another type learnt about its zone maps belongs to the indexes bound for it: it goes in before they change.
        if (state.FlushZones is { } flush)
        {
            await flush(this, cancellationToken).ConfigureAwait(false);
        }

        var known = await IndexesAsync(state, cancellationToken).ConfigureAwait(false);
        var declared = table.Indexes;
        var bound = new IndexEntry[declared.Length];
        try
        {
            foreach (var (name, entry) in known.ToArray())
            {
                var same = Array.Find(declared, index => string.Equals(index.Name, name, StringComparison.Ordinal));
                if (same is null || !same.Encoded.AsSpan().SequenceEqual(entry.Definition))
                {
                    known.Remove(name);
                    state.DroppedIndexes.Add(entry.CatalogKey);
                    await Indexing.BTree.DropAsync(_inner, entry.Root, cancellationToken).ConfigureAwait(false);
                }
            }

            for (var i = 0; i < declared.Length; i++)
            {
                if (!known.TryGetValue(declared[i].Name, out var entry))
                {
                    entry = new IndexEntry(declared[i].CatalogKey, declared[i].Encoded) { Dirty = true };
                    known.Add(declared[i].Name, entry);
                    await table.BuildIndexAsync(this, state, declared[i], entry, cancellationToken).ConfigureAwait(false);
                }

                bound[i] = entry;
            }

            state.Bound = bound;
            await table.BindConstraintsAsync(this, state, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Fail();
            throw;
        }

        state.IndexedAs = typeof(T);
    }

    private async ValueTask SetAsync(byte[] key, byte[] value, CancellationToken cancellationToken)
    {
        if (value.Length == 0)
        {
            await Database.Tree.DeleteAsync(_inner, key, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Database.Tree.PutAsync(_inner, key, value, cancellationToken).ConfigureAwait(false);
        }
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
            await BindIndexesAsync(table, state, cancellationToken).ConfigureAwait(false);
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
        await BindIndexesAsync(table, state, cancellationToken).ConfigureAwait(false);
        return state;
    }

    /// <summary>An index as this transaction sees it.</summary>
    internal sealed class IndexEntry(byte[] catalogKey, byte[] definition)
    {
        public byte[] CatalogKey { get; } = catalogKey;

        /// <summary>Gets what the index indexes, as the catalog stores it.</summary>
        public byte[] Definition { get; } = definition;

        /// <summary>Gets or sets the root of its tree. <see cref="PageNumber.None"/> while it is empty.</summary>
        public PageNumber Root { get; set; }

        /// <summary>Gets or sets a value indicating whether the catalog entry must be written at commit.</summary>
        public bool Dirty { get; set; }
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

        /// <summary>Gets or sets the indexes of the table by name. <see langword="null"/> until something asks for them.</summary>
        public Dictionary<string, IndexEntry>? Indexes { get; set; }

        /// <summary>Gets the catalog keys of the indexes this transaction dropped, to remove at commit.</summary>
        public List<byte[]> DroppedIndexes { get; } = [];

        /// <summary>Gets or sets the stored CHECK and FOREIGN KEY constraints. <see langword="null"/> until something asks for them.</summary>
        public byte[]? Constraints { get; set; }

        /// <summary>Gets or sets the foreign keys of other tables that refer to this one. <see langword="null"/> until something asks for them.</summary>
        public List<Referrer>? Referrers { get; set; }

        /// <summary>Gets or sets what the transaction has learnt about each zone map of the table and not yet written to it.</summary>
        public Indexing.ZoneWindows?[]? PendingZones { get; set; }

        /// <summary>Gets or sets what writes <see cref="PendingZones"/> to the zone maps. <see langword="null"/> when there is nothing pending.</summary>
        public Func<TkWriteTransaction, CancellationToken, ValueTask>? FlushZones { get; set; }

        /// <summary>Gets or sets the type whose declared indexes <see cref="Bound"/> are.</summary>
        public Type? IndexedAs { get; set; }

        /// <summary>Gets or sets the indexes a write must keep, in the order <see cref="IndexedAs"/> declares them.</summary>
        public IndexEntry[] Bound { get; set; } = [];
    }
}
