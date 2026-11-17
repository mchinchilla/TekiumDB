// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded;

/// <summary>
/// How a column is stored. The numeric values are part of the file format: never renumber them.
/// </summary>
public enum TkColumnType
{
    /// <summary>Not a valid column type.</summary>
    None = 0,

    /// <summary><see cref="bool"/>: one byte.</summary>
    Boolean = 1,

    /// <summary><see cref="byte"/>.</summary>
    U8 = 2,

    /// <summary><see cref="sbyte"/>.</summary>
    I8 = 3,

    /// <summary><see cref="short"/>, little-endian.</summary>
    I16 = 4,

    /// <summary><see cref="ushort"/>, little-endian.</summary>
    U16 = 5,

    /// <summary><see cref="int"/>, little-endian.</summary>
    I32 = 6,

    /// <summary><see cref="uint"/>, little-endian.</summary>
    U32 = 7,

    /// <summary><see cref="long"/>, little-endian.</summary>
    I64 = 8,

    /// <summary><see cref="ulong"/>, little-endian.</summary>
    U64 = 9,

    /// <summary><see cref="float"/>: its IEEE 754 bits, little-endian.</summary>
    F32 = 10,

    /// <summary><see cref="double"/>: its IEEE 754 bits, little-endian.</summary>
    F64 = 11,

    /// <summary><see cref="decimal"/>: its four 32-bit parts, 16 bytes.</summary>
    Numeric = 12,

    /// <summary><see cref="char"/>: one UTF-16 code unit.</summary>
    Character = 13,

    /// <summary><see cref="System.Guid"/>: 16 bytes in network order.</summary>
    Uuid = 14,

    /// <summary><see cref="System.DateTime"/>: ticks and kind, 8 bytes.</summary>
    DateTime = 15,

    /// <summary><see cref="System.DateTimeOffset"/>: ticks and offset in minutes, 10 bytes.</summary>
    DateTimeOffset = 16,

    /// <summary><see cref="System.TimeSpan"/>: ticks, 8 bytes.</summary>
    TimeSpan = 17,

    /// <summary><see cref="System.DateOnly"/>: day number, 4 bytes.</summary>
    DateOnly = 18,

    /// <summary><see cref="System.TimeOnly"/>: ticks, 8 bytes.</summary>
    TimeOnly = 19,

    /// <summary><see cref="string"/>: length prefix and UTF-8 bytes.</summary>
    Text = 20,

    /// <summary>A byte array: length prefix and the bytes.</summary>
    Bytes = 21,

    /// <summary>A JSON document kept as its text (<see cref="TkJson"/>): length prefix and the UTF-8 text, as it was written.</summary>
    Json = 22,

    /// <summary>A JSON document in its binary form (<see cref="TkJson"/>): length prefix and the bytes of <c>JsonBinary</c>.</summary>
    Jsonb = 23,
}
