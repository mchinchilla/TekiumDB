// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Tekium.Embedded.Indexing;

namespace Tekium.Embedded.Catalog;

/// <summary>
/// One secondary index of a table as a row type declares it: which fields make its key, and for a
/// partial index, which rows are in it.
/// </summary>
/// <remarks>
/// <see cref="Encoded"/> is what the catalog stores, and it is file format. Two definitions are the
/// same index exactly when those bytes are equal: an index whose stored definition differs from the
/// declared one is never read, and is rebuilt by the next write. Layout: a format byte (2 if the index is unique); the number of
/// fields and each field; the number of conditions and, for each, its field, a byte of flags (1 has a
/// low bound, 2 it is inclusive, 4 has a high bound, 8 it is inclusive) and the two bounds, each with a
/// two-byte length. A field is its type, whether it is nullable, whether it is an expression, its name
/// with a one-byte length, and the text of the expression with a four-byte length.
/// </remarks>
internal sealed class IndexDefinition
{
    private const byte Format = 1;

    // A unique index is another format: one that was not unique does not become so by being read as such.
    private const byte UniqueFormat = 2;

    private IndexDefinition(string name, int[] fields, bool[] nullable, FilterTerm[] where, bool isUnique, byte[] catalogKey, byte[] encoded)
    {
        Nullable = nullable;
        IsUnique = isUnique;
        Name = name;
        Fields = fields;
        Where = where;
        CatalogKey = catalogKey;
        Encoded = encoded;
    }

    public string Name { get; }

    /// <summary>Gets the fields of the key, most significant first.</summary>
    public int[] Fields { get; }

    /// <summary>Gets the conditions a row meets to be in the index, one per field. Empty if every row is.</summary>
    public FilterTerm[] Where { get; }

    /// <summary>Gets, for each field of the key, whether it may have no value.</summary>
    public bool[] Nullable { get; }

    /// <summary>Gets a value indicating whether two rows may not have the same values in the index.</summary>
    public bool IsUnique { get; }

    public byte[] CatalogKey { get; }

    public byte[] Encoded { get; }

    /// <summary>The indexes a row type declares, checked.</summary>
    public static IndexDefinition[] Build<T>(string table, ReadOnlySpan<byte> tableKey)
        where T : class, ITkRow<T>
    {
        var declared = T.Indexes;
        var fieldCount = T.Columns.Length + T.Expressions.Length;
        var definitions = new IndexDefinition[declared.Length];
        for (var i = 0; i < declared.Length; i++)
        {
            var index = declared[i];
            byte[] catalogKey;
            try
            {
                catalogKey = CatalogStore.IndexKey(tableKey, index.Name);
            }
            catch (ArgumentException invalid)
            {
                throw new TkSchemaException($"Table '{table}': index '{index.Name}' cannot have that name. {invalid.Message}", invalid);
            }

            var fields = index.Fields.ToArray();
            var where = index.Where?.Normalize() ?? [];
            Array.Sort(where, static (a, b) => a.Field.CompareTo(b.Field));
            if (fields.Length is 0 or > byte.MaxValue
                || where.Length > byte.MaxValue
                || fields.Any(field => (uint)field >= (uint)fieldCount)
                || where.Any(term => (uint)term.Field >= (uint)fieldCount)
                || fields.Distinct().Count() != fields.Length)
            {
                throw new TkSchemaException(
                    $"Table '{table}': index '{index.Name}' must name between 1 and {byte.MaxValue} fields of the table, each once.");
            }

            for (var j = 0; j < i; j++)
            {
                if (string.Equals(definitions[j].Name, index.Name, StringComparison.Ordinal))
                {
                    throw new TkSchemaException($"Table '{table}' declares index '{index.Name}' twice.");
                }
            }

            var nullable = Array.ConvertAll(fields, static field => IsNullable<T>(field));
            definitions[i] = new IndexDefinition(
                index.Name, fields, nullable, where, index.IsUnique, catalogKey, Encode<T>(fields, where, index.IsUnique));
        }

        return definitions;
    }

    /// <summary>
    /// Room to write a field in before comparing it with these conditions: one byte more than the longest
    /// bound, which is as much of the field as the comparison can depend on.
    /// </summary>
    public static int ScratchSize(ReadOnlySpan<FilterTerm> terms)
    {
        var longest = 0;
        foreach (var term in terms)
        {
            longest = Math.Max(longest, Math.Max(term.Range.Low?.Length ?? 0, term.Range.High?.Length ?? 0));
        }

        return longest + 1;
    }

    private static bool IsNullable<T>(int field)
        where T : class, ITkRow<T> =>
        field < T.Columns.Length ? T.Columns[field].IsNullable : T.Expressions[field - T.Columns.Length].IsNullable;

    private static byte[] Encode<T>(int[] fields, FilterTerm[] where, bool isUnique)
        where T : class, ITkRow<T>
    {
        var encoded = new ArrayBufferWriter<byte>();
        Write(encoded, isUnique ? UniqueFormat : Format);
        Write(encoded, (byte)fields.Length);
        foreach (var field in fields)
        {
            WriteField<T>(encoded, field);
        }

        Write(encoded, (byte)where.Length);
        foreach (var term in where)
        {
            WriteField<T>(encoded, term.Field);
            var range = term.Range;
            Write(encoded, (byte)(
                (range.Low is null ? 0 : 1) | (range.LowInclusive ? 2 : 0) | (range.High is null ? 0 : 4) | (range.HighInclusive ? 8 : 0)));
            WriteBound(encoded, range.Low);
            WriteBound(encoded, range.High);
        }

        return encoded.WrittenSpan.ToArray();
    }

    private static void WriteField<T>(ArrayBufferWriter<byte> encoded, int field)
        where T : class, ITkRow<T>
    {
        var columns = T.Columns;
        var isExpression = field >= columns.Length;
        string name;
        string text;
        TkColumnType type;
        bool isNullable;
        if (isExpression)
        {
            var expression = T.Expressions[field - columns.Length];
            (name, type, isNullable, text) = (expression.Name, expression.Type, expression.IsNullable, expression.Text);
        }
        else
        {
            var column = columns[field];
            (name, type, isNullable, text) = (column.Name, column.Type, column.IsNullable, string.Empty);
        }

        Write(encoded, (byte)type);
        Write(encoded, isNullable ? (byte)1 : (byte)0);
        Write(encoded, isExpression ? (byte)1 : (byte)0);

        // A column name is at most 128 bytes, which a column checks when it is built.
        Write(encoded, (byte)Encoding.UTF8.GetByteCount(name));
        Encoding.UTF8.GetBytes(name, encoded);
        BinaryPrimitives.WriteInt32LittleEndian(encoded.GetSpan(sizeof(int)), Encoding.UTF8.GetByteCount(text));
        encoded.Advance(sizeof(int));
        Encoding.UTF8.GetBytes(text, encoded);
    }

    private static void WriteBound(ArrayBufferWriter<byte> encoded, byte[]? bound)
    {
        var length = bound?.Length ?? 0;
        if (length > ushort.MaxValue)
        {
            throw new TkSchemaException("The condition of a partial index compares with a value that is too long.");
        }

        BinaryPrimitives.WriteUInt16LittleEndian(encoded.GetSpan(sizeof(ushort)), (ushort)length);
        encoded.Advance(sizeof(ushort));
        encoded.Write(bound);
    }

    private static void Write(ArrayBufferWriter<byte> encoded, byte value)
    {
        encoded.GetSpan(1)[0] = value;
        encoded.Advance(1);
    }
}
