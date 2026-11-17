// Copyright (c) Tekium. Licensed under the MIT License.

using System.Text.Json;
using CsCheck;
using Tekium.Embedded.Tests.Storage;

namespace Tekium.Embedded.Tests.Database;

/// <summary>
/// Criterio de salida de la Fase 5: ida y vuelta de todos los tipos primitivos y sus formas anulables,
/// por el código que escribe el generador y a través de la base, no sólo del codificador.
/// </summary>
public abstract class RoundTripTests : DatabaseTests
{
    // Texto Unicode válido de cualquier plano: BMP sin sustitutos sueltos, y algún par sustituto entero.
    private static readonly Gen<string> Text =
        Gen.OneOf(
            Gen.Char.Where(static c => !char.IsSurrogate(c)).Select(static c => c.ToString()),
            Gen.Const("\U0001F600"),
            Gen.Const("ñ"))
        .Array[0, 12].Select(string.Concat);

    private static readonly Gen<DateTime> AnyDateTime =
        Gen.Select(Gen.Long[DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks], Gen.Int[0, 2])
            .Select(static (ticks, kind) => new DateTime(ticks, (DateTimeKind)kind));

    private static readonly Gen<DateTimeOffset> AnyDateTimeOffset =
        Gen.Select(Gen.Long[TimeSpan.TicksPerDay, DateTime.MaxValue.Ticks - TimeSpan.TicksPerDay], Gen.Int[-14 * 60, 14 * 60])
            .Select(static (ticks, minutes) => new DateTimeOffset(ticks, TimeSpan.FromMinutes(minutes)));

    private static readonly Priority[] Priorities = [Priority.Low, Priority.Normal, Priority.High, (Priority)77];

    private static readonly Flavor[] Flavors = [Flavor.None, Flavor.Sweet, Flavor.Bitter];

    private static readonly Gen<Priority> AnyPriority = Gen.Int[0, 3].Select(static index => Priorities[index]);

    // Documentos JSON escritos con espacios y orden propios: la columna de texto los devuelve tal cual.
    private static readonly Gen<TkJson> AnyJson =
        Gen.Select(Text, Gen.Int, Gen.Bool, Gen.Int[0, 3])
            .Select(static (text, number, flag, shape) => TkJson.Parse(shape switch
            {
                0 => $" {{ \"z\" : {number} , \"a\": [ {(flag ? "true" : "null")} , 1.50 ] }}",
                1 => JsonSerializer.Serialize(text),
                2 => $"[{number}, {number}e3]",
                _ => "{}",
            }));

