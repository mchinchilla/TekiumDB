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
/// the typed API would: plain, unique and partial indexes over columns. A table with indexes over
/// expressions, zone maps, CHECK or FOREIGN KEY constraints is read-only here.
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
        var referrers = await CatalogStore.ReadAsync(source, catalog, CatalogStore.ReferrersKey(tableKey), cancellationToken).ConfigureAwait(false);
        table.ReadOnlyReason =
            table.Indexes.Find(static index => index.Unmaintainable is not null) is { } index0 ? $"its index '{index0.Name}' cannot be kept from SQL: {index0.Unmaintainable}"
            : constraints.Length > 0 && ConstraintSet.Decode(constraints) is { } set && (set.Checks.Length > 0 || set.ForeignKeys.Length > 0) ? "it has CHECK or FOREIGN KEY constraints, which only the row type that declares them can check"
            : referrers.Length > 0 && ConstraintSet.DecodeReferrers(referrers).Count > 0 ? "other tables refer to it with FOREIGN KEY constraints"
            : null;
        return table;
    }

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
