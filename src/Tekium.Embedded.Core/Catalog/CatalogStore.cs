// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Catalog;

/// <summary>
/// The catalog is the tree the root slot points to. It maps a table name to where its rows are, a
/// table name and version to the columns of that version, and a table and index name to that index. Being a tree like any other, it is versioned
/// by copy-on-write with everything else: a snapshot sees the tables and schemas of its own commit.
/// </summary>
internal static class CatalogStore
{
    /// <summary>The longest table name, in UTF-8 bytes. Bounded by the smallest key a tree accepts.</summary>
    public const int MaxNameBytes = 128;

    /// <summary>The longest index name, in UTF-8 bytes: with the table name before it, it must fit in the smallest key.</summary>
    public const int MaxIndexNameBytes = 96;

    private const byte TablePrefix = 1;
    private const byte SchemaPrefix = 2;
    private const byte IndexPrefix = 3;
    private const byte ConstraintsPrefix = 4;
    private const byte ReferrersPrefix = 5;
    private const byte IndexFormat = 1;
    private const int IndexHeaderSize = 5;
    private const int ExpectedSchemaSize = 1024;

    /// <summary>The key of the entry that says where the rows of a table are.</summary>
    public static byte[] TableKey(string name)
    {
        var length = ValidatedNameLength(name);
        var key = new byte[1 + length];
        key[0] = TablePrefix;
        Encoding.UTF8.GetBytes(name, key.AsSpan(1));
        return key;
    }

    /// <summary>The key of the entry that holds the columns of one version: the table key, a zero, and the version.</summary>
    public static byte[] SchemaKey(ReadOnlySpan<byte> tableKey, byte version)
    {
        var key = new byte[tableKey.Length + 2];
        tableKey.CopyTo(key);
        key[0] = SchemaPrefix;
        key[^1] = version;
        return key;
    }

    /// <summary>Whether a catalog key is a table entry.</summary>
    public static bool IsTableKey(ReadOnlySpan<byte> key) => key.Length > 1 && key[0] == TablePrefix;

    /// <summary>Whether a catalog key is the schema of one version of a table.</summary>
    public static bool IsSchemaKey(ReadOnlySpan<byte> key) => key.Length > 1 && key[0] == SchemaPrefix;

    /// <summary>Whether a catalog key is an index entry.</summary>
    public static bool IsIndexKey(ReadOnlySpan<byte> key) => key.Length > 1 && key[0] == IndexPrefix;

    /// <summary>The key of the entry that holds the CHECK and FOREIGN KEY constraints of a table.</summary>
    public static byte[] ConstraintsKey(ReadOnlySpan<byte> tableKey)
    {
        var key = tableKey.ToArray();
        key[0] = ConstraintsPrefix;
        return key;
    }

    /// <summary>The key of the entry that lists the foreign keys of other tables that refer to a table.</summary>
    public static byte[] ReferrersKey(ReadOnlySpan<byte> tableKey)
    {
        var key = tableKey.ToArray();
        key[0] = ReferrersPrefix;
        return key;
    }

    /// <summary>Reads a whole catalog entry, whatever its size. Empty if there is none.</summary>
    public static async ValueTask<byte[]> ReadAsync(
        IPageSource source, PageNumber catalogRoot, ReadOnlyMemory<byte> key, CancellationToken cancellationToken)
    {
        var (buffer, length) = await BTree.GetRentedAsync(source, catalogRoot, key, ExpectedSchemaSize, cancellationToken)
            .ConfigureAwait(false);
        if (buffer is null)
        {
            return [];
        }

        try
        {
            return buffer.AsSpan(0, length).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>What the key of every index of a table starts with: the table key and a zero.</summary>
    public static byte[] IndexKeyPrefix(ReadOnlySpan<byte> tableKey)
    {
        var key = new byte[tableKey.Length + 1];
        tableKey.CopyTo(key);
        key[0] = IndexPrefix;
        return key;
    }

    /// <summary>The key of the entry of an index: the table key, a zero, and the name of the index.</summary>
    public static byte[] IndexKey(ReadOnlySpan<byte> tableKey, string indexName)
    {
        ArgumentException.ThrowIfNullOrEmpty(indexName);
        var length = Encoding.UTF8.GetByteCount(indexName);
        if (length > MaxIndexNameBytes || indexName.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"An index name must not exceed {MaxIndexNameBytes} UTF-8 bytes nor contain a null character.", nameof(indexName));
        }

        var key = new byte[tableKey.Length + 1 + length];
        tableKey.CopyTo(key);
        key[0] = IndexPrefix;
        Encoding.UTF8.GetBytes(indexName, key.AsSpan(tableKey.Length + 1));
        return key;
    }

    /// <summary>An index entry: where its tree is, and after that, what it indexes.</summary>
    public static byte[] EncodeIndex(PageNumber root, ReadOnlySpan<byte> definition)
    {
        var entry = new byte[IndexHeaderSize + definition.Length];
        entry[0] = IndexFormat;
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(1), root.Value);
        definition.CopyTo(entry.AsSpan(IndexHeaderSize));
        return entry;
    }