    private static readonly Gen<Everything> AnyRow =
        from id in Gen.Long
        from numbers in Gen.Select(Gen.Bool, Gen.Byte, Gen.SByte, Gen.Short, Gen.UShort, Gen.Int, Gen.UInt)
        from wide in Gen.Select(Gen.Long, Gen.ULong, Gen.Float, Gen.Double, Gen.Decimal, Gen.Char, Gen.Guid)
        from times in Gen.Select(AnyDateTime, AnyDateTimeOffset, Gen.TimeSpan, Gen.DateOnly, Gen.TimeOnly)
        from others in Gen.Select(Text, AnyPriority, Gen.Int[0, 2].Select(static index => Flavors[index]))
        from nullableNumbers in Gen.Select(Gen.Bool.Nullable(), Gen.Byte.Nullable(), Gen.SByte.Nullable(), Gen.Short.Nullable(), Gen.UShort.Nullable(), Gen.Int.Nullable(), Gen.UInt.Nullable())
        from nullableWide in Gen.Select(Gen.Long.Nullable(), Gen.ULong.Nullable(), Gen.Float.Nullable(), Gen.Double.Nullable(), Gen.Decimal.Nullable(), Gen.Char.Nullable(), Gen.Guid.Nullable())
        from nullableTimes in Gen.Select(AnyDateTime.Nullable(), AnyDateTimeOffset.Nullable(), Gen.TimeSpan.Nullable(), Gen.DateOnly.Nullable(), Gen.TimeOnly.Nullable())
        from nullableOthers in Gen.Select(Text.Null(), AnyPriority.Nullable())
        from json in Gen.Select(AnyJson, AnyJson, AnyJson.Nullable(), AnyJson.Nullable())
        select new Everything
        {
            Id = id,
            Boolean = numbers.Item1,
            Byte = numbers.Item2,
            SByte = numbers.Item3,
            Int16 = numbers.Item4,
            UInt16 = numbers.Item5,
            Int32 = numbers.Item6,
            UInt32 = numbers.Item7,
            Int64 = wide.Item1,
            UInt64 = wide.Item2,
            Single = wide.Item3,
            Double = wide.Item4,
            Decimal = wide.Item5,
            Char = wide.Item6,
            Guid = wide.Item7,
            DateTime = times.Item1,
            DateTimeOffset = times.Item2,
            TimeSpan = times.Item3,
            DateOnly = times.Item4,
            TimeOnly = times.Item5,
            String = others.Item1,
            Enum = others.Item2,
            WideEnum = others.Item3,
            NullableBoolean = nullableNumbers.Item1,
            NullableByte = nullableNumbers.Item2,
            NullableSByte = nullableNumbers.Item3,
            NullableInt16 = nullableNumbers.Item4,
            NullableUInt16 = nullableNumbers.Item5,
            NullableInt32 = nullableNumbers.Item6,
            NullableUInt32 = nullableNumbers.Item7,
            NullableInt64 = nullableWide.Item1,
            NullableUInt64 = nullableWide.Item2,
            NullableSingle = nullableWide.Item3,
            NullableDouble = nullableWide.Item4,
            NullableDecimal = nullableWide.Item5,
            NullableChar = nullableWide.Item6,
            NullableGuid = nullableWide.Item7,
            NullableDateTime = nullableTimes.Item1,
            NullableDateTimeOffset = nullableTimes.Item2,
            NullableTimeSpan = nullableTimes.Item3,
            NullableDateOnly = nullableTimes.Item4,
            NullableTimeOnly = nullableTimes.Item5,
            NullableString = nullableOthers.Item1,
            NullableEnum = nullableOthers.Item2,
            Json = json.Item1,
            Jsonb = json.Item2,
            NullableJson = json.Item3,
            NullableJsonb = json.Item4,
        };

    [Fact]
    public async Task Any_row_of_every_column_type_comes_back_equal()
    {
        var device = NewDevice();
        var written = new Dictionary<long, Everything>();
        await using (var database = await OpenAsync(device))
        {
            var table = database.Table<Everything>();
            await AnyRow.Array[1, 40].SampleAsync(
                async rows =>
                {
                    await using (var writer = await database.BeginWriteAsync(Ct))
                    {
                        foreach (var row in rows)
                        {
                            await table.UpsertAsync(writer, row, Ct);
                            written[row.Id] = row;
                            AssertSame(row, await table.GetAsync(writer, row.Id, Ct));
                        }

                        await writer.CommitAsync(Ct);
                    }

                    foreach (var row in rows)
                    {
                        AssertSame(written[row.Id], await table.GetAsync(row.Id, Ct));
                    }
                },
                iter: 150,
                threads: 1);
        }

        // Y todo sigue igual después de cerrar y abrir.
        await using var reopened = await OpenAsync(device);
        var all = await ToListAsync(reopened.Table<Everything>().ScanAsync(Ct));
        Assert.Equal(written.Count, all.Count);
        Assert.Equal(written.Keys.Order(), all.Select(static row => row.Id));
        foreach (var row in all)
        {
            AssertSame(written[row.Id], row);
        }
    }

