// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using TekiumDB.Catalog;
using TekiumDB.Indexing;
using TekiumDB.Storage;

namespace TekiumDB.Sql.Storage;

/// <summary>
/// An index of a table as the catalog describes it, read without the row type that declared it. The
/// definition bytes are those of <see cref="IndexDefinition"/>: an index created through SQL over plain
/// columns is, byte for byte, the one a <c>[TkIndex]</c> on the same columns declares.
/// </summary>
internal sealed class StoredIndex
{
    private const byte PlainFormat = 1;
    private const byte UniqueFormat = 2;
    private const byte ZoneFormat = 3;

    /// <summary>The kind of field that is the value at a path of a JSON column, as <c>IndexDefinition</c> writes it.</summary>
    private const byte PathField = 2;

    private StoredIndex(string name, byte[] definition)
    {
        Name = name;
        Definition = definition;
    }

    public string Name { get; }

    public byte[] Definition { get; }

    public PageNumber Root { get; set; }

    /// <summary>Gets or sets the index as a write transaction holds it: where it is now, and whether it changed.</summary>
    public TkWriteTransaction.IndexEntry? Entry { get; set; }

    /// <summary>Gets where the entries are now.</summary>
    public PageNumber CurrentRoot => Entry?.Root ?? Root;

    public bool IsUnique { get; private set; }

    public bool IsZoneMap { get; private set; }

    /// <summary>Gets the columns of the key, most significant first, as positions in the current schema; -1 for an expression or a column that is gone.</summary>
    public int[] Columns { get; private set; } = [];

    public TkColumnType[] Types { get; private set; } = [];

    public bool[] Nullable { get; private set; } = [];

    /// <summary>Gets, for each field, the path of its JSON column it indexes the value at; null for a column indexed as it is.</summary>
    public TkJsonPath?[] Paths { get; private set; } = [];

    /// <summary>Gets the conditions of a partial index: a column of the current schema, how it is encoded, and the range it must be in.</summary>
    public (int Column, TkColumnType Type, bool Nullable, KeyRange Range)[] Where { get; private set; } = [];

    /// <summary>Gets why the SQL layer cannot keep this index up to date, or null if it can.</summary>
    public string? Unmaintainable { get; private set; }

    /// <summary>The definition of an index over plain columns.</summary>
    public static StoredIndex Create(string name, ReadOnlySpan<TkColumn> columns, int[] fields, bool unique, (TkJsonPath Path, TkColumnType Type)?[]? paths = null)
    {
        var encoded = new ArrayBufferWriter<byte>();
        Put(encoded, unique ? UniqueFormat : PlainFormat);
        Put(encoded, (byte)fields.Length);
        for (var i = 0; i < fields.Length; i++)
        {
            // As the row type writes it: a value at a path is named after its column, nullable, with the canonical path as its text.
            var column = columns[fields[i]];
            var path = paths?[i];
            var text = path?.Path.Text ?? string.Empty;
            Put(encoded, (byte)(path?.Type ?? column.Type));
            Put(encoded, path is not null || column.IsNullable ? (byte)1 : (byte)0);
            Put(encoded, path is null ? (byte)0 : PathField);
            Put(encoded, (byte)Encoding.UTF8.GetByteCount(column.Name));
            Encoding.UTF8.GetBytes(column.Name, encoded);
            BinaryPrimitives.WriteInt32LittleEndian(encoded.GetSpan(sizeof(int)), Encoding.UTF8.GetByteCount(text));
            encoded.Advance(sizeof(int));
            Encoding.UTF8.GetBytes(text, encoded);
        }

        Put(encoded, 0);
        return Decode(name, encoded.WrittenSpan.ToArray(), columns);

        static void Put(ArrayBufferWriter<byte> writer, byte value)
        {
            writer.GetSpan(1)[0] = value;
            writer.Advance(1);
        }
    }

    /// <summary>
    /// The same definition with some of its columns changed: each field, and each condition of a partial
    /// index, naming a column in <paramref name="changes"/> gets that name, type and nullability. Everything
    /// else is copied as it is.
    /// </summary>
    public static byte[] Redefine(byte[] definition, IReadOnlyDictionary<string, TkColumn> changes)
    {
        var copy = new ArrayBufferWriter<byte>(definition.Length + 16);
        var position = 0;
        var format = definition[position++];
        Put(copy, format);
        if (format == ZoneFormat)
        {
            copy.Write(definition.AsSpan(position, sizeof(int)));
            position += sizeof(int);
        }

        var count = definition[position++];
        Put(copy, count);
        for (var i = 0; i < count; i++)
        {
            CopyField(definition, ref position, copy, changes);
        }

        var conditions = definition[position++];
        Put(copy, conditions);
        for (var i = 0; i < conditions; i++)
        {
            CopyField(definition, ref position, copy, changes);
            var start = position;
            position++;
            position += sizeof(ushort) + BinaryPrimitives.ReadUInt16LittleEndian(definition.AsSpan(position));
            position += sizeof(ushort) + BinaryPrimitives.ReadUInt16LittleEndian(definition.AsSpan(position));
            copy.Write(definition.AsSpan(start, position - start));
        }

        copy.Write(definition.AsSpan(position));
        return copy.WrittenSpan.ToArray();

        static void CopyField(byte[] definition, ref int position, ArrayBufferWriter<byte> copy, IReadOnlyDictionary<string, TkColumn> changes)
        {
            var type = definition[position];
            var nullable = definition[position + 1];
            var isExpression = definition[position + 2];
            var nameLength = definition[position + 3];
            var name = Encoding.UTF8.GetString(definition, position + 4, nameLength);
            var afterName = position + 4 + nameLength;
            var end = afterName + sizeof(int) + BinaryPrimitives.ReadInt32LittleEndian(definition.AsSpan(afterName));
            if (isExpression is 0 or PathField && changes.TryGetValue(name, out var column))
            {
                // A value at a path keeps its kind: only the name of its column changes.
                Put(copy, isExpression == PathField ? type : (byte)column.Type);
                Put(copy, isExpression == PathField ? nullable : column.IsNullable ? (byte)1 : (byte)0);
                Put(copy, isExpression);
                Put(copy, (byte)Encoding.UTF8.GetByteCount(column.Name));
                Encoding.UTF8.GetBytes(column.Name, copy);
                copy.Write(definition.AsSpan(afterName, end - afterName));
            }
            else
            {
                copy.Write(definition.AsSpan(position, end - position));
            }

            position = end;
        }

        static void Put(ArrayBufferWriter<byte> writer, byte value)
        {
            writer.GetSpan(1)[0] = value;
            writer.Advance(1);
        }
    }

