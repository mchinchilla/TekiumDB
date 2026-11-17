// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Catalog;

namespace Tekium.Embedded.Tests.Rows;

/// <summary>El formato de fila visto byte a byte, y lo que pasa cuando los bytes no son lo que dicen ser.</summary>
public sealed class RowFormatTests
{
    private static readonly TkColumn[] Columns =
    [
        new("Id", TkColumnType.I32, false),
        new("Name", TkColumnType.Text, true),
        new("Score", TkColumnType.F64, true),
    ];

    [Fact]
    public void A_row_is_its_version_a_null_bitmap_and_the_values_that_are_not_null()
    {
        var buffer = new byte[64];
        var writer = new TkRowWriter(buffer, version: 3, columns: 3);
        writer.WriteInt32(0x01020304);
        writer.WriteString("añ");
        writer.WriteNull();

        // Versión, mapa de nulos (bit 2), entero little-endian, longitud y UTF-8. La columna nula no ocupa nada.
        Assert.Equal([3, 0b100, 4, 3, 2, 1, 3, (byte)'a', 0xC3, 0xB1], buffer.AsSpan(0, writer.Length).ToArray());
    }

    [Fact]
    public void Writing_fewer_or_more_columns_than_declared_is_a_bug_that_is_caught()
    {
        Assert.Throws<InvalidOperationException>(static () =>
        {
            var writer = new TkRowWriter(new byte[64], 1, columns: 2);
            writer.WriteInt32(1);
            return writer.Length;
        });
        Assert.Throws<InvalidOperationException>(static () =>
        {
            var writer = new TkRowWriter(new byte[64], 1, columns: 1);
            writer.WriteInt32(1);
            writer.WriteNull();
        });
    }

    [Fact]
    public void A_row_that_does_not_fit_is_refused_wherever_it_overflows()
    {
        for (var size = 0; size < 14; size++)
        {
            var room = size;
            Assert.Throws<TkRowTooLargeException>(() =>
            {
                var writer = new TkRowWriter(new byte[room], 1, columns: 3);
                writer.WriteInt32(1);
                writer.WriteString("abcde");
                writer.WriteDouble(1.5);
            });
        }
    }

    [Fact]
    public void Columns_the_type_no_longer_has_are_skipped_whatever_their_type()
    {
        var stored = Enum.GetValues<TkColumnType>().Where(static type => type != TkColumnType.None)
            .Select(static type => new TkColumn(type.ToString(), type, false))
            .Append(new TkColumn("Last", TkColumnType.I32, false))
            .ToArray();
        var buffer = new byte[512];
        var writer = new TkRowWriter(buffer, 1, stored.Length);
        writer.WriteBoolean(true);
        writer.WriteByte(1);
        writer.WriteSByte(-1);
        writer.WriteInt16(2);
        writer.WriteUInt16(3);
        writer.WriteInt32(4);
        writer.WriteUInt32(5);
        writer.WriteInt64(6);
        writer.WriteUInt64(7);
        writer.WriteSingle(8);
        writer.WriteDouble(9);
        writer.WriteDecimal(10);
        writer.WriteChar('x');
        writer.WriteGuid(Guid.AllBitsSet);
        writer.WriteDateTime(DateTime.UnixEpoch);
        writer.WriteDateTimeOffset(DateTimeOffset.UnixEpoch);
        writer.WriteTimeSpan(TimeSpan.FromHours(1));
        writer.WriteDateOnly(new DateOnly(2026, 1, 1));
        writer.WriteTimeOnly(new TimeOnly(12, 0));
        writer.WriteString(new string('s', 200));
        writer.WriteBytes([1, 2, 3]);
        writer.WriteJson(TkJson.Parse(" [1, {\"a\": 2}] "));
        writer.WriteJsonb(TkJson.Parse("{\"b\":1,\"a\":[true]}"));
        writer.WriteInt32(424242);

        // El tipo que lee sólo conserva la última columna: todas las demás se saltan por su tipo guardado.
        var plan = ReadPlan.Build("t", new TableSchema(1, stored, stored.Length - 1), [new TkColumn("Last", TkColumnType.I32, false)], 0);
        var reader = new TkRowReader(buffer.AsSpan(0, writer.Length), plan.Stored, plan.Members);
        Assert.True(reader.Next(out var member));
        Assert.Equal(0, member);
        Assert.Equal(424242, reader.ReadInt32());
        Assert.False(reader.Next(out _));
    }

    [Fact]
    public void Truncated_or_damaged_rows_are_reported_as_corruption_and_never_read_out_of_bounds()
    {
        var buffer = new byte[64];
        var writer = new TkRowWriter(buffer, 1, columns: 3);
        writer.WriteInt32(7);
        writer.WriteString("hello");
        writer.WriteDouble(2.5);
        var row = buffer.AsSpan(0, writer.Length).ToArray();
        var members = new[] { 0, 1, 2 };

        // Cortada en cualquier punto.
        for (var length = 0; length < row.Length; length++)
        {
            var cut = row.AsSpan(0, length).ToArray();
            Assert.Throws<InvalidDataException>(() => ReadAll(cut, members));
        }

        // Una longitud de texto que apunta fuera de la fila, y una que nunca termina.
        var longer = (byte[])row.Clone();
        longer[6] = 0x7F;
        Assert.Throws<InvalidDataException>(() => ReadAll(longer, members));
        var endless = (byte[])row.Clone();
        endless.AsSpan(6).Fill(0xFF);
        Assert.Throws<InvalidDataException>(() => ReadAll(endless, members));

        // Bytes que no son UTF-8.
        var notText = (byte[])row.Clone();
        notText[7] = 0xFF;
        Assert.Throws<InvalidDataException>(() => ReadAll(notText, members));

        Assert.Equal(3, ReadAll(row, members));
    }

    [Fact]
    public void A_damaged_schema_in_the_catalog_is_reported_and_a_good_one_survives_the_round_trip()
    {
        var schema = new TableSchema(4, Columns, keyColumn: 0);
        var encoded = new byte[schema.EncodedSize()];
        Assert.Equal(encoded.Length, schema.Encode(encoded));

        var decoded = TableSchema.Decode(4, encoded);
        Assert.Equal(4, decoded.Version);
        Assert.True(decoded.Matches(Columns, 0));
        Assert.False(decoded.Matches(Columns, 1));
        Assert.False(decoded.Matches(Columns.AsSpan(0, 2), 0));

        for (var length = 0; length < encoded.Length; length++)
        {
            var cut = encoded.AsSpan(0, length).ToArray();
            Assert.Throws<InvalidDataException>(() => TableSchema.Decode(4, cut));
        }

        var badType = (byte[])encoded.Clone();
        badType[4] = 99;
        Assert.Throws<InvalidDataException>(() => TableSchema.Decode(4, badType));
        var badKey = (byte[])encoded.Clone();
        badKey[2] = 3;
        Assert.Throws<InvalidDataException>(() => TableSchema.Decode(4, badKey));
    }

    private static int ReadAll(byte[] row, int[] members)
    {
        var reader = new TkRowReader(row, Columns, members);
        var read = 0;
        while (reader.Next(out var member))
        {
            read++;
            if (reader.IsNull)
            {
                continue;
            }

            switch (member)
            {
                case 0:
                    reader.ReadInt32();
                    break;
                case 1:
                    reader.ReadString();
                    break;
                default:
                    reader.ReadDouble();
                    break;
            }
        }

        return read;
    }
}
