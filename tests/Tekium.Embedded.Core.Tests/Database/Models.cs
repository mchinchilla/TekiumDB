// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Tests.Database;

public enum Priority : byte
{
    Low,
    Normal,
    High = 200,
}

public enum Flavor : long
{
    None,
    Sweet = long.MinValue,
    Bitter = long.MaxValue,
}

[TkTable("people")]
public sealed partial record Person
{
    [TkKey]
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int? Age { get; set; }

    public string? Email { get; set; }
}

[TkTable]
public sealed partial record Counter
{
    [TkKey]
    public int Id { get; set; }

    public long Value { get; set; }
}

/// <summary>Clave de texto, propiedades <c>init</c> y <c>required</c>, una propiedad calculada y una ignorada.</summary>
[TkTable("notes")]
public sealed partial record Note
{
    [TkKey]
    public required string Slug { get; init; }

    public required string Body { get; init; }

    public byte[]? Attachment { get; init; }

    public int Length => Body.Length;

    [TkIgnore]
    public string Scratch { get; set; } = "not stored";
}

public abstract class Entity
{
    [TkKey]
    public long Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Hereda la clave de su clase base.</summary>
[TkTable("orders")]
public sealed partial class Order : Entity
{
    public decimal Total { get; set; }

    public Priority Priority { get; set; }
}

public static partial class Outer
{
    /// <summary>Un tipo anidado: el generador debe reabrir el tipo que lo contiene.</summary>
    [TkTable("nested")]
    public sealed partial record Inner
    {
        [TkKey]
        public ulong Id { get; set; }

        public string? Text { get; set; }
    }
}

/// <summary>Todos los tipos que una columna puede tener, en su forma no anulable y anulable.</summary>
[TkTable("everything")]
public sealed partial record Everything
{
    [TkKey]
    public long Id { get; set; }

    public bool Boolean { get; set; }

    public byte Byte { get; set; }

    public sbyte SByte { get; set; }

    public short Int16 { get; set; }

    public ushort UInt16 { get; set; }

    public int Int32 { get; set; }

    public uint UInt32 { get; set; }

    public long Int64 { get; set; }

    public ulong UInt64 { get; set; }

    public float Single { get; set; }

    public double Double { get; set; }

    public decimal Decimal { get; set; }

    public char Char { get; set; }

    public Guid Guid { get; set; }

    public DateTime DateTime { get; set; }

    public DateTimeOffset DateTimeOffset { get; set; }

    public TimeSpan TimeSpan { get; set; }

    public DateOnly DateOnly { get; set; }

    public TimeOnly TimeOnly { get; set; }

    public string String { get; set; } = string.Empty;

    public Priority Enum { get; set; }

    public Flavor WideEnum { get; set; }

    public bool? NullableBoolean { get; set; }

    public byte? NullableByte { get; set; }

    public sbyte? NullableSByte { get; set; }

    public short? NullableInt16 { get; set; }

    public ushort? NullableUInt16 { get; set; }

    public int? NullableInt32 { get; set; }

    public uint? NullableUInt32 { get; set; }

    public long? NullableInt64 { get; set; }

    public ulong? NullableUInt64 { get; set; }

    public float? NullableSingle { get; set; }

    public double? NullableDouble { get; set; }

    public decimal? NullableDecimal { get; set; }

    public char? NullableChar { get; set; }

    public Guid? NullableGuid { get; set; }

    public DateTime? NullableDateTime { get; set; }

    public DateTimeOffset? NullableDateTimeOffset { get; set; }

    public TimeSpan? NullableTimeSpan { get; set; }

    public DateOnly? NullableDateOnly { get; set; }

    public TimeOnly? NullableTimeOnly { get; set; }

    public string? NullableString { get; set; }

    public Priority? NullableEnum { get; set; }
}

/// <summary>Los arreglos de bytes aparte: un record los compara por referencia.</summary>
[TkTable("blobs")]
public sealed partial class Blob
{
    [TkKey]
    public int Id { get; set; }

    public byte[] Data { get; set; } = [];

    public byte[]? Optional { get; set; }
}

// --- Evolución de esquema: varias versiones del mismo tipo, todas sobre la tabla "contacts". ---

[TkTable("contacts")]
public sealed partial record ContactV1
{
    [TkKey]
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Legacy { get; set; }

    public string? Phone { get; set; }
}

/// <summary>Renombra Name, quita Legacy, agrega columnas anulables y no anulables, y cambia el orden.</summary>
[TkTable("contacts")]
public sealed partial record ContactV2
{
    public string? Email { get; set; }

    public string? Phone { get; set; }

    [TkKey]
    public Guid Id { get; set; }

    public int Score { get; set; }

    [TkColumn("Name")]
    public string FullName { get; set; } = string.Empty;

    public DateTime? Since { get; set; }

    public string Notes { get; set; } = string.Empty;
}

/// <summary>Otra forma de la versión 2, para probar que una versión descartada no deja rastro.</summary>
[TkTable("contacts")]
public sealed partial record ContactAlternative
{
    [TkKey]
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public long Score { get; set; }
}

[TkTable("contacts")]
public sealed partial record ContactWithAnotherType
{
    [TkKey]
    public Guid Id { get; set; }

    public int Name { get; set; }
}

[TkTable("contacts")]
public sealed partial record ContactWithAnotherKey
{
    public Guid Id { get; set; }

    [TkKey]
    public string Name { get; set; } = string.Empty;
}

[TkTable("contacts")]
public sealed partial record ContactThatForbidsNull
{
    [TkKey]
    public Guid Id { get; set; }

    public string Phone { get; set; } = string.Empty;
}

/// <summary>
/// Una tabla de 600 columnas, escrita a mano porque nadie quiere leer 600 propiedades: implementa lo
/// mismo que el generador. Sólo guarda la clave y la última columna; las demás van nulas.
/// </summary>
public sealed class Wide : ITkRow<Wide>
{
    private const int ColumnCount = 600;

    private static readonly TkColumn[] AllColumns =
    [
        new("Id", TkColumnType.I32, false),
        .. Enumerable.Range(1, ColumnCount - 2).Select(static i => new TkColumn($"OptionalColumnNumber{i:D4}", TkColumnType.F64, true)),
        new("Last", TkColumnType.I32, false),
    ];

    public static string TableName => "wide";

    public static ReadOnlySpan<TkColumn> Columns => AllColumns;

    public static int KeyColumn => 0;

    public int Id { get; set; }

    public int Last { get; set; }

    public static void Write(ref TkRowWriter writer, Wide row)
    {
        ArgumentNullException.ThrowIfNull(row);
        writer.WriteInt32(row.Id);
        for (var i = 0; i < ColumnCount - 2; i++)
        {
            writer.WriteNull();
        }

        writer.WriteInt32(row.Last);
    }

    public static Wide Read(ref TkRowReader reader)
    {
        var row = new Wide();
        while (reader.Next(out var member))
        {
            if (member == 0)
            {
                row.Id = reader.ReadInt32();
            }
            else if (member == ColumnCount - 1)
            {
                row.Last = reader.ReadInt32();
            }
        }

        return row;
    }

    public static int WriteKey(Wide row, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(row);
        return TkKey.Write(row.Id, destination);
    }
}