    public static StoredIndex Decode(string name, byte[] definition, ReadOnlySpan<TkColumn> columns)
    {
        var index = new StoredIndex(name, definition);
        try
        {
            var position = 0;
            var format = definition[position++];
            index.IsUnique = format == UniqueFormat;
            index.IsZoneMap = format == ZoneFormat;
            if (format is not (PlainFormat or UniqueFormat or ZoneFormat))
            {
                index.Unmaintainable = "it is of a kind this version does not know";
                return index;
            }

            if (index.IsZoneMap)
            {
                position += sizeof(int);
                index.Unmaintainable = "it is a zone map";
            }

            var count = definition[position++];
            index.Columns = new int[count];
            index.Types = new TkColumnType[count];
            index.Nullable = new bool[count];
            index.Paths = new TkJsonPath?[count];
            for (var i = 0; i < count; i++)
            {
                (index.Columns[i], index.Types[i], index.Nullable[i], index.Paths[i]) = Field(definition, ref position, columns, index);
            }

            var conditions = definition[position++];
            index.Where = new (int, TkColumnType, bool, KeyRange)[conditions];
            for (var i = 0; i < conditions; i++)
            {
                var (column, type, nullable, path) = Field(definition, ref position, columns, index);
                if (path is not null)
                {
                    index.Unmaintainable ??= "the condition of a partial index is on a value at a path, which this version does not keep";
                }

                var flags = definition[position++];
                var low = Bound(definition, ref position);
                var high = Bound(definition, ref position);
                index.Where[i] = (column, type, nullable, new KeyRange((flags & 1) != 0 ? low : null, (flags & 2) != 0, (flags & 4) != 0 ? high : null, (flags & 8) != 0));
            }
        }
        catch (Exception exception) when (exception is IndexOutOfRangeException or ArgumentException)
        {
            throw new InvalidDataException($"The definition of index '{name}' in the catalog is damaged.", exception);
        }

        return index;
    }

    private static (int Column, TkColumnType Type, bool Nullable, TkJsonPath? Path) Field(byte[] definition, ref int position, ReadOnlySpan<TkColumn> columns, StoredIndex index)
    {
        var type = (TkColumnType)definition[position++];
        var nullable = definition[position++] != 0;
        var kind = definition[position++];
        var nameLength = definition[position++];
        var name = Encoding.UTF8.GetString(definition, position, nameLength);
        position += nameLength;
        var textLength = BinaryPrimitives.ReadInt32LittleEndian(definition.AsSpan(position));
        var text = Encoding.UTF8.GetString(definition, position + sizeof(int), textLength);
        position += sizeof(int) + textLength;
        if (kind == PathField)
        {
            // A value at a path: its column must still be a JSON column, and the path one SQL reads the same.
            for (var i = 0; i < columns.Length; i++)
            {
                if (string.Equals(columns[i].Name, name, StringComparison.Ordinal) && columns[i].Type is TkColumnType.Json or TkColumnType.Jsonb
                    && type is TkColumnType.Text or TkColumnType.F64 && nullable && TkJsonPath.TryParse(text, out var path) && path.Text == text)
                {
                    return (i, type, nullable, path);
                }
            }

            index.Unmaintainable ??= $"it indexes a value at a path of column '{name}', which the table no longer has as a JSON column";
            return (-1, type, nullable, null);
        }

        if (kind != 0)
        {
            index.Unmaintainable ??= $"it indexes the expression '{name}', which only the row type that declares it can compute";
            return (-1, type, nullable, null);
        }

        for (var i = 0; i < columns.Length; i++)
        {
            if (string.Equals(columns[i].Name, name, StringComparison.Ordinal) && columns[i].Type == type && columns[i].IsNullable == nullable)
            {
                return (i, type, nullable, null);
            }
        }

        index.Unmaintainable ??= $"it indexes column '{name}', which the table no longer has as it was";
        return (-1, type, nullable, null);
    }

    private static byte[] Bound(byte[] definition, ref int position)
    {
        var length = BinaryPrimitives.ReadUInt16LittleEndian(definition.AsSpan(position));
        position += sizeof(ushort);
        var bound = definition.AsSpan(position, length).ToArray();
        position += length;
        return bound;
    }
}
