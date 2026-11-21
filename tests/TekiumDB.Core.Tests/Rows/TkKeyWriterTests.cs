// Copyright (c) Tekium. Licensed under the MIT License.

using System.Text;
using CsCheck;

namespace TekiumDB.Tests.Rows;

/// <summary>
/// El criterio de la Fase 6: para todos los tipos, el orden de las claves codificadas es el de los valores.
/// </summary>
public sealed class TkKeyWriterTests
{
    private static readonly Gen<string> Text =
        Gen.OneOf(
            Gen.Int[0, 1],
            Gen.Int[0, 0x7F],
            Gen.Int[0, 0xD7FF],
            Gen.Int[0xE000, 0xFFFF],
            Gen.Int[0x10000, 0x10FFFF])
        .Array[0, 6]
        .Select(static points => string.Concat(points.Select(static point => new Rune(point).ToString())));

    private static readonly Gen<double> Doubles = Gen.OneOf(
        Gen.Double,
        Gen.Const(double.NaN),
        Gen.Const(double.NegativeInfinity),
        Gen.Const(double.PositiveInfinity),
        Gen.Const(0.0),
        Gen.Const(-0.0),
        Gen.Const(double.Epsilon),
        Gen.Const(-double.Epsilon),
        Gen.Int[-3, 3].Select(static value => (double)value));

    private static readonly Gen<float> Floats = Gen.OneOf(
        Gen.Float,
        Gen.Const(float.NaN),
        Gen.Const(float.NegativeInfinity),
        Gen.Const(float.PositiveInfinity),
        Gen.Const(0.0f),
        Gen.Const(-0.0f),
        Gen.Int[-3, 3].Select(static value => (float)value));

    // The same value at several scales (1, 1.0, 1.00) must give the same key, and neighbours must stay apart.
    private static readonly Gen<decimal> Decimals = Gen.OneOf(
        Gen.Decimal,
        Gen.Select(Gen.Int[-1000, 1000], Gen.Int[0, 28]).Select(static pair => new decimal(
            Math.Abs(pair.Item1), 0, 0, pair.Item1 < 0, (byte)pair.Item2)),
        Gen.Select(Gen.Long, Gen.Int[0, 28]).Select(static pair => new decimal(
            (int)pair.Item1, (int)(pair.Item1 >> 32), 0, pair.Item1 < 0, (byte)pair.Item2)),
        Gen.Const(decimal.MaxValue),
        Gen.Const(decimal.MinValue),
        Gen.Const(decimal.Zero),
        Gen.Const(new decimal(0, 0, 0, true, 5)));

    private delegate void Encoder<in T>(ref TkKeyWriter writer, T value);

    [Fact]
    public void Booleans_and_integers_sort_like_their_values()
    {
        Ordered(Gen.Bool, static (ref w, v) => w.Write(v));
        Ordered(Gen.Byte, static (ref w, v) => w.Write(v));
        Ordered(Gen.SByte, static (ref w, v) => w.Write(v));
        Ordered(Gen.Short, static (ref w, v) => w.Write(v));
        Ordered(Gen.UShort, static (ref w, v) => w.Write(v));
        Ordered(Gen.Int, static (ref w, v) => w.Write(v));
        Ordered(Gen.UInt, static (ref w, v) => w.Write(v));
        Ordered(Gen.Long, static (ref w, v) => w.Write(v));
        Ordered(Gen.ULong, static (ref w, v) => w.Write(v));
        Ordered(Gen.Int[-2, 2], static (ref w, v) => w.Write(v));
        Ordered(Gen.Long[-2, 2], static (ref w, v) => w.Write(v));
        Ordered(Gen.SByte[-2, 2], static (ref w, v) => w.Write(v));
        Ordered(Gen.Short[-2, 2], static (ref w, v) => w.Write(v));
    }

