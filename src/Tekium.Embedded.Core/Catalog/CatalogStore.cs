// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers;
using System.Text;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Catalog;

/// <summary>
/// The catalog is the tree the root slot points to. It maps a table name to where its rows are, and a
/// table name and version to the columns of that version. Being a tree like any other, it is versioned
/// by copy-on-write with everything else: a snapshot sees the tables and schemas of its own commit.
/// </summary>
internal static class CatalogStore
{
    /// <summary>The longest table name, in UTF-8 bytes. Bounded by the smallest key a tree accepts.</summary>
    public const int MaxNameBytes = 128;

    private const byte TablePrefix = 1;
    private const byte SchemaPrefix = 2;

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

    /// <summary>Whether a catalog key is a table entry, as opposed to a schema.</summary>
    public static bool IsTableKey(ReadOnlySpan<byte> key) => key.Length > 1 && key[0] == TablePrefix;

    public static async ValueTask<TableInfo?> FindTableAsync(
        IPageSource source, PageNumber catalogRoot, ReadOnlyMemory<byte> tableKey, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(TableInfo.EncodedSize);
        try
        {
            var length = await BTree.GetAsync(source, catalogRoot, tableKey, buffer.AsMemory(0, TableInfo.EncodedSize), cancellationToken)
                .ConfigureAwait(false);
            return length == BTree.NotFound ? null : TableInfo.Decode(buffer.AsSpan(0, length));
        }
        catch (ArgumentException)
        {
            // The entry is longer than a table entry can be.
            throw new InvalidDataException("A table entry in the catalog is damaged.");
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
        int maxEntrySize,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(maxEntrySize);
        try
        {
            var length = await BTree.GetAsync(
                source, catalogRoot, SchemaKey(tableKey.Span, version), buffer.AsMemory(0, maxEntrySize), cancellationToken)
                .ConfigureAwait(false);
            return length == BTree.NotFound
                ? throw new InvalidDataException($"A row was written with schema version {version}, which the catalog does not have.")
                : TableSchema.Decode(version, buffer.AsSpan(0, length));
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