    [Fact]
    public async Task The_extremes_of_every_type_come_back_exactly()
    {
        await using var database = await OpenAsync(NewDevice());
        var table = database.Table<Everything>();
        var extremes = new[]
        {
            new Everything
            {
                Id = long.MinValue, Boolean = true, Byte = byte.MaxValue, SByte = sbyte.MinValue, Int16 = short.MinValue,
                UInt16 = ushort.MaxValue, Int32 = int.MinValue, UInt32 = uint.MaxValue, Int64 = long.MinValue,
                UInt64 = ulong.MaxValue, Single = float.NegativeInfinity, Double = double.Epsilon, Decimal = decimal.MinValue,
                Char = char.MaxValue, Guid = Guid.AllBitsSet, DateTime = DateTime.MaxValue, DateTimeOffset = DateTimeOffset.MinValue,
                TimeSpan = TimeSpan.MinValue, DateOnly = DateOnly.MaxValue, TimeOnly = TimeOnly.MaxValue,
                String = "\0 inside", Enum = Priority.High, WideEnum = Flavor.Sweet,
                NullableSingle = float.NaN, NullableDouble = -0.0, NullableDecimal = 0.10m, NullableChar = '\0',
                NullableDateTime = DateTime.MinValue, NullableString = string.Empty, NullableEnum = Priority.Low,
            },
            new Everything
            {
                Id = long.MaxValue, DateTime = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Local),
                NullableDateTime = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc),
                DateTimeOffset = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.FromHours(-6)),
                Decimal = decimal.MaxValue, Single = float.MaxValue, Double = double.MinValue,
            },
        };

        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            foreach (var row in extremes)
            {
                await table.InsertAsync(writer, row, Ct);
            }

            await writer.CommitAsync(Ct);
        }

        var read = await ToListAsync(table.ScanAsync(Ct));
        AssertSame(extremes[0], read[0]);
        AssertSame(extremes[1], read[1]);

        // Lo que la igualdad de .NET no mira y también tiene que volver: la clase del DateTime, el
        // desplazamiento, el signo del cero y la escala del decimal.
        Assert.Equal(DateTimeKind.Local, read[1].DateTime.Kind);
        Assert.Equal(DateTimeKind.Utc, read[1].NullableDateTime!.Value.Kind);
        Assert.Equal(TimeSpan.FromHours(-6), read[1].DateTimeOffset.Offset);
        Assert.True(double.IsNegative(read[0].NullableDouble!.Value));
        Assert.Equal("0.10", read[0].NullableDecimal!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Byte_arrays_come_back_byte_for_byte_and_null_is_not_empty()
    {
        await using var database = await OpenAsync(NewDevice());
        var table = database.Table<Blob>();
        var data = new byte[700];
        new Random(5).NextBytes(data);
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await table.InsertAsync(writer, new Blob { Id = 1, Data = data, Optional = null }, Ct);
            await table.InsertAsync(writer, new Blob { Id = 2, Data = [], Optional = [] }, Ct);
            await table.InsertAsync(writer, new Blob { Id = 3, Data = [0], Optional = [0, 255] }, Ct);
            await writer.CommitAsync(Ct);
        }

        var first = await table.GetAsync(1, Ct);
        Assert.Equal(data, first!.Data);
        Assert.Null(first.Optional);
        var second = await table.GetAsync(2, Ct);
        Assert.Empty(second!.Data);
        Assert.NotNull(second.Optional);
        Assert.Empty(second.Optional);
        Assert.Equal([0, 255], (await table.GetAsync(3, Ct))!.Optional);
    }

    private static void AssertSame(Everything expected, Everything? actual)
    {
        Assert.Equal(expected, actual);
        Assert.Equal(expected.DateTime.Kind, actual!.DateTime.Kind);
        Assert.Equal(expected.NullableDateTime?.Kind, actual.NullableDateTime?.Kind);
        Assert.Equal(expected.DateTimeOffset.Offset, actual.DateTimeOffset.Offset);
        Assert.Equal(expected.NullableDateTimeOffset?.Offset, actual.NullableDateTimeOffset?.Offset);

        // Un json vuelve con el mismo texto; un jsonb, en su forma binaria, que dice lo mismo.
        Assert.Equal(expected.Json.ToString(), actual.Json.ToString());
        Assert.Equal(expected.NullableJson?.ToString(), actual.NullableJson?.ToString());
        Assert.Equal(TkJsonFormat.Text, actual.Json.Format);
        Assert.Equal(TkJsonFormat.Binary, actual.Jsonb.Format);
        Assert.Equal(expected.Jsonb.ToBinary().ToString(), actual.Jsonb.ToString());
    }
}

public sealed class RoundTripTests_File : RoundTripTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class RoundTripTests_Memory : RoundTripTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
