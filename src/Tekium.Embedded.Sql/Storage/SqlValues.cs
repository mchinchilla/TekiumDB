// Copyright (c) Tekium. Licensed under the MIT License.

using System.Globalization;
using System.Text;

namespace Tekium.Embedded.Sql.Storage;

/// <summary>
/// Values as the SQL layer handles them: boxed, in the CLR type of their column. Conversion to a column,
/// comparison across numeric types, and the same encodings the typed API writes: the row format, the
/// ordered key of an index field, and the primary key.
/// </summary>
internal static class SqlValues
{
    /// <summary>The CLR type a column of this kind holds.</summary>
    public static Type ClrType(TkColumnType type) => type switch
    {
        TkColumnType.Boolean => typeof(bool),
        TkColumnType.U8 => typeof(byte),
        TkColumnType.I8 => typeof(sbyte),
        TkColumnType.I16 => typeof(short),
        TkColumnType.U16 => typeof(ushort),
        TkColumnType.I32 => typeof(int),
        TkColumnType.U32 => typeof(uint),
        TkColumnType.I64 => typeof(long),
        TkColumnType.U64 => typeof(ulong),
        TkColumnType.F32 => typeof(float),
        TkColumnType.F64 => typeof(double),
        TkColumnType.Numeric => typeof(decimal),
        TkColumnType.Character => typeof(char),
        TkColumnType.Uuid => typeof(Guid),
        TkColumnType.DateTime => typeof(DateTime),
        TkColumnType.DateTimeOffset => typeof(DateTimeOffset),
        TkColumnType.TimeSpan => typeof(TimeSpan),
        TkColumnType.DateOnly => typeof(DateOnly),
        TkColumnType.TimeOnly => typeof(TimeOnly),
        TkColumnType.Text => typeof(string),
        _ => typeof(byte[]),
    };

    /// <summary>Turns a value into the one a column of this kind stores. Throws if it cannot be that.</summary>
    public static object Convert(object value, TkColumnType type, string column)
    {
        try
        {
            return type switch
            {
                TkColumnType.Boolean => value is bool ? value : System.Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
                TkColumnType.U8 => System.Convert.ToByte(Number(value), CultureInfo.InvariantCulture),
                TkColumnType.I8 => System.Convert.ToSByte(Number(value), CultureInfo.InvariantCulture),
                TkColumnType.I16 => System.Convert.ToInt16(Number(value), CultureInfo.InvariantCulture),
                TkColumnType.U16 => System.Convert.ToUInt16(Number(value), CultureInfo.InvariantCulture),
                TkColumnType.I32 => System.Convert.ToInt32(Number(value), CultureInfo.InvariantCulture),
                TkColumnType.U32 => System.Convert.ToUInt32(Number(value), CultureInfo.InvariantCulture),
                TkColumnType.I64 => System.Convert.ToInt64(Number(value), CultureInfo.InvariantCulture),
                TkColumnType.U64 => System.Convert.ToUInt64(Number(value), CultureInfo.InvariantCulture),
                TkColumnType.F32 => System.Convert.ToSingle(Number(value), CultureInfo.InvariantCulture),
                TkColumnType.F64 => System.Convert.ToDouble(Number(value), CultureInfo.InvariantCulture),
                TkColumnType.Numeric => System.Convert.ToDecimal(Number(value), CultureInfo.InvariantCulture),
                TkColumnType.Character => value switch
                {
                    char => value,
                    string { Length: 1 } text => text[0],
                    _ => throw new InvalidCastException(),
                },
                TkColumnType.Uuid => value switch
                {
                    Guid => value,
                    string text => Guid.Parse(text, CultureInfo.InvariantCulture),
                    byte[] { Length: 16 } bytes => new Guid(bytes, bigEndian: true),
                    _ => throw new InvalidCastException(),
                },
                TkColumnType.DateTime => value switch
                {
                    DateTime => value,
                    DateTimeOffset moment => moment.UtcDateTime,
                    string text => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    _ => throw new InvalidCastException(),
                },
                TkColumnType.DateTimeOffset => value switch
                {
                    DateTimeOffset => value,
                    DateTime { Kind: DateTimeKind.Unspecified } moment => new DateTimeOffset(moment, TimeSpan.Zero),
                    DateTime moment => new DateTimeOffset(moment),
                    string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                    _ => throw new InvalidCastException(),
                },
                TkColumnType.TimeSpan => value switch
                {
                    TimeSpan => value,
                    string text => TimeSpan.Parse(text, CultureInfo.InvariantCulture),
                    _ => throw new InvalidCastException(),
                },
                TkColumnType.DateOnly => value switch
                {
                    DateOnly => value,
                    DateTime moment => DateOnly.FromDateTime(moment),
                    string text => DateOnly.Parse(text, CultureInfo.InvariantCulture),
                    _ => throw new InvalidCastException(),
                },
                TkColumnType.TimeOnly => value switch
                {
                    TimeOnly => value,
                    TimeSpan span => TimeOnly.FromTimeSpan(span),
                    string text => TimeOnly.Parse(text, CultureInfo.InvariantCulture),
                    _ => throw new InvalidCastException(),
                },
                TkColumnType.Text => value switch
                {
                    string => value,
                    char one => one.ToString(),
                    Guid id => id.ToString(),
                    _ => throw new InvalidCastException(),
                },
                _ => value is byte[] ? value : throw new InvalidCastException(),
            };
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            throw new TkSqlException($"The value {Show(value)} cannot be stored in column '{column}', which holds {type}.", exception);
        }

