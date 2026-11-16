// Copyright (c) Tekium. Licensed under the MIT License.

using CsCheck;
using Tekium.Embedded.Indexing;

namespace Tekium.Embedded.Tests.Indexing;

/// <summary>
/// El planificador razona sobre rangos de claves: de que estas preguntas se contesten bien depende que
/// un índice parcial nunca devuelva de menos. El modelo es probar clave por clave.
/// </summary>
public sealed class KeyRangeTests
{
    // Todas las claves de hasta tres bytes sobre un alfabeto de tres: bastan para caer en cada borde.
    private static readonly byte[][] Universe =
    [
        .. from length in Enumerable.Range(0, 4)
           from number in Enumerable.Range(0, (int)Math.Pow(3, length))
           select Enumerable.Range(0, length).Select(position => (byte)(number / (int)Math.Pow(3, position) % 3)).ToArray(),
    ];

    private static readonly Gen<byte[]?> Bound = Gen.OneOf(
        Gen.Const((byte[]?)null),
        Gen.Byte[0, 2].Array[0, 2].Select(static bytes => (byte[]?)bytes));

    private static readonly Gen<KeyRange> Ranges =
        Gen.Select(Bound, Gen.Bool, Bound, Gen.Bool).Select(static parts => new KeyRange(parts.Item1, parts.Item2, parts.Item3, parts.Item4));

    [Fact]
    public void A_range_covers_another_only_if_it_accepts_every_key_the_other_does()
    {
        Gen.Select(Ranges, Ranges).Sample(static (outer, inner) =>
            !outer.Covers(inner) || Universe.All(key => !inner.Contains(key) || outer.Contains(key)));
    }

    [Fact]
    public void A_range_covers_the_ranges_inside_it_and_not_the_ones_that_stick_out()
    {
        var from1To2 = new KeyRange([1], true, [2], true);
        var above1 = new KeyRange([1], false, null, false);

        Assert.True(from1To2.Covers(from1To2));
        Assert.True(from1To2.Covers(new KeyRange([1], false, [2], false)));
        Assert.True(from1To2.Covers(new KeyRange([1, 5], true, [1, 5], true)));
        Assert.True(above1.Covers(new KeyRange([1], false, [9], true)));
        Assert.True(above1.Covers(new KeyRange([1, 0], true, null, false)));
        Assert.True(new KeyRange(null, false, null, false).Covers(above1));

        // El borde que uno excluye y el otro no; un lado sin cota; un rango más ancho.
        Assert.False(above1.Covers(new KeyRange([1], true, [9], true)));
        Assert.False(new KeyRange(null, false, [2], false).Covers(new KeyRange([0], true, [2], true)));
        Assert.False(from1To2.Covers(new KeyRange([1], true, null, false)));
        Assert.False(from1To2.Covers(new KeyRange(null, false, [2], true)));
        Assert.False(from1To2.Covers(new KeyRange([0], true, [2], true)));
        Assert.False(from1To2.Covers(new KeyRange([1], true, [2, 0], true)));
        Assert.False(above1.Covers(new KeyRange(null, false, null, false)));
    }

    [Fact]
    public void The_intersection_accepts_exactly_the_keys_both_accept()
    {
        Gen.Select(Ranges, Ranges).Sample(static (a, b) =>
        {
            var both = a.Intersect(b);
            return Universe.All(key => both.Contains(key) == (a.Contains(key) && b.Contains(key)));
        });
    }

    [Fact]
    public void An_empty_range_accepts_no_key_and_a_point_exactly_one()
    {
        Ranges.Sample(static range => !range.IsEmpty || Universe.All(key => !range.Contains(key)));
        Ranges.Sample(static range => !range.IsPoint || Universe.Count(key => range.Contains(key)) == 1);

        Assert.True(new KeyRange([2], true, [1], true).IsEmpty);
        Assert.True(new KeyRange([1], true, [1], false).IsEmpty);
        Assert.False(new KeyRange([1], true, [1], true).IsEmpty);
        Assert.True(new KeyRange([1], true, [1], true).IsPoint);
        Assert.False(new KeyRange([1], true, [2], true).IsPoint);
    }

    /// <summary>Una clave cortada, si es más larga que las cotas, se compara como la clave entera.</summary>
    [Fact]
    public void A_key_cut_short_past_the_bounds_is_judged_like_the_whole_key()
    {
        Gen.Select(Ranges, Gen.Byte[0, 2].Array[3, 6]).Sample(static (range, key) =>
            range.Contains(key) == range.Contains(key.AsSpan(0, 3)));
    }
}
