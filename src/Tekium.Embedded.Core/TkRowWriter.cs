// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;

namespace Tekium.Embedded;

/// <summary>
/// Writes one row, column by column, straight into a buffer: no boxing and no allocation.
/// Generated code calls one method per column, in the order the columns are declared.
/// </summary>
/// <remarks>
/// Layout: one byte with the schema version, one bit per column saying whether it is null, then the
/// values of the columns that are not, back to back. Fixed-size types are little-endian; strings and
/// byte arrays carry a length prefix.
/// </remarks>
public ref struct TkRowWriter
{
    private readonly Span<byte> _buffer;
    private readonly int _columns;
    private int _column;
    private int _position;

    internal TkRowWriter(Span<byte> buffer, byte version, int columns)
    {
        _buffer = buffer;
        _columns = columns;
        _column = 0;
        _position = 1 + ((columns + 7) / 8);
        if (_position > buffer.Length)
        {
            ThrowTooLarge();
        }

        buffer[.._position].Clear();
        buffer[0] = version;
    }

    /// <summary>Gets the number of bytes written once every column is in.</summary>
    internal readonly int Length =>
        _column == _columns
            ? _position
            : throw new InvalidOperationException($"The row has {_columns} columns but {_column} were written.");

    /// <summary>Records that the current column has no value.</summary>
    public void WriteNull()
    {
        var column = NextColumn();
        _buffer[1 + (column >> 3)] |= (byte)(1 << (column & 7));
    }

    /// <summary>Writes a <see cref="bool"/> column.</summary>
    /// <param name="value">The value.</param>
    public void WriteBoolean(bool value) => Take(1)[0] = value ? (byte)1 : (byte)0;

    /// <summary>Writes a <see cref="byte"/> column.</summary>
    /// <param name="value">The value.</param>
    public void WriteByte(byte value) => Take(1)[0] = value;

    /// <summary>Writes an <see cref="sbyte"/> column.</summary>
    /// <param name="value">The value.</param>
    public void WriteSByte(sbyte value) => Take(1)[0] = (byte)value;

    /// <summary>Writes a <see cref="short"/> column.</summary>
    /// <param name="value">The value.</param>
    public void WriteInt16(short value) => BinaryPrimitives.WriteInt16LittleEndian(Take(2), value);

    /// <summary>Writes a <see cref="ushort"/> column.</summary>
    /// <param name="value">The value.</param>
    public void WriteUInt16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), value);

    /// <summary>Writes an <see cref="int"/> column.</summary>
    /// <param name="value">The value.</param>
    public void WriteInt32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), value);

    /// <summary>Writes a <see cref="uint"/> column.</summary>
    /// <param name="value">The value.</param>
    public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), value);

    /// <summary>Writes a <see cref="long"/> column.</summary>
    /// <param name="value">The value.</param>
    public void WriteInt64(long value) => BinaryPrimitives.WriteInt64LittleEndian(Take(8), value);

    /// <summary>Writes a <see cref="ulong"/> column.</summary>
    /// <param name="value">The value.</param>
    public void WriteUInt64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Take(8), value);

    /// <summary>Writes a <see cref="float"/> column.</summary>
    /// <param name="value">The value.</param>
    public void WriteSingle(float value) => BinaryPrimitives.WriteSingleLittleEndian(Take(4), value);

    /// <summary>Writes a <see cref="double"/> column.</summary>
    /// <param name="value">The value.</param>
    public void WriteDouble(double value) => BinaryPrimitives.WriteDoubleLittleEndian(Take(8), value);

    /// <summary>Writes a <see cref="decimal"/> column.</summary>
    /// <param name="value">The value.</param>
    public void WriteDecimal(decimal value)
    {
        Span<int> parts = stackalloc int[4];
        decimal.GetBits(value, parts);
        var target = Take(16);
        for (var i = 0; i < parts.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(target[(i * 4)..], parts[i]);
        }
    }

    /// <summary>Writes a <see cref="char"/> column.</summary>
    /// <param name="value">The value.</param>
    public void WriteChar(char value) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), value);

    /// <summary>Writes a <see cref="Guid"/> column.</summary>
    /// <param name="value">The value.</param>
    public void WriteGuid(Guid value) => value.TryWriteBytes(Take(16), bigEndian: true, out _);

    /// <summary>Writes a <see cref="DateTime"/> column, keeping its <see cref="DateTime.Kind"/>.</summary>
    /// <param name="value">The value.</param>
    public void WriteDateTime(DateTime value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(Take(8), (ulong)value.Ticks | ((ulong)value.Kind << 62));

    /// <summary>Writes a <see cref="DateTimeOffset"/> column, keeping its offset.</summary>
    /// <param name="value">The value.</param>
    public void WriteDateTimeOffset(DateTimeOffset value)
    {
        var target = Take(10);
        BinaryPrimitives.WriteInt64LittleEndian(target, value.Ticks);
        BinaryPrimitives.WriteInt16LittleEndian(target[8..], (short)value.TotalOffsetMinutes);
    }

    /// <summary>Writes a <see cref="TimeSpan"/> column.</summary>
    /// <param name="value">The value.</param>
    public void WriteTimeSpan(TimeSpan value) => BinaryPrimitives.WriteInt64LittleEndian(Take(8), value.Ticks);

    /// <summary>Writes a <see cref="DateOnly"/> column.</summary>
    /// <param name="value">The value.</param>
    public void WriteDateOnly(DateOnly value) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), value.DayNumber);

    /// <summary>Writes a <see cref="TimeOnly"/> column.</summary>
    /// <param name="value">The value.</param>
    public void WriteTimeOnly(TimeOnly value) => BinaryPrimitives.WriteInt64LittleEndian(Take(8), value.Ticks);

    /// <summary>Writes a <see cref="string"/> column as UTF-8.</summary>
    /// <param name="value">The value. A column that may hold no value takes <see cref="WriteNull"/> instead.</param>
    public void WriteString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        NextColumn();
        WriteLength(TkText.ByteCount(value));
        if (!TkText.Utf8.TryGetBytes(value, _buffer[_position..], out var written))
        {
            ThrowTooLarge();
        }

        _position += written;
    }

    /// <summary>Writes a byte array column.</summary>
    /// <param name="value">The value.</param>
    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        NextColumn();
        WriteLength(value.Length);
        if (value.Length > _buffer.Length - _position)
        {
            ThrowTooLarge();
        }

        value.CopyTo(_buffer[_position..]);
        _position += value.Length;
    }

    /// <summary>Writes a <c>json</c> column: the text of the document, as it was written (canonical if it was binary).</summary>
    /// <param name="value">The document.</param>
    public void WriteJson(TkJson value) => WriteBytes(value.ToText().Bytes);

    /// <summary>Writes a <c>jsonb</c> column: the binary form of the document.</summary>
    /// <param name="value">The document.</param>
    public void WriteJsonb(TkJson value) => WriteBytes(value.ToBinary().Bytes);

    private static void ThrowTooLarge() =>
        throw new TkRowTooLargeException("The row does not fit in the space a row may take.");

    private int NextColumn()
    {
        if (_column == _columns)
        {
            throw new InvalidOperationException($"The row has {_columns} columns and more are being written.");
        }

        return _column++;
    }

    /// <summary>Starts the next column and reserves its bytes.</summary>
    private Span<byte> Take(int count)
    {
        NextColumn();
        if (count > _buffer.Length - _position)
        {
            ThrowTooLarge();
        }

        var span = _buffer.Slice(_position, count);
        _position += count;
        return span;
    }

    /// <summary>LEB128: seven bits per byte, least significant first.</summary>
    private void WriteLength(int length)
    {
        var value = (uint)length;
        do
        {
            if (_position == _buffer.Length)
            {
                ThrowTooLarge();
            }

            var low = (byte)(value & 0x7F);
            value >>= 7;
            _buffer[_position++] = value == 0 ? low : (byte)(low | 0x80);
        }
        while (value != 0);
    }
}