        static object Number(object value) => value switch
        {
            bool flag => flag ? 1 : 0,
            string or Guid or DateTime or DateTimeOffset or byte[] => throw new InvalidCastException(),
            _ => value,
        };
    }

    /// <summary>How a value reads in a message.</summary>
    public static string Show(object? value) => value switch
    {
        null => "NULL",
        string text => $"'{text}'",
        byte[] bytes => $"x'{System.Convert.ToHexString(bytes)}'",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>Orders two values that are not null. Numbers compare by value whatever their types; other values need the same type.</summary>
    public static int Compare(object left, object right)
    {
        if (IsNumber(left) && IsNumber(right))
        {
            if (left is double or float || right is double or float)
            {
                return System.Convert.ToDouble(left, CultureInfo.InvariantCulture).CompareTo(System.Convert.ToDouble(right, CultureInfo.InvariantCulture));
            }

            return System.Convert.ToDecimal(left, CultureInfo.InvariantCulture).CompareTo(System.Convert.ToDecimal(right, CultureInfo.InvariantCulture));
        }

        return (left, right) switch
        {
            (string a, string b) => CompareText(a, b),
            (char a, string b) => CompareText(a.ToString(), b),
            (string a, char b) => CompareText(a, b.ToString()),
            (byte[] a, byte[] b) => a.AsSpan().SequenceCompareTo(b),
            (Guid a, Guid b) => a.ToByteArray(bigEndian: true).AsSpan().SequenceCompareTo(b.ToByteArray(bigEndian: true)),
            (DateTimeOffset a, DateTimeOffset b) => a.UtcTicks.CompareTo(b.UtcTicks),
            (DateTimeOffset a, DateTime b) => a.UtcTicks.CompareTo(((DateTimeOffset)Convert(b, TkColumnType.DateTimeOffset, "value")).UtcTicks),
            (DateTime a, DateTimeOffset b) => ((DateTimeOffset)Convert(a, TkColumnType.DateTimeOffset, "value")).UtcTicks.CompareTo(b.UtcTicks),
            (IComparable a, _) when a.GetType() == right.GetType() => a.CompareTo(right),
            _ => throw new TkSqlException($"{Show(left)} ({left.GetType().Name}) and {Show(right)} ({right.GetType().Name}) cannot be compared."),
        };
    }

    /// <summary>The value a column that cannot be NULL has in a row written before the column existed.</summary>
    public static object Zero(TkColumnType type) => type switch
    {
        TkColumnType.Boolean => false,
        TkColumnType.U8 => (byte)0,
        TkColumnType.I8 => (sbyte)0,
        TkColumnType.I16 => (short)0,
        TkColumnType.U16 => (ushort)0,
        TkColumnType.I32 => 0,
        TkColumnType.U32 => 0u,
        TkColumnType.I64 => 0L,
        TkColumnType.U64 => 0ul,
        TkColumnType.F32 => 0f,
        TkColumnType.F64 => 0d,
        TkColumnType.Numeric => 0m,
        TkColumnType.Character => '\0',
        TkColumnType.Uuid => Guid.Empty,
        TkColumnType.DateTime => default(DateTime),
        TkColumnType.DateTimeOffset => default(DateTimeOffset),
        TkColumnType.TimeSpan => TimeSpan.Zero,
        TkColumnType.DateOnly => default(DateOnly),
        TkColumnType.TimeOnly => default(TimeOnly),
        TkColumnType.Text => string.Empty,
        _ => Array.Empty<byte>(),
    };

    /// <summary>The kind of column that holds values of the type of this one. Null if no column does.</summary>
    public static TkColumnType? KindOf(object value) => value switch
    {
        bool => TkColumnType.Boolean,
        byte => TkColumnType.U8,
        sbyte => TkColumnType.I8,
        short => TkColumnType.I16,
        ushort => TkColumnType.U16,
        int => TkColumnType.I32,
        uint => TkColumnType.U32,
        long => TkColumnType.I64,
        ulong => TkColumnType.U64,
        float => TkColumnType.F32,
        double => TkColumnType.F64,
        decimal => TkColumnType.Numeric,
        char => TkColumnType.Character,
        Guid => TkColumnType.Uuid,
        DateTime => TkColumnType.DateTime,
        DateTimeOffset => TkColumnType.DateTimeOffset,
        TimeSpan => TkColumnType.TimeSpan,
        DateOnly => TkColumnType.DateOnly,
        TimeOnly => TkColumnType.TimeOnly,
        string => TkColumnType.Text,
        byte[] => TkColumnType.Bytes,
        _ => null,
    };

    public static bool IsNumber(object value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;

    /// <summary>The primary key as the tree stores it: the same bytes the typed API writes.</summary>
    public static byte[] PrimaryKey(object value, TkColumnType type)
    {
        Span<byte> buffer = stackalloc byte[512];
        var length = type switch
        {
            TkColumnType.Uuid => TkKey.Write((Guid)value, buffer),
            TkColumnType.I32 => TkKey.Write((int)value, buffer),
            TkColumnType.I64 => TkKey.Write((long)value, buffer),
            TkColumnType.U32 => TkKey.Write((uint)value, buffer),
            TkColumnType.U64 => TkKey.Write((ulong)value, buffer),
            TkColumnType.Text => WriteTextKey((string)value, buffer),
            _ => throw new TkSqlException($"A primary key cannot be of type {type}."),
        };
        return buffer[..length].ToArray();

        static int WriteTextKey(string text, Span<byte> buffer)
        {
            try
            {
                return TkKey.Write(text, buffer);
            }
            catch (ArgumentException exception)
            {
                throw new TkSqlException($"'{text}' cannot be a primary key: {exception.Message}", exception);
            }
        }
    }

    /// <summary>Writes a value as an index field: ordered bytes, with the mark of "has a value" first if the field is nullable.</summary>
    public static void WriteField(ref TkKeyWriter writer, object? value, TkColumnType type, bool nullable)
    {
        if (nullable)
        {
            if (value is null)
            {
                writer.WriteNull();
                return;
            }

            writer.WritePresent();
        }

        switch (type)
        {
            case TkColumnType.Boolean: writer.Write((bool)value!); break;
            case TkColumnType.U8: writer.Write((byte)value!); break;
            case TkColumnType.I8: writer.Write((sbyte)value!); break;
            case TkColumnType.I16: writer.Write((short)value!); break;
            case TkColumnType.U16: writer.Write((ushort)value!); break;
            case TkColumnType.I32: writer.Write((int)value!); break;
            case TkColumnType.U32: writer.Write((uint)value!); break;
            case TkColumnType.I64: writer.Write((long)value!); break;
            case TkColumnType.U64: writer.Write((ulong)value!); break;
            case TkColumnType.F32: writer.Write((float)value!); break;
            case TkColumnType.F64: writer.Write((double)value!); break;
            case TkColumnType.Numeric: writer.Write((decimal)value!); break;
            case TkColumnType.Character: writer.Write((char)value!); break;
            case TkColumnType.Uuid: writer.Write((Guid)value!); break;
            case TkColumnType.DateTime: writer.Write((DateTime)value!); break;
            case TkColumnType.DateTimeOffset: writer.Write((DateTimeOffset)value!); break;
            case TkColumnType.TimeSpan: writer.Write((TimeSpan)value!); break;
            case TkColumnType.DateOnly: writer.Write((DateOnly)value!); break;
            case TkColumnType.TimeOnly: writer.Write((TimeOnly)value!); break;
            case TkColumnType.Text: writer.Write((string)value!); break;
            default: writer.Write((byte[])value!); break;
        }
    }

    /// <summary>Writes a row in the format of the typed API: version, null map and values.</summary>
    public static byte[] EncodeRow(byte version, ReadOnlySpan<TkColumn> columns, object?[] values)
    {
        var size = 2 + (columns.Length / 8);
        for (var i = 0; i < columns.Length; i++)
        {
            size += values[i] switch
            {
                null => 0,
                string text => 5 + Encoding.UTF8.GetByteCount(text),
                byte[] bytes => 5 + bytes.Length,
                _ => 24,
            };
        }

        var buffer = new byte[size];
        var writer = new TkRowWriter(buffer, version, columns.Length);
        for (var i = 0; i < columns.Length; i++)
        {
            var value = values[i];
            if (value is null)
            {
                writer.WriteNull();
                continue;
            }

            switch (columns[i].Type)
            {
                case TkColumnType.Boolean: writer.WriteBoolean((bool)value); break;
                case TkColumnType.U8: writer.WriteByte((byte)value); break;
                case TkColumnType.I8: writer.WriteSByte((sbyte)value); break;
                case TkColumnType.I16: writer.WriteInt16((short)value); break;
                case TkColumnType.U16: writer.WriteUInt16((ushort)value); break;
                case TkColumnType.I32: writer.WriteInt32((int)value); break;
                case TkColumnType.U32: writer.WriteUInt32((uint)value); break;
                case TkColumnType.I64: writer.WriteInt64((long)value); break;
                case TkColumnType.U64: writer.WriteUInt64((ulong)value); break;
                case TkColumnType.F32: writer.WriteSingle((float)value); break;
                case TkColumnType.F64: writer.WriteDouble((double)value); break;
                case TkColumnType.Numeric: writer.WriteDecimal((decimal)value); break;
                case TkColumnType.Character: writer.WriteChar((char)value); break;
                case TkColumnType.Uuid: writer.WriteGuid((Guid)value); break;
                case TkColumnType.DateTime: writer.WriteDateTime((DateTime)value); break;
                case TkColumnType.DateTimeOffset: writer.WriteDateTimeOffset((DateTimeOffset)value); break;
                case TkColumnType.TimeSpan: writer.WriteTimeSpan((TimeSpan)value); break;
                case TkColumnType.DateOnly: writer.WriteDateOnly((DateOnly)value); break;
                case TkColumnType.TimeOnly: writer.WriteTimeOnly((TimeOnly)value); break;
                case TkColumnType.Text: writer.WriteString((string)value); break;
                default: writer.WriteBytes((byte[])value); break;
            }
        }

        return buffer.AsSpan(0, writer.Length).ToArray();
    }

    /// <summary>Reads a stored row into one value per column of the schema it was written with.</summary>
    public static object?[] DecodeRow(ReadOnlySpan<byte> row, ReadOnlySpan<TkColumn> stored, ReadOnlySpan<int> identity)
    {
        var values = new object?[stored.Length];
        var reader = new TkRowReader(row, stored, identity);
        while (reader.Next(out var column))
        {
            if (reader.IsNull)
            {
                continue;
            }

            values[column] = stored[column].Type switch
            {
                TkColumnType.Boolean => reader.ReadBoolean(),
                TkColumnType.U8 => reader.ReadByte(),
                TkColumnType.I8 => reader.ReadSByte(),
                TkColumnType.I16 => reader.ReadInt16(),
                TkColumnType.U16 => reader.ReadUInt16(),
                TkColumnType.I32 => reader.ReadInt32(),
                TkColumnType.U32 => reader.ReadUInt32(),
                TkColumnType.I64 => reader.ReadInt64(),
                TkColumnType.U64 => reader.ReadUInt64(),
                TkColumnType.F32 => reader.ReadSingle(),
                TkColumnType.F64 => reader.ReadDouble(),
                TkColumnType.Numeric => reader.ReadDecimal(),
                TkColumnType.Character => reader.ReadChar(),
                TkColumnType.Uuid => reader.ReadGuid(),
                TkColumnType.DateTime => reader.ReadDateTime(),
                TkColumnType.DateTimeOffset => reader.ReadDateTimeOffset(),
                TkColumnType.TimeSpan => reader.ReadTimeSpan(),
                TkColumnType.DateOnly => reader.ReadDateOnly(),
                TkColumnType.TimeOnly => reader.ReadTimeOnly(),
                TkColumnType.Text => reader.ReadString(),
                _ => reader.ReadBytes(),
            };
        }

        return values;
    }

    private static int CompareText(string left, string right)
    {
        // By Unicode code point, the order the engine keeps text in.
        var a = left.EnumerateRunes().GetEnumerator();
        var b = right.EnumerateRunes().GetEnumerator();
        while (true)
        {
            var more = a.MoveNext();
            if (more != b.MoveNext())
            {
                return more ? 1 : -1;
            }

            if (!more)
            {
                return 0;
            }

            if (a.Current != b.Current)
            {
                return a.Current.Value.CompareTo(b.Current.Value);
            }
        }
    }
}
