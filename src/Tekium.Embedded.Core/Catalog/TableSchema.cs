// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using System.Text;

namespace Tekium.Embedded.Catalog;

/// <summary>
/// One version of the columns of a table. Versions are never changed once committed: a row carries
/// the version it was written with, and that version says how to read it forever.
/// </summary>
internal sealed class TableSchema
{
    public const int MaxColumns = 1024;

    private const byte NullableFlag = 1;

    public TableSchema(byte version, TkColumn[] columns, int keyColumn)
    {
        Version = version;
        Columns = columns;
        KeyColumn = keyColumn;
    }

    /// <summary>Gets the version number rows written with this schema start with. Never zero.</summary>
    public byte Version { get; }

    public TkColumn[] Columns { get; }

    public int KeyColumn { get; }

    /// <summary>Whether a type declares exactly these columns, in this order, with this key.</summary>
    public bool Matches(ReadOnlySpan<TkColumn> columns, int keyColumn) =>
        keyColumn == KeyColumn && columns.SequenceEqual(Columns);

    public int EncodedSize()
    {
        var size = 4;
        foreach (var column in Columns)
        {
            size += 3 + Encoding.UTF8.GetByteCount(column.Name);
        }

        return size;
    }

    /// <summary>Layout: column count (u16), key column (u16), then per column its type, flags and name.</summary>
    public int Encode(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)Columns.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], (ushort)KeyColumn);
        var position = 4;
        foreach (var column in Columns)
        {
            destination[position++] = (byte)column.Type;
            destination[position++] = column.IsNullable ? NullableFlag : (byte)0;
            var length = Encoding.UTF8.GetBytes(column.Name, destination[(position + 1)..]);
            destination[position] = (byte)length;
            position += 1 + length;
        }

        return position;
    }

    public static TableSchema Decode(byte version, ReadOnlySpan<byte> source)
    {
        if (source.Length < 4)
        {
            throw Corrupt();
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(source);
        var keyColumn = BinaryPrimitives.ReadUInt16LittleEndian(source[2..]);
        if (count == 0 || count > MaxColumns || keyColumn >= count)
        {
            throw Corrupt();
        }

        var columns = new TkColumn[count];
        var position = 4;
        for (var i = 0; i < count; i++)
        {
            if (source.Length - position < 3)
            {
                throw Corrupt();
            }

            var type = (TkColumnType)source[position];
            var flags = source[position + 1];
            var length = source[position + 2];
            position += 3;
            if (type is <= TkColumnType.None or > TkColumnType.Bytes
                || flags > NullableFlag
                || length == 0
                || length > TkColumn.MaxNameBytes
                || length > source.Length - position)
            {
                throw Corrupt();
            }

            columns[i] = new TkColumn(Encoding.UTF8.GetString(source.Slice(position, length)), type, flags == NullableFlag);
            position += length;
        }

        return new TableSchema(version, columns, keyColumn);
    }

    private static InvalidDataException Corrupt() => new("A table schema in the catalog is damaged.");
}
