// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using Tekium.Embedded.Catalog;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Sql.Storage;

/// <summary>
/// A table of the catalog, used without the row type that declares it: rows are arrays of values, one
/// per column. It reads any table. It writes those whose indexes and constraints it can keep exactly as
/// the typed API would: plain, unique and partial indexes over columns, and FOREIGN KEY constraints. A
/// table with indexes over expressions, zone maps or CHECK constraints is read-only here.
/// </summary>
internal sealed class SqlTable
{
    private readonly TkTransaction _transaction;
    private readonly TkWriteTransaction.TableState? _state;
    private readonly PageNumber _root;
    private readonly Dictionary<byte, (TkColumn[] Stored, int[] Identity, int[] ToCurrent)> _versions = [];

    private SqlTable(
        TkTransaction transaction, string name, byte[] tableKey, TableSchema schema, PageNumber root, TkWriteTransaction.TableState? state)
    {
        _transaction = transaction;
        _state = state;
        _root = root;
        Name = name;
        TableKey = tableKey;
        Columns = schema.Columns;
        KeyColumn = schema.KeyColumn;
        Version = schema.Version;
    }

    public string Name { get; }

    public byte[] TableKey { get; }

    public TkColumn[] Columns { get; }

    public int KeyColumn { get; }

    public byte Version { get; }

    public List<StoredIndex> Indexes { get; } = [];

    /// <summary>Gets the FOREIGN KEY constraints of the table, each with the column it is on.</summary>
    public List<(ForeignKeyInfo Info, int Column)> ForeignKeys { get; } = [];

    /// <summary>Gets why the table cannot be written through SQL, or null if it can.</summary>
    public string? ReadOnlyReason { get; private set; }

    /// <summary>Gets where the rows are now, as the transaction sees them.</summary>
    public PageNumber Root => _state?.Root ?? _root;

    private IPageSource Source => _transaction.Source;

    /// <summary>Finds a table by name, exactly or ignoring case. Null if there is none.</summary>
    public static async ValueTask<SqlTable?> OpenAsync(TkTransaction transaction, string name, CancellationToken cancellationToken)
    {
        var source = transaction.Source;
        var catalog = transaction.CatalogRoot;
        var writer = transaction as TkWriteTransaction;
        var actual = await ResolveAsync(transaction, name, cancellationToken).ConfigureAwait(false);
        if (actual is null)
        {
            return null;
        }

        var tableKey = CatalogStore.TableKey(actual);
        SqlTable table;
        if (writer is not null)
        {
            var state = await writer.TableAsync(actual, tableKey, cancellationToken).ConfigureAwait(false);
            if (state.Schema is null)
            {
                return null;
            }

            table = new SqlTable(transaction, actual, tableKey, state.Schema, state.Root, state);
            foreach (var (indexName, entry) in await writer.IndexesAsync(state, cancellationToken).ConfigureAwait(false))
            {
                var index = StoredIndex.Decode(indexName, entry.Definition, table.Columns);
                index.Entry = entry;
                table.Indexes.Add(index);
            }
        }
        else
        {
            var info = await CatalogStore.FindTableAsync(source, catalog, tableKey, cancellationToken).ConfigureAwait(false);
            if (info is not { } found)
            {
                return null;
            }

            var schema = await CatalogStore.LoadSchemaAsync(source, catalog, tableKey, found.SchemaVersion, cancellationToken).ConfigureAwait(false);
            table = new SqlTable(transaction, actual, tableKey, schema, found.Root, null);
            var from = CatalogStore.IndexKeyPrefix(tableKey);
            var to = (byte[])from.Clone();
            to[^1] = 1;
            await foreach (var entry in BTree.RangeAsync(source, catalog, from, to, cancellationToken).ConfigureAwait(false))
            {
                var index = StoredIndex.Decode(
                    Encoding.UTF8.GetString(entry.Key.Span[from.Length..]), CatalogStore.IndexDefinition(entry.Value.Span).ToArray(), table.Columns);
                index.Root = CatalogStore.IndexRoot(entry.Value.Span);
                table.Indexes.Add(index);
            }
        }

        table.Indexes.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        var constraints = await CatalogStore.ReadAsync(source, catalog, CatalogStore.ConstraintsKey(tableKey), cancellationToken).ConfigureAwait(false);
        var set = ConstraintSet.Decode(constraints);
        string? unchecked0 = null;
        foreach (var foreignKey in set.ForeignKeys)
        {
            // The column is the one its index starts with: the constraint itself does not name it.
            if (table.Indexes.Find(index => string.Equals(index.Name, foreignKey.Index, StringComparison.Ordinal)) is { } index && CanCheck(index))
            {
                table.ForeignKeys.Add((foreignKey, index.Columns[0]));
            }
            else
            {
                unchecked0 ??= $"its FOREIGN KEY '{foreignKey.Name}' has no index '{foreignKey.Index}' that SQL can check it with";
            }
        }

        table.ReadOnlyReason =
            table.Indexes.Find(static index => index.Unmaintainable is not null) is { } index0 ? $"its index '{index0.Name}' cannot be kept from SQL: {index0.Unmaintainable}"
            : set.Checks.Length > 0 ? "it has CHECK constraints, which only the row type that declares them can check"
            : unchecked0;
        return table;
    }