    /// <summary>The root of the tree of an index, from its entry.</summary>
    public static PageNumber IndexRoot(ReadOnlySpan<byte> entry) =>
        entry.Length > IndexHeaderSize && entry[0] == IndexFormat
            ? new PageNumber(BinaryPrimitives.ReadUInt32LittleEndian(entry[1..]))
            : throw new InvalidDataException("An index entry in the catalog is damaged.");

    /// <summary>What an index indexes, from its entry: the bytes two definitions are compared by.</summary>
    public static ReadOnlySpan<byte> IndexDefinition(ReadOnlySpan<byte> entry) =>
        entry.Length > IndexHeaderSize && entry[0] == IndexFormat
            ? entry[IndexHeaderSize..]
            : throw new InvalidDataException("An index entry in the catalog is damaged.");

    /// <summary>
    /// Looks for an index with exactly this definition. One with the same name that indexes something
    /// else is not found: its entries would not be the ones the caller expects.
    /// </summary>
    public static async ValueTask<(bool Found, PageNumber Root)> FindIndexAsync(
        IPageSource source,
        PageNumber catalogRoot,
        ReadOnlyMemory<byte> indexKey,
        ReadOnlyMemory<byte> definition,
        CancellationToken cancellationToken)
    {
        var (buffer, length) = await BTree.GetRentedAsync(
            source, catalogRoot, indexKey, IndexHeaderSize + definition.Length, cancellationToken)
            .ConfigureAwait(false);
        if (buffer is null)
        {
            return (false, PageNumber.None);
        }

        try
        {
            var entry = buffer.AsSpan(0, length);
            return IndexDefinition(entry).SequenceEqual(definition.Span) ? (true, IndexRoot(entry)) : (false, PageNumber.None);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async ValueTask<TableInfo?> FindTableAsync(
        IPageSource source, PageNumber catalogRoot, ReadOnlyMemory<byte> tableKey, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(TableInfo.EncodedSize);
        try
        {
            var length = await BTree.GetAsync(source, catalogRoot, tableKey, buffer.AsMemory(0, TableInfo.EncodedSize), cancellationToken)
                .ConfigureAwait(false);
            if (length == BTree.NotFound)
            {
                return null;
            }

            // Longer than a table entry can be: the read says so instead of a length.
            return length >= 0
                ? TableInfo.Decode(buffer.AsSpan(0, length))
                : throw new InvalidDataException("A table entry in the catalog is damaged.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async ValueTask<TableSchema> LoadSchemaAsync(
        IPageSource source,
        PageNumber catalogRoot,
        ReadOnlyMemory<byte> tableKey,
        byte version,
        CancellationToken cancellationToken)
    {
        // A schema with many columns does not fit in a leaf; the tree stores it in overflow pages.
        var (buffer, length) = await BTree.GetRentedAsync(
            source, catalogRoot, SchemaKey(tableKey.Span, version), ExpectedSchemaSize, cancellationToken)
            .ConfigureAwait(false);
        if (buffer is null)
        {
            throw new InvalidDataException(
                $"A row was written with schema version {version}, which the catalog does not have.");
        }

        try
        {
            return TableSchema.Decode(version, buffer.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int ValidatedNameLength(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var length = Encoding.UTF8.GetByteCount(name);
        if (length > MaxNameBytes || name.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"A table name must not exceed {MaxNameBytes} UTF-8 bytes nor contain a null character.", nameof(name));
        }

        return length;
    }
}