    [Fact]
    public void Floating_point_numbers_sort_like_their_values_with_nan_first()
    {
        Ordered(Doubles, static (ref w, v) => w.Write(v));
        Ordered(Floats, static (ref w, v) => w.Write(v));

        // CompareTo holds that -0.0 and 0.0 are the same value, and so must their keys.
        Assert.Equal(Encode(0.0, static (ref w, v) => w.Write(v)), Encode(-0.0, static (ref w, v) => w.Write(v)));
        Assert.Equal(
            Encode(double.NaN, static (ref w, v) => w.Write(v)),
            Encode(BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0001), static (ref w, v) => w.Write(v)));
    }

    [Fact]
    public void Decimals_sort_like_their_values_whatever_their_scale()
    {
        Ordered(Decimals, static (ref w, v) => w.Write(v));

        Assert.Equal(Encode(1m, static (ref w, v) => w.Write(v)), Encode(1.000m, static (ref w, v) => w.Write(v)));
        Assert.Equal(Encode(0m, static (ref w, v) => w.Write(v)), Encode(-0.00m, static (ref w, v) => w.Write(v)));
        Assert.True(Encode(decimal.MaxValue, static (ref w, v) => w.Write(v)).Length <= 18);
    }

    [Fact]
    public void Characters_and_uuids_sort_like_their_values()
    {
        Ordered(Gen.Char, static (ref w, v) => w.Write(v));
        Ordered(Gen.Guid, static (ref w, v) => w.Write(v));
    }

    [Fact]
    public void Dates_and_times_sort_like_their_values()
    {
        Ordered(Gen.DateTime, static (ref w, v) => w.Write(v));
        Ordered(Gen.DateTimeOffset, static (ref w, v) => w.Write(v));
        Ordered(Gen.TimeSpan, static (ref w, v) => w.Write(v));
        Ordered(Gen.DateOnly, static (ref w, v) => w.Write(v));
        Ordered(Gen.TimeOnly, static (ref w, v) => w.Write(v));
    }

    [Fact]
    public void The_same_instant_in_two_zones_has_one_key()
    {
        var utc = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        var local = utc.ToOffset(TimeSpan.FromHours(-6));

        Assert.Equal(Encode(utc, static (ref w, v) => w.Write(v)), Encode(local, static (ref w, v) => w.Write(v)));
    }

    [Fact]
    public void A_date_time_sorts_by_its_ticks_whatever_its_kind()
    {
        var ticks = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc).Ticks;

        Assert.Equal(
            Encode(new DateTime(ticks, DateTimeKind.Utc), static (ref w, v) => w.Write(v)),
            Encode(new DateTime(ticks, DateTimeKind.Local), static (ref w, v) => w.Write(v)));
    }

    [Fact]
    public void Text_sorts_by_code_point()
    {
        Ordered(Text, static (ref w, v) => w.Write(v), CompareByCodePoint);

        // Characters outside the basic plane come after all of it, unlike in UTF-16.
        Assert.True(Compare("￿", "\U00010000", static (ref w, v) => w.Write(v)) < 0);
        Assert.True(Compare(string.Empty, "\0", static (ref w, v) => w.Write(v)) < 0);
        Assert.True(Compare("a", "a\0", static (ref w, v) => w.Write(v)) < 0);
        Assert.True(Compare("a\0", "ab", static (ref w, v) => w.Write(v)) < 0);
    }

    [Fact]
    public void Text_that_is_not_unicode_is_refused()
    {
        Assert.Throws<ArgumentException>(static () => Encode("a\ud800", static (ref w, v) => w.Write(v)));
        Assert.Throws<ArgumentException>(static () => Encode("\udc00b", static (ref w, v) => w.Write(v)));
    }

    [Fact]
    public void Bytes_sort_like_their_contents()
    {
        Ordered(
            Gen.Byte[0, 2].Array[0, 5],
            static (ref w, v) => w.Write(v),
            static (a, b) => a.AsSpan().SequenceCompareTo(b));
        Ordered(
            Gen.Byte.Array[0, 5],
            static (ref w, v) => w.Write(v),
            static (a, b) => a.AsSpan().SequenceCompareTo(b));
    }

    [Fact]
    public void No_value_sorts_before_every_value()
    {
        Gen.Select(Gen.Int.Nullable(), Gen.Int.Nullable()).Sample(static (a, b) =>
            Math.Sign(Comparer<int?>.Default.Compare(a, b)) == Compare(a, b, static (ref w, v) =>
            {
                if (v is { } value)
                {
                    w.WritePresent();
                    w.Write(value);
                }
                else
                {
                    w.WriteNull();
                }
            }));
    }

    /// <summary>Ningún valor codificado es prefijo de otro: por eso varias columnas seguidas ordenan como la tupla.</summary>
    [Fact]
    public void Several_values_in_a_row_sort_like_the_tuple()
    {
        var parts = Gen.Select(Text, Gen.Byte[0, 2].Array[0, 3], Decimals, Gen.Int[-2, 2]);
        Gen.Select(parts, parts).Sample(static (a, b) =>
        {
            var expected = CompareByCodePoint(a.Item1, b.Item1);
            expected = expected != 0 ? expected : a.Item2.AsSpan().SequenceCompareTo(b.Item2);
            expected = expected != 0 ? expected : a.Item3.CompareTo(b.Item3);
            expected = expected != 0 ? expected : a.Item4.CompareTo(b.Item4);
            return Math.Sign(expected) == Compare(a, b, static (ref w, v) =>
            {
                w.Write(v.Item1);
                w.Write(v.Item2);
                w.Write(v.Item3);
                w.Write(v.Item4);
            });
        });
    }

    /// <summary>Un cero dentro de un texto no puede confundirse con su final: lo que sigue es otra columna.</summary>
    [Fact]
    public void A_zero_inside_a_value_is_not_mistaken_for_its_end()
    {
        Encoder<(string, string)> texts = static (ref w, v) =>
        {
            w.Write(v.Item1);
            w.Write(v.Item2);
        };
        Encoder<(byte[], byte[])> bytes = static (ref w, v) =>
        {
            w.Write(v.Item1);
            w.Write(v.Item2);
        };

        Assert.True(Compare(("a", "\0b"), ("a\0", "b"), texts) < 0);
        Assert.True(Compare(("a", "\uFFFF"), ("a\0", string.Empty), texts) < 0);
        Assert.True(Compare((new byte[] { 1 }, new byte[] { 0, 2 }), (new byte[] { 1, 0 }, new byte[] { 2 }), bytes) < 0);
        Assert.True(Compare((new byte[] { 1 }, new byte[] { 255 }), (new byte[] { 1, 0 }, Array.Empty<byte>()), bytes) < 0);

        Gen.Select(Gen.Select(Text, Text), Gen.Select(Text, Text)).Sample((a, b) =>
        {
            var expected = CompareByCodePoint(a.Item1, b.Item1);
            expected = expected != 0 ? expected : CompareByCodePoint(a.Item2, b.Item2);
            return Math.Sign(expected) == Compare(a, b, texts);
        });
    }

    /// <summary>Lo que no cabe se corta, y lo escrito es el principio de la clave entera: alcanza para comparar.</summary>
    [Fact]
    public void A_key_that_does_not_fit_is_cut_short_and_says_so()
    {
        var parts = Gen.Select(Text, Decimals, Gen.Guid, Doubles, Gen.Int[0, 40]);
        parts.Sample(static value =>
        {
            Encoder<(string, decimal, Guid, double, int)> encode = static (ref w, v) =>
            {
                w.Write(v.Item1);
                w.Write(v.Item2);
                w.Write(v.Item3);
                w.Write(v.Item4);
            };

            var whole = Encode(value, encode);
            var room = Math.Min(value.Item5, whole.Length);
            var buffer = new byte[room];
            var writer = new TkKeyWriter(buffer);
            encode(ref writer, value);

            return writer.Length == room
                && writer.IsTruncated == (room < whole.Length)
                && buffer.AsSpan().SequenceEqual(whole.AsSpan(0, room));
        });
    }

    private static void Ordered<T>(Gen<T> values, Encoder<T> encode, Comparison<T>? compare = null)
    {
        compare ??= Comparer<T>.Default.Compare;
        Gen.Select(values, values).Sample((a, b) => Math.Sign(compare(a, b)) == Compare(a, b, encode));
        values.Sample(value => Compare(value, value, encode) == 0);
    }

    private static int Compare<T>(T a, T b, Encoder<T> encode) =>
        Math.Sign(Encode(a, encode).AsSpan().SequenceCompareTo(Encode(b, encode)));

    private static byte[] Encode<T>(T value, Encoder<T> encode)
    {
        var writer = new TkKeyWriter(stackalloc byte[256]);
        encode(ref writer, value);
        Assert.False(writer.IsTruncated);
        return writer.Written.ToArray();
    }

    private static int CompareByCodePoint(string a, string b)
    {
        var left = a.EnumerateRunes().GetEnumerator();
        var right = b.EnumerateRunes().GetEnumerator();
        while (true)
        {
            var hasLeft = left.MoveNext();
            var hasRight = right.MoveNext();
            if (!hasLeft || !hasRight)
            {
                return hasLeft.CompareTo(hasRight);
            }

            if (left.Current != right.Current)
            {
                return left.Current.CompareTo(right.Current);
            }
        }
    }
}
