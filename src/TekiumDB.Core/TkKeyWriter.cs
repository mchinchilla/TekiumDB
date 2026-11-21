// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace TekiumDB;

/// <summary>
/// Writes values so that comparing the bytes compares the values, one after another: several values in
/// a row sort like the tuple, because no encoded value is the beginning of another. An index only ever
/// compares these bytes. Generated code calls it once per indexed column.
/// </summary>
/// <remarks>
/// <para>
/// The order is that of <c>CompareTo</c> for every type but text, which sorts by Unicode code point
/// (not by UTF-16 unit, and with no notion of culture), and byte arrays, which sort by content.
/// </para>
/// <para>
/// What does not fit is cut short, never refused: the bytes written are then the beginning of the whole
/// key, which is all a comparison against a shorter one needs. <see cref="IsTruncated"/> says so.
/// </para>
/// <para>This layout is file format: the entries of every index are these bytes.</para>
/// </remarks>
public ref struct TkKeyWriter
{
    private const ulong SignBit64 = 0x8000_0000_0000_0000;
    private const uint SignBit32 = 0x8000_0000;

    // The first byte of a decimal says its sign and its magnitude at once: zero in the middle, larger
    // positives above, larger negatives below.
    private const int DecimalZero = 0x80;
    private const int DecimalExponentBias = 40;
    private const int MaxDecimalDigits = 29;

    private readonly Span<byte> _destination;
    private int _position;

    /// <summary>Initializes a new instance of the <see cref="TkKeyWriter"/> struct.</summary>
    /// <param name="destination">Where the key goes.</param>
    public TkKeyWriter(Span<byte> destination)
    {
        _destination = destination;
        _position = 0;
        IsTruncated = false;
    }

    /// <summary>Gets the number of bytes written.</summary>
    public readonly int Length => _position;

    /// <summary>Gets a value indicating whether the key did not fit and was cut short.</summary>
    public bool IsTruncated { get; private set; }

    /// <summary>Gets the bytes written so far.</summary>
    internal readonly ReadOnlySpan<byte> Written => _destination[.._position];

    /// <summary>Writes the mark of a nullable column that has no value. It sorts before every value.</summary>
    public void WriteNull() => Put(0);

    /// <summary>Writes the mark of a nullable column that has a value, which must follow.</summary>
    public void WritePresent() => Put(1);

    /// <summary>Writes a <see cref="bool"/>: false before true.</summary>
    /// <param name="value">The value.</param>
    public void Write(bool value) => Put(value ? (byte)1 : (byte)0);

    /// <summary>Writes a <see cref="byte"/>.</summary>
    /// <param name="value">The value.</param>
    public void Write(byte value) => Put(value);

    /// <summary>Writes an <see cref="sbyte"/>, with the sign bit flipped so that negatives sort first.</summary>
    /// <param name="value">The value.</param>
    public void Write(sbyte value) => Put((byte)(value ^ 0x80));

    /// <summary>Writes a <see cref="short"/>, big-endian with the sign bit flipped.</summary>
    /// <param name="value">The value.</param>
    public void Write(short value) => Write((ushort)(value ^ 0x8000));

    /// <summary>Writes a <see cref="ushort"/>, big-endian.</summary>
    /// <param name="value">The value.</param>
    public void Write(ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        Put(bytes);
    }

    /// <summary>Writes an <see cref="int"/>, big-endian with the sign bit flipped.</summary>
    /// <param name="value">The value.</param>
    public void Write(int value) => Write((uint)value ^ SignBit32);

    /// <summary>Writes a <see cref="uint"/>, big-endian.</summary>
    /// <param name="value">The value.</param>
    public void Write(uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        Put(bytes);
    }

    /// <summary>Writes a <see cref="long"/>, big-endian with the sign bit flipped.</summary>
    /// <param name="value">The value.</param>
    public void Write(long value) => Write((ulong)value ^ SignBit64);

    /// <summary>Writes a <see cref="ulong"/>, big-endian.</summary>
    /// <param name="value">The value.</param>
    public void Write(ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        Put(bytes);
    }

    /// <summary>
    /// Writes a <see cref="float"/> in the order of <see cref="float.CompareTo(float)"/>: NaN first, and
    /// negative zero the same as zero.
    /// </summary>
    /// <param name="value">The value.</param>
    public void Write(float value)
    {
        if (float.IsNaN(value))
        {
            Write(0u);
            return;
        }

        // Positives get the sign bit set and so sort above; negatives are inverted, so that the larger
        // magnitude sorts lower.
        var bits = value == 0 ? 0u : BitConverter.SingleToUInt32Bits(value);
        Write((bits & SignBit32) != 0 ? ~bits : bits | SignBit32);
    }

    /// <summary>
    /// Writes a <see cref="double"/> in the order of <see cref="double.CompareTo(double)"/>: NaN first, and
    /// negative zero the same as zero.
    /// </summary>
    /// <param name="value">The value.</param>
    public void Write(double value)
    {
        if (double.IsNaN(value))
        {
            Write(0ul);
            return;
        }

        var bits = value == 0 ? 0ul : BitConverter.DoubleToUInt64Bits(value);
        Write((bits & SignBit64) != 0 ? ~bits : bits | SignBit64);
    }

    /// <summary>
    /// Writes a <see cref="decimal"/> by value: 1 and 1.00 get the same key, in at most 18 bytes.
    /// </summary>
    /// <param name="value">The value.</param>
    public void Write(decimal value)
    {
        Span<int> parts = stackalloc int[4];
        decimal.GetBits(value, parts);
        var mantissa = new UInt128((uint)parts[2], ((ulong)(uint)parts[1] << 32) | (uint)parts[0]);
        if (mantissa == UInt128.Zero)
        {
            Put(DecimalZero);
            return;
        }

        var negative = parts[3] < 0;
        var scale = (parts[3] >> 16) & 0xFF;

        // The digits, most significant first, padded with a zero so that they pair up.
        Span<byte> digits = stackalloc byte[MaxDecimalDigits + 1];
        var count = 0;
        for (var rest = mantissa; rest != UInt128.Zero; count++)
        {
            (rest, var digit) = UInt128.DivRem(rest, 10);
            digits[count] = (byte)digit;
        }

        digits[..count].Reverse();
        digits[count] = 0;

        // The value is 0.d1d2d3… × 10^exponent, with d1 not zero: a larger exponent is a larger magnitude.
        var exponent = count - scale + DecimalExponentBias;
        var pairs = (count + 1) / 2;
        while (digits[(pairs * 2) - 2] == 0 && digits[(pairs * 2) - 1] == 0)
        {
            pairs--;
        }

        // A negative is the same bytes inverted, so that the larger magnitude sorts lower. The end mark
        // is below every pair of a positive and above every pair of a negative: 0.12 sorts before 0.123,
        // and -0.12 after -0.123.
        var flip = negative ? (byte)0xFF : (byte)0;
        Put((byte)((DecimalZero + exponent) ^ flip));
        for (var pair = 0; pair < pairs; pair++)
        {
            Put((byte)(((digits[pair * 2] * 10) + digits[(pair * 2) + 1] + 1) ^ flip));
        }

        Put(flip);
    }

    /// <summary>Writes a <see cref="char"/> as its UTF-16 unit, big-endian.</summary>
    /// <param name="value">The value.</param>
    public void Write(char value) => Write((ushort)value);

    /// <summary>Writes a <see cref="Guid"/> in network order, the order UUID v7 sorts by time in.</summary>
    /// <param name="value">The value.</param>
    public void Write(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        Put(bytes);
    }

    /// <summary>Writes a <see cref="DateTime"/> by its ticks, whatever its kind, as comparing two does.</summary>
    /// <param name="value">The value.</param>
    public void Write(DateTime value) => Write(value.Ticks);

    /// <summary>
    /// Writes a <see cref="DateTimeOffset"/> by its instant: the same moment in two zones is one key.
    /// </summary>
    /// <param name="value">The value.</param>
    public void Write(DateTimeOffset value) => Write(value.UtcTicks);

    /// <summary>Writes a <see cref="TimeSpan"/> by its ticks.</summary>
    /// <param name="value">The value.</param>
    public void Write(TimeSpan value) => Write(value.Ticks);

    /// <summary>Writes a <see cref="DateOnly"/> by its day number.</summary>
    /// <param name="value">The value.</param>
    public void Write(DateOnly value) => Write(value.DayNumber);

    /// <summary>Writes a <see cref="TimeOnly"/> by its ticks.</summary>
    /// <param name="value">The value.</param>
    public void Write(TimeOnly value) => Write(value.Ticks);

    /// <summary>Writes text as UTF-8, which sorts by code point, followed by an end mark.</summary>
    /// <param name="value">The value.</param>
    /// <exception cref="ArgumentException">The text has half of a surrogate pair on its own.</exception>
    public void Write(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Span<byte> encoded = stackalloc byte[4];
        var rest = value.AsSpan();
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out var consumed) != OperationStatus.Done)
            {
                throw new ArgumentException(
                    "The text is not valid Unicode: it has half of a surrogate pair on its own.", nameof(value));
            }

            PutEscaped(encoded[..rune.EncodeToUtf8(encoded)]);
            rest = rest[consumed..];
        }

        PutEnd();
    }

    /// <summary>Writes bytes as they are, followed by an end mark.</summary>
    /// <param name="value">The value.</param>
    public void Write(scoped ReadOnlySpan<byte> value)
    {
        PutEscaped(value);
        PutEnd();
    }

    /// <summary>Writes a value given as a type parameter. The branches not taken disappear when the method is compiled.</summary>
    internal static void WriteValue<TValue>(ref TkKeyWriter writer, TValue value, TkColumnType type)
    {
        // Unsafe.As only reinterprets a value whose exact type, or whose size if it is an enum, was just established.
        switch (type)
        {
            case TkColumnType.Boolean when typeof(TValue) == typeof(bool):
                writer.Write(Unsafe.As<TValue, bool>(ref value));
                return;
            case TkColumnType.U8 when Is<TValue, byte>():
                writer.Write(Unsafe.As<TValue, byte>(ref value));
                return;
            case TkColumnType.I8 when Is<TValue, sbyte>():
                writer.Write(Unsafe.As<TValue, sbyte>(ref value));
                return;
            case TkColumnType.I16 when Is<TValue, short>():
                writer.Write(Unsafe.As<TValue, short>(ref value));
                return;
            case TkColumnType.U16 when Is<TValue, ushort>():
                writer.Write(Unsafe.As<TValue, ushort>(ref value));
                return;
            case TkColumnType.I32 when Is<TValue, int>():
                writer.Write(Unsafe.As<TValue, int>(ref value));
                return;
            case TkColumnType.U32 when Is<TValue, uint>():
                writer.Write(Unsafe.As<TValue, uint>(ref value));
                return;
            case TkColumnType.I64 when Is<TValue, long>():
                writer.Write(Unsafe.As<TValue, long>(ref value));
                return;
            case TkColumnType.U64 when Is<TValue, ulong>():
                writer.Write(Unsafe.As<TValue, ulong>(ref value));
                return;
            case TkColumnType.F32 when typeof(TValue) == typeof(float):
                writer.Write(Unsafe.As<TValue, float>(ref value));
                return;
            case TkColumnType.F64 when typeof(TValue) == typeof(double):
                writer.Write(Unsafe.As<TValue, double>(ref value));
                return;
            case TkColumnType.Numeric when typeof(TValue) == typeof(decimal):
                writer.Write(Unsafe.As<TValue, decimal>(ref value));
                return;
            case TkColumnType.Character when typeof(TValue) == typeof(char):
                writer.Write(Unsafe.As<TValue, char>(ref value));
                return;
            case TkColumnType.Uuid when typeof(TValue) == typeof(Guid):
                writer.Write(Unsafe.As<TValue, Guid>(ref value));
                return;
            case TkColumnType.DateTime when typeof(TValue) == typeof(DateTime):
                writer.Write(Unsafe.As<TValue, DateTime>(ref value));
                return;
            case TkColumnType.DateTimeOffset when typeof(TValue) == typeof(DateTimeOffset):
                writer.Write(Unsafe.As<TValue, DateTimeOffset>(ref value));
                return;
            case TkColumnType.TimeSpan when typeof(TValue) == typeof(TimeSpan):
                writer.Write(Unsafe.As<TValue, TimeSpan>(ref value));
                return;
            case TkColumnType.DateOnly when typeof(TValue) == typeof(DateOnly):
                writer.Write(Unsafe.As<TValue, DateOnly>(ref value));
                return;
            case TkColumnType.TimeOnly when typeof(TValue) == typeof(TimeOnly):
                writer.Write(Unsafe.As<TValue, TimeOnly>(ref value));
                return;
            case TkColumnType.Text when typeof(TValue) == typeof(string):
                writer.Write(Unsafe.As<TValue, string>(ref value));
                return;
            case TkColumnType.Bytes when typeof(TValue) == typeof(byte[]):
                writer.Write(Unsafe.As<TValue, byte[]>(ref value));
                return;
            case TkColumnType.Json or TkColumnType.Jsonb:
                throw new ArgumentException("A JSON document has no order of its own: index or compare a value at a path of it.", nameof(value));
            default:
                throw new ArgumentException($"The field is a {type}, which a {typeof(TValue).Name} cannot be compared with.", nameof(value));
        }
    }

    /// <summary>Copies bytes that are already a key.</summary>
    internal void WriteRaw(scoped ReadOnlySpan<byte> key) => Put(key);

    /// <summary>
    /// A zero inside the value becomes 00 FF and the value ends with 00 00: the end sorts before any
    /// continuation, so "a" comes before "a\0", and that before "ab".
    /// </summary>
    private void PutEscaped(scoped ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            Put(value);
            if (value == 0)
            {
                Put(0xFF);
            }
        }
    }

    private static bool Is<TValue, TStored>() =>
        typeof(TValue) == typeof(TStored) || (typeof(TValue).IsEnum && Unsafe.SizeOf<TValue>() == Unsafe.SizeOf<TStored>());

    private void PutEnd()
    {
        Put(0);
        Put(0);
    }

    private void Put(byte value)
    {
        if (_position < _destination.Length)
        {
            _destination[_position++] = value;
        }
        else
        {
            IsTruncated = true;
        }
    }

    private void Put(scoped ReadOnlySpan<byte> bytes)
    {
        var room = Math.Min(bytes.Length, _destination.Length - _position);
        bytes[..room].CopyTo(_destination[_position..]);
        _position += room;
        IsTruncated |= room < bytes.Length;
    }
}
