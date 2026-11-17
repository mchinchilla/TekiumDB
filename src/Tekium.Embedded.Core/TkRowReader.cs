// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using System.Text;

namespace Tekium.Embedded;

/// <summary>
/// Reads one stored row. The row may be older than the type reading it: the reader walks the columns
/// the row was written with and tells generated code which property each one belongs to, skipping the
/// ones that no longer exist.
/// </summary>
public ref struct TkRowReader
{
    private readonly ReadOnlySpan<byte> _row;
    private readonly ReadOnlySpan<TkColumn> _stored;
    private readonly ReadOnlySpan<int> _members;
    private int _column;
    private int _position;

    /// <param name="row">The stored row, schema version byte included.</param>
    /// <param name="stored">The columns of the schema version the row was written with.</param>
    /// <param name="members">For each stored column, the index of the column of the type that receives it, or -1.</param>
    internal TkRowReader(ReadOnlySpan<byte> row, ReadOnlySpan<TkColumn> stored, ReadOnlySpan<int> members)
    {
        _row = row;
        _stored = stored;
        _members = members;
        _column = -1;
        _position = 1 + ((stored.Length + 7) / 8);
        IsNull = false;
        if (_position > row.Length)
        {
            ThrowCorrupt();
        }
    }

    /// <summary>Gets a value indicating whether the column <see cref="Next"/> stopped at holds no value.</summary>
    public bool IsNull { get; private set; }

    /// <summary>Moves to the next stored column the type still has.</summary>
    /// <param name="member">Index of that column in the columns of the type.</param>
    /// <returns><see langword="false"/> when the row has no more columns.</returns>
    public bool Next(out int member)
    {
        while (++_column < _stored.Length)
        {
            var isNull = (_row[1 + (_column >> 3)] & (1 << (_column & 7))) != 0;
            member = _members[_column];
            if (member >= 0)
            {
                IsNull = isNull;
                return true;
            }

            if (!isNull)
            {
                Skip(_stored[_column].Type);
            }
        }

        member = -1;
        return false;
    }

    /// <summary>Reads a <see cref="bool"/> column.</summary>
    /// <returns>The value.</returns>
    public bool ReadBoolean() => Take(1)[0] != 0;

    /// <summary>Reads a <see cref="byte"/> column.</summary>
    /// <returns>The value.</returns>
    public byte ReadByte() => Take(1)[0];

    /// <summary>Reads an <see cref="sbyte"/> column.</summary>
    /// <returns>The value.</returns>
    public sbyte ReadSByte() => (sbyte)Take(1)[0];

    /// <summary>Reads a <see cref="short"/> column.</summary>
    /// <returns>The value.</returns>
    public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));

    /// <summary>Reads a <see cref="ushort"/> column.</summary>
    /// <returns>The value.</returns>
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    /// <summary>Reads an <see cref="int"/> column.</summary>
    /// <returns>The value.</returns>
    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    /// <summary>Reads a <see cref="uint"/> column.</summary>
    /// <returns>The value.</returns>
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    /// <summary>Reads a <see cref="long"/> column.</summary>
    /// <returns>The value.</returns>
    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    /// <summary>Reads a <see cref="ulong"/> column.</summary>
    /// <returns>The value.</returns>
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    /// <summary>Reads a <see cref="float"/> column.</summary>
    /// <returns>The value.</returns>
    public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    /// <summary>Reads a <see cref="double"/> column.</summary>
    /// <returns>The value.</returns>
    public double ReadDouble() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    /// <summary>Reads a <see cref="decimal"/> column.</summary>
    /// <returns>The value.</returns>
    public decimal ReadDecimal()
    {
        var source = Take(16);
        Span<int> parts = stackalloc int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = BinaryPrimitives.ReadInt32LittleEndian(source[(i * 4)..]);
        }

        try
        {
            return new decimal(parts);
        }
        catch (ArgumentException)
        {
            ThrowCorrupt();
            return default;
        }
    }

    /// <summary>Reads a <see cref="char"/> column.</summary>
    /// <returns>The value.</returns>
    public char ReadChar() => (char)BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    /// <summary>Reads a <see cref="Guid"/> column.</summary>
    /// <returns>The value.</returns>
    public Guid ReadGuid() => new(Take(16), bigEndian: true);

    /// <summary>Reads a <see cref="DateTime"/> column, with the <see cref="DateTime.Kind"/> it was written with.</summary>
    /// <returns>The value.</returns>
    public DateTime ReadDateTime()
    {
        var bits = BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        var ticks = (long)(bits & 0x3FFF_FFFF_FFFF_FFFF);
        var kind = (int)(bits >> 62);
        if (ticks > DateTime.MaxValue.Ticks || kind > (int)DateTimeKind.Local)
        {
            ThrowCorrupt();
        }

        return new DateTime(ticks, (DateTimeKind)kind);
    }

    /// <summary>Reads a <see cref="DateTimeOffset"/> column, with the offset it was written with.</summary>
    /// <returns>The value.</returns>
    public DateTimeOffset ReadDateTimeOffset()
    {
        var source = Take(10);
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(source);
        var minutes = BinaryPrimitives.ReadInt16LittleEndian(source[8..]);
        try
        {
            return new DateTimeOffset(ticks, TimeSpan.FromMinutes(minutes));
        }
        catch (ArgumentException)
        {
            ThrowCorrupt();
            return default;
        }
    }

    /// <summary>Reads a <see cref="TimeSpan"/> column.</summary>
    /// <returns>The value.</returns>
    public TimeSpan ReadTimeSpan() => new(BinaryPrimitives.ReadInt64LittleEndian(Take(8)));

    /// <summary>Reads a <see cref="DateOnly"/> column.</summary>
    /// <returns>The value.</returns>
    public DateOnly ReadDateOnly()
    {
        var day = BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        if ((uint)day > (uint)DateOnly.MaxValue.DayNumber)
        {
            ThrowCorrupt();
        }

        return DateOnly.FromDayNumber(day);
    }

    /// <summary>Reads a <see cref="TimeOnly"/> column.</summary>
    /// <returns>The value.</returns>
    public TimeOnly ReadTimeOnly()
    {
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        if ((ulong)ticks > (ulong)TimeOnly.MaxValue.Ticks)
        {
            ThrowCorrupt();
        }

        return new TimeOnly(ticks);
    }

    /// <summary>Reads a <see cref="string"/> column.</summary>
    /// <returns>The value.</returns>
    public string ReadString()
    {
        try
        {
            return TkText.Utf8.GetString(Take(ReadLength()));
        }
        catch (DecoderFallbackException)
        {
            ThrowCorrupt();
            return string.Empty;
        }
    }

    /// <summary>Reads a byte array column.</summary>
    /// <returns>The value, in a new array.</returns>
    public byte[] ReadBytes() => Take(ReadLength()).ToArray();

    /// <summary>Reads a <c>json</c> column, checked to be a document.</summary>
    /// <returns>The document, kept as text.</returns>
    public TkJson ReadJson() => TkJson.FromStored(Take(ReadLength()), TkJsonFormat.Text);

    /// <summary>Reads a <c>jsonb</c> column, checked whole.</summary>
    /// <returns>The document, kept as binary.</returns>
    public TkJson ReadJsonb() => TkJson.FromStored(Take(ReadLength()), TkJsonFormat.Binary);

    private static void ThrowCorrupt() =>
        throw new InvalidDataException("A stored row does not match the schema it claims to have been written with.");

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count > _row.Length - _position)
        {
            ThrowCorrupt();
        }

        var span = _row.Slice(_position, count);
        _position += count;
        return span;
    }

    private int ReadLength()
    {
        uint value = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var next = Take(1)[0];
            value |= (uint)(next & 0x7F) << shift;
            if ((next & 0x80) == 0)
            {
                if (value > (uint)(_row.Length - _position))
                {
                    break;
                }

                return (int)value;
            }
        }

        ThrowCorrupt();
        return 0;
    }

    private void Skip(TkColumnType type)
    {
        var size = type switch
        {
            TkColumnType.Boolean or TkColumnType.U8 or TkColumnType.I8 => 1,
            TkColumnType.I16 or TkColumnType.U16 or TkColumnType.Character => 2,
            TkColumnType.I32 or TkColumnType.U32 or TkColumnType.F32 or TkColumnType.DateOnly => 4,
            TkColumnType.I64 or TkColumnType.U64 or TkColumnType.F64 or TkColumnType.DateTime
                or TkColumnType.TimeSpan or TkColumnType.TimeOnly => 8,
            TkColumnType.DateTimeOffset => 10,
            TkColumnType.Numeric or TkColumnType.Uuid => 16,
            TkColumnType.Text or TkColumnType.Bytes or TkColumnType.Json or TkColumnType.Jsonb => ReadLength(),
            _ => -1,
        };
        if (size < 0)
        {
            ThrowCorrupt();
        }

        Take(size);
    }
}
