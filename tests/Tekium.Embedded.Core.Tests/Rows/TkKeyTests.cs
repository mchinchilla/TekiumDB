// Copyright (c) Tekium. Licensed under the MIT License.

using CsCheck;

namespace Tekium.Embedded.Tests.Rows;

/// <summary>El árbol sólo compara bytes: el orden de las claves codificadas tiene que ser el de los valores.</summary>
public sealed class TkKeyTests
{
    [Fact]
    public void Encoded_integers_sort_like_the_integers()
    {
        Gen.Select(Gen.Int, Gen.Int).Sample(static (a, b) =>
            Math.Sign(a.CompareTo(b)) == Compare(a, b, static (value, span) => TkKey.Write(value, span)));
        Gen.Select(Gen.Long, Gen.Long).Sample(static (a, b) =>
            Math.Sign(a.CompareTo(b)) == Compare(a, b, static (value, span) => TkKey.Write(value, span)));
        Gen.Select(Gen.UInt, Gen.UInt).Sample(static (a, b) =>
            Math.Sign(a.CompareTo(b)) == Compare(a, b, static (value, span) => TkKey.Write(value, span)));
        Gen.Select(Gen.ULong, Gen.ULong).Sample(static (a, b) =>
            Math.Sign(a.CompareTo(b)) == Compare(a, b, static (value, span) => TkKey.Write(value, span)));

        Assert.True(Compare(-1, 0, static (value, span) => TkKey.Write(value, span)) < 0);
        Assert.True(Compare(long.MinValue, long.MaxValue, static (value, span) => TkKey.Write(value, span)) < 0);
    }

    [Fact]
    public void Encoded_strings_sort_by_code_point()
    {
        var text = Gen.Char.Where(static c => !char.IsSurrogate(c)).Array[1, 6].Select(static chars => new string(chars));
        Gen.Select(text, text).Sample(static (a, b) =>
            Math.Sign(CompareByCodePoint(a, b)) == Compare(a, b, static (value, span) => TkKey.Write(value, span)));
    }

    [Fact]
    public void Uuid_v7_keys_sort_by_the_time_they_were_created()
    {
        var epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Gen.Select(Gen.Int[0, 100_000_000], Gen.Int[0, 100_000_000]).Where(static pair => pair.Item1 != pair.Item2).Sample((a, b) =>
            Math.Sign(a.CompareTo(b)) == Compare(
                Guid.CreateVersion7(epoch.AddMilliseconds(a)),
                Guid.CreateVersion7(epoch.AddMilliseconds(b)),
                static (value, span) => TkKey.Write(value, span)));
    }

    [Fact]
    public void A_key_that_cannot_be_written_is_refused()
    {
        Assert.Throws<ArgumentException>(static () => TkKey.Write(string.Empty, new byte[8]));
        Assert.Throws<ArgumentException>(static () => TkKey.Write("too long for this", new byte[4]));
        Assert.Throws<ArgumentException>(static () => TkKey.Write("\ud800", new byte[8]));
        Assert.Throws<ArgumentException>(static () => TkKey.Write(Guid.Empty, new byte[15]));
        Assert.Throws<ArgumentException>(static () => TkKey.Write(1L, new byte[7]));
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

    private delegate int Encoder<in T>(T value, Span<byte> destination);

    private static int Compare<T>(T a, T b, Encoder<T> encode)
    {
        Span<byte> left = stackalloc byte[64];
        Span<byte> right = stackalloc byte[64];
        left = left[..encode(a, left)];
        right = right[..encode(b, right)];
        return Math.Sign(left.SequenceCompareTo(right));
    }
}
