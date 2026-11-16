// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Indexing;

/// <summary>
/// The values a condition accepts, as a range of encoded keys. Because the encoding keeps the order of
/// the values, everything a planner needs to ask about conditions is a question about byte ranges.
/// </summary>
/// <param name="Low">The lowest key accepted, or <see langword="null"/> for no lower bound.</param>
/// <param name="LowInclusive">Whether <paramref name="Low"/> itself is accepted.</param>
/// <param name="High">The highest key accepted, or <see langword="null"/> for no upper bound.</param>
/// <param name="HighInclusive">Whether <paramref name="High"/> itself is accepted.</param>
internal sealed record KeyRange(byte[]? Low, bool LowInclusive, byte[]? High, bool HighInclusive)
{
    /// <summary>Gets a value indicating whether no key can be in the range.</summary>
    public bool IsEmpty =>
        Low is not null && High is not null
        && Low.AsSpan().SequenceCompareTo(High) is var order
        && (order > 0 || (order == 0 && !(LowInclusive && HighInclusive)));

    /// <summary>Gets a value indicating whether the range accepts exactly one key.</summary>
    public bool IsPoint =>
        Low is not null && High is not null && LowInclusive && HighInclusive && Low.AsSpan().SequenceEqual(High);

    /// <summary>The keys both ranges accept.</summary>
    public KeyRange Intersect(KeyRange other)
    {
        var (low, lowInclusive) = (Low, LowInclusive);
        if (other.Low is not null)
        {
            var order = low is null ? -1 : low.AsSpan().SequenceCompareTo(other.Low);
            (low, lowInclusive) = order switch
            {
                < 0 => (other.Low, other.LowInclusive),
                0 => (low, lowInclusive && other.LowInclusive),
                _ => (low, lowInclusive),
            };
        }

        var (high, highInclusive) = (High, HighInclusive);
        if (other.High is not null)
        {
            var order = high is null ? 1 : high.AsSpan().SequenceCompareTo(other.High);
            (high, highInclusive) = order switch
            {
                > 0 => (other.High, other.HighInclusive),
                0 => (high, highInclusive && other.HighInclusive),
                _ => (high, highInclusive),
            };
        }

        return new KeyRange(low, lowInclusive, high, highInclusive);
    }

    /// <summary>Whether every key <paramref name="other"/> accepts is accepted by this range too.</summary>
    public bool Covers(KeyRange other)
    {
        if (other.IsEmpty)
        {
            return true;
        }

        if (Low is not null)
        {
            if (other.Low is null)
            {
                return false;
            }

            var order = other.Low.AsSpan().SequenceCompareTo(Low);
            if (order < 0 || (order == 0 && other.LowInclusive && !LowInclusive))
            {
                return false;
            }
        }

        if (High is not null)
        {
            if (other.High is null)
            {
                return false;
            }

            var order = other.High.AsSpan().SequenceCompareTo(High);
            if (order > 0 || (order == 0 && other.HighInclusive && !HighInclusive))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the range accepts a key. The key may have been cut short, as long as it is longer than
    /// both bounds: it then compares against them the way the whole key would.
    /// </summary>
    public bool Contains(ReadOnlySpan<byte> key)
    {
        if (Low is not null)
        {
            var order = key.SequenceCompareTo(Low);
            if (order < 0 || (order == 0 && !LowInclusive))
            {
                return false;
            }
        }

        if (High is not null)
        {
            var order = key.SequenceCompareTo(High);
            if (order > 0 || (order == 0 && !HighInclusive))
            {
                return false;
            }
        }

        return true;
    }
}