    /// <summary>Whether a FOREIGN KEY can be checked with an index: it holds every row, and starts with a column.</summary>
    public static bool CanCheck(StoredIndex index) =>
        index.Unmaintainable is null && !index.IsZoneMap && index.Where.Length == 0 && index.Columns.Length > 0 && index.Columns[0] >= 0;

    /// <summary>The name of a table as the catalog has it: the one given, or one that differs only in case. Null if there is none.</summary>
    public static async ValueTask<string?> ResolveAsync(TkTransaction transaction, string name, CancellationToken cancellationToken)
    {
        string? insensitive = null;
        await foreach (var existing in NamesAsync(transaction, 1, cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(existing, name, StringComparison.Ordinal))
            {
                return existing;
            }

            if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
            {
                insensitive = existing;
            }
        }

        return insensitive;
    }

    /// <summary>The names of the catalog entries with a prefix: 1 for tables, 8 for views.</summary>
    public static async IAsyncEnumerable<string> NamesAsync(
        TkTransaction transaction, byte prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var from = new[] { prefix };
        var to = new[] { (byte)(prefix + 1) };
        await foreach (var entry in BTree.RangeAsync(transaction.Source, transaction.CatalogRoot, from, to, cancellationToken).ConfigureAwait(false))
        {
            yield return Encoding.UTF8.GetString(entry.Key.Span[1..]);
        }
    }

    /// <summary>The position of a column by name, ignoring case. -1 if the table has none.</summary>
    public int Column(string name)
    {
        for (var i = 0; i < Columns.Length; i++)
        {
            if (string.Equals(Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The primary key of a value, as the tree stores it.</summary>
    public byte[] Key(object value) => SqlValues.PrimaryKey(value, Columns[KeyColumn].Type);

    /// <summary>The row with a primary key. Null if there is none.</summary>
    public async ValueTask<object?[]?> GetAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken)
    {
        var (buffer, length) = await BTree.GetRentedAsync(Source, Root, key, 1024, cancellationToken).ConfigureAwait(false);
        if (buffer is null)
        {
            return null;
        }

        try
        {
            return await DecodeAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// The key after the highest of a table whose key is a whole number; 1 if the table is empty. The
    /// tree is only read forward, so the highest is found by halving: a few dozen descents.
    /// </summary>
    public async ValueTask<long> NextKeyAsync(CancellationToken cancellationToken)
    {
        var type = Columns[KeyColumn].Type;
        var (low, high) = type == TkColumnType.I32 ? (int.MinValue, int.MaxValue) : (long.MinValue, long.MaxValue);
        if (!await AnyFromAsync(low).ConfigureAwait(false))
        {
            return 1;
        }

        // There is a key at or after low, and none after high.
        while (low < high)
        {
            var middle = (long)(low + (((Int128)high - low + 1) / 2));
            if (await AnyFromAsync(middle).ConfigureAwait(false))
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low < (type == TkColumnType.I32 ? int.MaxValue : long.MaxValue)
            ? low + 1
            : throw new TkSqlException($"Table '{Name}' has used the highest key its type holds: give the key of the new row.");

        async ValueTask<bool> AnyFromAsync(long key)
        {
            await foreach (var entry in BTree.RangeAsync(Source, Root, Key(SqlValues.Convert(key, type, Columns[KeyColumn].Name)), default, cancellationToken).ConfigureAwait(false))
            {
                _ = entry;
                return true;
            }

            return false;
        }
    }

    /// <summary>The rows whose primary key is from <paramref name="from"/> up to, not including, <paramref name="to"/>, in key order. Empty bounds are open.</summary>
    public async IAsyncEnumerable<object?[]> ScanAsync(
        ReadOnlyMemory<byte> from, ReadOnlyMemory<byte> to, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var entry in BTree.RangeAsync(Source, Root, from, to, cancellationToken).ConfigureAwait(false))
        {
            yield return await DecodeAsync(entry.Value, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The rows of the entries of an index in a range of its key, in index order.</summary>
    public async IAsyncEnumerable<object?[]> IndexAsync(
        StoredIndex index, ReadOnlyMemory<byte> from, ReadOnlyMemory<byte> to, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var keys = new List<byte[]>();
        await foreach (var entry in BTree.RangeAsync(Source, index.CurrentRoot, from, to, cancellationToken).ConfigureAwait(false))
        {
            var key = entry.Key.Span;
            if (entry.Value.Length != sizeof(ushort) || BinaryPrimitives.ReadUInt16LittleEndian(entry.Value.Span) is var prefix && prefix >= key.Length)
            {
                throw new InvalidDataException($"Index '{index.Name}' of table '{Name}' has a damaged entry.");
            }

            keys.Add(key[prefix..].ToArray());
            if (keys.Count == 256)
            {
                foreach (var primary in keys)
                {
                    if (await GetAsync(primary, cancellationToken).ConfigureAwait(false) is { } row)
                    {
                        yield return row;
                    }
                }

                keys.Clear();
            }
        }

        foreach (var primary in keys)
        {
            if (await GetAsync(primary, cancellationToken).ConfigureAwait(false) is { } row)
            {
                yield return row;
            }
        }
    }

    /// <summary>
    /// About how many entries a tree has in a range of keys, worked out from the shape of the tree: the
    /// nodes on the way to the range say how many children each has. It reads a few pages, not the range.
    /// </summary>
    public async ValueTask<long> EstimateAsync(PageNumber root, ReadOnlyMemory<byte> from, ReadOnlyMemory<byte> to, CancellationToken cancellationToken)
    {
        if (root.IsNone)
        {
            return 0;
        }

        // The path to the start of the range: at each level, where the range starts and ends among the children.
        var levels = new List<(int First, int Last, int Children)>();
        var number = root;
        int leafFirst;
        int leafLast;
        int leafCount;
        var together = true;
        while (true)
        {
            using var page = await Source.PinAsync(number, cancellationToken).ConfigureAwait(false);
            var node = page.Span;
            var count = BTreeNode.Count(node);
            if (BTreeNode.IsLeaf(node))
            {
                leafCount = count;
                leafFirst = from.IsEmpty ? 0 : BTreeNode.LowerBound(node, from.Span, out _);
                leafLast = to.IsEmpty || !together ? count : BTreeNode.LowerBound(node, to.Span, out _);
                break;
            }

            var first = from.IsEmpty ? 0 : BTreeNode.FindChildIndex(node, from.Span);
            var last = to.IsEmpty || !together ? count : BTreeNode.FindChildIndex(node, to.Span);
            levels.Add((first, last, count + 1));
            together &= first == last;
            number = BTreeNode.Child(node, first);
        }

        if (together)
        {
            return Math.Max(0, leafLast - leafFirst);
        }

        // From the leaves up: what a whole subtree holds at each level, taking the nodes seen as typical.
        double subtree = Math.Max(leafCount, 1);
        double estimate = Math.Max(0, leafCount - leafFirst);
        var split = false;
        for (var i = levels.Count - 1; i >= 0; i--)
        {
            var (first, last, children) = levels[i];
            if (first != last)
            {
                // Whole children between the two ends, and half a child for the far end.
                estimate += (Math.Max(0, last - first - 1) * subtree) + (split ? 0 : subtree / 2);
                split = true;
            }

            subtree *= children;
        }

        return Math.Max(1, (long)estimate);
    }

    /// <summary>Adds a row. Throws if a row with its key exists, or if a unique index already has its values.</summary>
    public async ValueTask InsertAsync(object?[] row, CancellationToken cancellationToken)
    {
        var writer = Writable();
        var key = Key(row[KeyColumn] ?? throw new TkSqlException($"Column '{Columns[KeyColumn].Name}' of table '{Name}' is its primary key and cannot be NULL."));
        if (await BTree.GetAsync(writer.Inner, Root, key, Memory<byte>.Empty, cancellationToken).ConfigureAwait(false) != BTree.NotFound)
        {
            throw new TkDuplicateKeyException($"Table '{Name}' already has a row with key {SqlValues.Show(row[KeyColumn])}.");
        }

        await WriteAsync(writer, null, row, key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces a row with another with the same primary key.</summary>
    public ValueTask UpdateAsync(object?[] old, object?[] replacement, CancellationToken cancellationToken) =>
        WriteAsync(Writable(), old, replacement, Key(old[KeyColumn]!), cancellationToken);

    /// <summary>Removes a row.</summary>
    public async ValueTask DeleteAsync(object?[] old, CancellationToken cancellationToken)
    {
        var writer = Writable();
        var key = Key(old[KeyColumn]!);
        var changes = Changes(old, null, key);
        await EnsureNotReferredAsync(writer, old, key, cancellationToken).ConfigureAwait(false);
        try
        {
            await ApplyAsync(writer, changes, cancellationToken).ConfigureAwait(false);
            (_, _state!.Root) = await _transaction.Database.Tree.DeleteAsync(writer.Inner, Root, key, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            writer.Fail();
            throw;
        }

        _state.Changed = true;
    }

    /// <summary>Gives a row that is already in the table its entry in an index that is being built.</summary>
    public async ValueTask IndexRowAsync(StoredIndex index, object?[] row, CancellationToken cancellationToken)
    {
        var writer = Writable();
        var key = Key(row[KeyColumn]!);
        if (Entry(index, row, key, out var prefix, out var hasNull) is not { } entry)
        {
            return;
        }

        if (index.IsUnique && !hasNull && await TakenAsync(writer.Inner, index.CurrentRoot, entry, prefix, cancellationToken).ConfigureAwait(false))
        {
            throw new TkConstraintException(
                $"the rows of table '{Name}' repeat the values of its columns (one of them has key {SqlValues.Show(row[KeyColumn])}).", Name, index.Name, SqlValues.Show(row[KeyColumn]));
        }

        try
        {
            var value = new byte[sizeof(ushort)];
            BinaryPrimitives.WriteUInt16LittleEndian(value, (ushort)prefix);
            index.Entry!.Root = await _transaction.Database.Tree.PutAsync(writer.Inner, index.Entry.Root, entry, value, cancellationToken).ConfigureAwait(false);
            index.Entry.Dirty = true;
        }
        catch
        {
            writer.Fail();
            throw;
        }
    }

    /// <summary>The key of an index for the values of its first columns: what a search for those values starts from.</summary>
    public static byte[] IndexPrefix(StoredIndex index, object?[] values, int count)
    {
        var buffer = new byte[1024];
        var writer = new TkKeyWriter(buffer);
        for (var i = 0; i < count; i++)
        {
            SqlValues.WriteField(ref writer, values[i], index.Types[i], index.Nullable[i]);
        }

        return writer.IsTruncated ? throw new TkSqlException("A value compared with an indexed column is too long.") : buffer.AsSpan(0, writer.Length).ToArray();
    }

    /// <summary>The first key past every key that starts with a prefix. Empty if there is none.</summary>
    public static byte[] After(byte[] prefix)
    {
        for (var i = prefix.Length - 1; i >= 0; i--)
        {
            if (prefix[i] != 0xFF)
            {
                var end = prefix.AsSpan(0, i + 1).ToArray();
                end[i]++;
                return end;
            }
        }

        return [];
    }

    private TkWriteTransaction Writable()
    {
        if (_transaction is not TkWriteTransaction writer || _state is null)
        {
            throw new InvalidOperationException("Writing needs a write transaction.");
        }

        return ReadOnlyReason is null
            ? writer
            : throw new TkSqlException($"Table '{Name}' cannot be written through SQL: {ReadOnlyReason}. Write to it through its row type.");
    }

    private async ValueTask WriteAsync(TkWriteTransaction writer, object?[]? old, object?[] row, byte[] key, CancellationToken cancellationToken)
    {
        for (var i = 0; i < Columns.Length; i++)
        {
            if (row[i] is null && !Columns[i].IsNullable)
            {
                throw new TkSqlException($"Column '{Columns[i].Name}' of table '{Name}' cannot be NULL.");
            }
        }

        var encoded = SqlValues.EncodeRow(Version, Columns, row);
        var changes = Changes(old, row, key);

        // FOREIGN KEY, before anything changes: the row each one refers to must be there.
        foreach (var (foreignKey, column) in ForeignKeys)
        {
            if (row[column] is { } value && !await RefersToARowAsync(writer, foreignKey, value, Columns[column].Type, key, cancellationToken).ConfigureAwait(false))
            {
                writer.Fail();
                throw new TkConstraintException(
                    $"Table '{Name}': the row with key {SqlValues.Show(row[KeyColumn])} violates FOREIGN KEY constraint '{foreignKey.Name}': no row of '{foreignKey.Table}' has the key it refers to. The transaction is aborted.",
                    Name,
                    foreignKey.Name,
                    SqlValues.Show(row[KeyColumn]));
            }
        }

        // UNIQUE, before anything changes: another row with the same values in a unique index.
        foreach (var (index, _, entry, prefix, hasNull) in changes)
        {
            if (index.IsUnique && entry is not null && !hasNull
                && await TakenAsync(writer.Inner, index.CurrentRoot, entry, prefix, cancellationToken).ConfigureAwait(false))
            {
                writer.Fail();
                throw new TkConstraintException(
                    $"Table '{Name}': the row with key {SqlValues.Show(row[KeyColumn])} violates UNIQUE constraint '{index.Name}': another row has the same values. The transaction is aborted.",
                    Name,
                    index.Name,
                    SqlValues.Show(row[KeyColumn]));
            }
        }

        try
        {
            await ApplyAsync(writer, changes, cancellationToken).ConfigureAwait(false);
            _state!.Root = await _transaction.Database.Tree.PutAsync(writer.Inner, Root, key, encoded, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A row and its index entries are several trees: past this point they may not agree.
            writer.Fail();
            throw;
        }

        _state.Changed = true;
    }

    /// <summary>
    /// Whether the table a FOREIGN KEY refers to has a row whose key is a value. A row may refer to
    /// itself, also while it is being inserted: <paramref name="own"/> is the key of the row asking.
    /// </summary>
    public async ValueTask<bool> RefersToARowAsync(
        TkWriteTransaction writer, ForeignKeyInfo foreignKey, object value, TkColumnType type, byte[]? own, CancellationToken cancellationToken)
    {
        byte[] key;
        try
        {
            key = SqlValues.PrimaryKey(value, type);
        }
        catch (TkSqlException)
        {
            // Too long to be a key, or empty: nothing in the other table can have it.
            return false;
        }

        var parent = await writer.TableAsync(foreignKey.Table, CatalogStore.TableKey(foreignKey.Table), cancellationToken).ConfigureAwait(false);
        return (ReferenceEquals(parent, _state) && own is not null && key.AsSpan().SequenceEqual(own))
            || await BTree.GetAsync(writer.Inner, parent.Root, key, Memory<byte>.Empty, cancellationToken).ConfigureAwait(false) != BTree.NotFound;
    }

    /// <summary>Refuses to delete a row while rows of this or another table refer to it through a FOREIGN KEY.</summary>
    private async ValueTask EnsureNotReferredAsync(TkWriteTransaction writer, object?[] old, byte[] key, CancellationToken cancellationToken)
    {
        var referrers = await writer.ReferrersAsync(_state!, cancellationToken).ConfigureAwait(false);
        if (referrers.Count == 0)
        {
            return;
        }

        var buffer = new byte[_transaction.Database.Tree.MaxKeySize];
        foreach (var referrer in referrers)
        {
            var referring = await writer.TableAsync(referrer.Table, CatalogStore.TableKey(referrer.Table), cancellationToken).ConfigureAwait(false);
            var indexes = await writer.IndexesAsync(referring, cancellationToken).ConfigureAwait(false);
            if (!indexes.TryGetValue(referrer.Index, out var index))
            {
                throw new InvalidDataException($"Table '{referrer.Table}' lacks the index of its FOREIGN KEY '{referrer.Name}' to table '{Name}'.");
            }

            // The entries of the rows that refer to this one start with its key, as an index holds it.
            var probe = buffer.AsMemory(0, ReferenceProbe(key, referrer.IsNullable, buffer));
            await foreach (var entry in BTree.RangeAsync(writer.Inner, index.Root, probe, After(probe.ToArray()), cancellationToken).ConfigureAwait(false))
            {
                // A row that refers to itself does not keep itself from being deleted.
                if (ReferenceEquals(referring, _state)
                    && entry.Value.Length == sizeof(ushort)
                    && BinaryPrimitives.ReadUInt16LittleEndian(entry.Value.Span) is var prefix
                    && prefix <= entry.Key.Length
                    && entry.Key.Span[prefix..].SequenceEqual(key))
                {
                    continue;
                }

                writer.Fail();
                var text = SqlValues.Show(old[KeyColumn]);
                throw new TkConstraintException(
                    $"Table '{referrer.Table}': FOREIGN KEY constraint '{referrer.Name}' is violated: the row of '{Name}' with key {text} cannot be deleted while rows of '{referrer.Table}' refer to it. The transaction is aborted.",
                    referrer.Table,
                    referrer.Name,
                    text);
            }
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

        if (Columns[KeyColumn].Type == TkColumnType.Text)
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

    /// <summary>For each index, the entry the old row has in it and the one the new row gets: null when the row is not in the index.</summary>
    private List<(StoredIndex Index, byte[]? Old, byte[]? New, int Prefix, bool HasNull)> Changes(object?[]? old, object?[]? row, byte[] key)
    {
        var changes = new List<(StoredIndex, byte[]?, byte[]?, int, bool)>(Indexes.Count);
        foreach (var index in Indexes)
        {
            var before = old is null ? null : Entry(index, old, key, out _, out _);
            var prefix = 0;
            var hasNull = false;
            var after = row is null ? null : Entry(index, row, key, out prefix, out hasNull);
            changes.Add((index, before, after, prefix, hasNull));
        }

        return changes;
    }

    /// <summary>The entry of a row in an index: its indexed values and its primary key. Null if a partial index leaves the row out.</summary>
    private byte[]? Entry(StoredIndex index, object?[] row, byte[] key, out int prefix, out bool hasNull)
    {
        prefix = 0;
        hasNull = false;
        Span<byte> scratch = stackalloc byte[512];
        foreach (var (column, type, nullable, range) in index.Where)
        {
            var field = new TkKeyWriter(scratch);
            SqlValues.WriteField(ref field, row[column], type, nullable);
            if (!range.Contains(scratch[..field.Length]))
            {
                return null;
            }
        }

        var limit = _transaction.Database.Tree.MaxKeySize;
        var buffer = new byte[limit];
        var writer = new TkKeyWriter(buffer);
        for (var i = 0; i < index.Columns.Length; i++)
        {
            hasNull |= index.Nullable[i] && row[index.Columns[i]] is null;
            SqlValues.WriteField(ref writer, row[index.Columns[i]], index.Types[i], index.Nullable[i]);
        }

        prefix = writer.Length;
        writer.WriteRaw(key);
        return writer.IsTruncated
            ? throw new TkIndexKeyTooLargeException(
                $"Index '{index.Name}' of table '{Name}': the indexed values of the row and its key take more than {limit} bytes, the most an index entry may.")
            : buffer.AsSpan(0, writer.Length).ToArray();
    }

    private async ValueTask ApplyAsync(
        TkWriteTransaction writer, List<(StoredIndex Index, byte[]? Old, byte[]? New, int Prefix, bool HasNull)> changes, CancellationToken cancellationToken)
    {
        var tree = _transaction.Database.Tree;
        var value = new byte[sizeof(ushort)];
        foreach (var (index, old, replacement, prefix, _) in changes)
        {
            if (old is not null && replacement is not null && old.AsSpan().SequenceEqual(replacement))
            {
                continue;
            }

            var entry = index.Entry!;
            if (old is not null)
            {
                var (removed, root) = await tree.DeleteAsync(writer.Inner, entry.Root, old, cancellationToken).ConfigureAwait(false);
                if (!removed)
                {
                    throw new InvalidDataException($"Index '{index.Name}' of table '{Name}' lacks the entry of a row.");
                }

                entry.Root = root;
                entry.Dirty = true;
            }

            if (replacement is not null)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(value, (ushort)prefix);
                entry.Root = await tree.PutAsync(writer.Inner, entry.Root, replacement, value.ToArray(), cancellationToken).ConfigureAwait(false);
                entry.Dirty = true;
            }
        }
    }

    /// <summary>Whether a unique index already has an entry with the indexed values of this one, for another row.</summary>
    private static async ValueTask<bool> TakenAsync(IPageSource source, PageNumber root, byte[] entry, int prefix, CancellationToken cancellationToken)
    {
        var values = entry.AsMemory(0, prefix);
        await foreach (var existing in BTree.RangeAsync(source, root, values, After(values.ToArray()), cancellationToken).ConfigureAwait(false))
        {
            return !existing.Key.Span.SequenceEqual(entry);
        }

        return false;
    }

    private async ValueTask<object?[]> DecodeAsync(ReadOnlyMemory<byte> row, CancellationToken cancellationToken)
    {
        if (row.IsEmpty || row.Span[0] == 0)
        {
            throw new InvalidDataException($"Table '{Name}': a stored row has no schema version.");
        }

        var version = row.Span[0];
        if (!_versions.TryGetValue(version, out var plan))
        {
            var stored = version == Version
                ? Columns
                : (await CatalogStore.LoadSchemaAsync(Source, _transaction.CatalogRoot, TableKey, version, cancellationToken).ConfigureAwait(false)).Columns;
            plan = (stored, [.. Enumerable.Range(0, stored.Length)], [.. stored.Select(column => Column(column.Name))]);
            _versions.Add(version, plan);
        }

        var values = SqlValues.DecodeRow(row.Span, plan.Stored, plan.Identity);
        if (version == Version)
        {
            return values;
        }

        // Written with other columns: each value goes to the column of the same name, if it is still there.
        // A column the row never had reads as the typed API reads it: NULL, or the zero of its type if it cannot be NULL.
        var current = new object?[Columns.Length];
        for (var i = 0; i < current.Length; i++)
        {
            current[i] = Columns[i].IsNullable ? null : SqlValues.Zero(Columns[i].Type);
        }

        for (var i = 0; i < values.Length; i++)
        {
            var target = plan.ToCurrent[i];
            if (target >= 0 && values[i] is { } value)
            {
                current[target] = Columns[target].Type == plan.Stored[i].Type ? value : SqlValues.Convert(value, Columns[target].Type, Columns[target].Name);
            }
        }

        return current;
    }
}
