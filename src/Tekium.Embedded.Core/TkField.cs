// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Indexing;

namespace Tekium.Embedded;

/// <summary>
/// A column of a row type, or a value computed from its columns, to write conditions on. The generator
/// declares one per property in the nested <c>Fields</c> class of the row type.
/// </summary>
/// <remarks>
/// Values compare the way <c>CompareTo</c> does, with two exceptions: text compares by Unicode code
/// point, with no notion of culture, and a row with no value in the field meets no condition but
/// <see cref="IsNull"/>.
/// </remarks>
/// <typeparam name="TRow">The row type.</typeparam>
/// <typeparam name="TValue">The type of the value, without its nullable marker.</typeparam>
public sealed class TkField<TRow, TValue>
    where TRow : class, ITkRow<TRow>
{
    // In a nullable field every value starts with the mark that it is there, and no value is the lone mark below it.
    private static readonly byte[] Absent = [0];
    private static readonly byte[] FirstPresent = [1];
    private static readonly byte[] PastPresent = [2];

    private readonly int _ordinal;
    private readonly TkColumnType _type;
    private readonly bool _isNullable;

    /// <summary>Initializes a new instance of the <see cref="TkField{TRow, TValue}"/> class.</summary>
    /// <param name="ordinal">The position of the field: a column, or past the columns, an expression.</param>
    /// <param name="type">How the value is stored.</param>
    /// <param name="isNullable">Whether the field may have no value.</param>
    public TkField(int ordinal, TkColumnType type, bool isNullable)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        _ordinal = ordinal;
        _type = type;
        _isNullable = isNullable;
    }

    /// <summary>Accepts the rows whose field is this value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The condition.</returns>
    public TkFilter<TRow> EqualTo(TValue value)
    {
        var key = Encode(value);
        return Filter(key, true, key, true);
    }

    /// <summary>Accepts the rows whose field is below this value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The condition.</returns>
    public TkFilter<TRow> LessThan(TValue value) => Filter(Lowest, true, Encode(value), false);

    /// <summary>Accepts the rows whose field is this value or below it.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The condition.</returns>
    public TkFilter<TRow> AtMost(TValue value) => Filter(Lowest, true, Encode(value), true);

    /// <summary>Accepts the rows whose field is above this value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The condition.</returns>
    public TkFilter<TRow> GreaterThan(TValue value) => Filter(Encode(value), false, Highest, false);

    /// <summary>Accepts the rows whose field is this value or above it.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The condition.</returns>
    public TkFilter<TRow> AtLeast(TValue value) => Filter(Encode(value), true, Highest, false);

    /// <summary>Accepts the rows whose field is between two values, both included.</summary>
    /// <param name="from">The lowest value accepted.</param>
    /// <param name="to">The highest value accepted.</param>
    /// <returns>The condition.</returns>
    public TkFilter<TRow> Between(TValue from, TValue to) => Filter(Encode(from), true, Encode(to), true);

    /// <summary>Accepts the rows whose field has no value. None, if the field is not nullable.</summary>
    /// <returns>The condition.</returns>
    public TkFilter<TRow> IsNull() =>
        _isNullable ? Filter(Absent, true, Absent, true) : Filter(FirstPresent, true, Absent, true);

    /// <summary>Accepts the rows whose field has a value. All of them, if the field is not nullable.</summary>
    /// <returns>The condition.</returns>
    public TkFilter<TRow> IsNotNull() => Filter(Lowest, true, Highest, false);

    private byte[]? Lowest => _isNullable ? FirstPresent : null;

    private byte[]? Highest => _isNullable ? PastPresent : null;

    private TkFilter<TRow> Filter(byte[]? low, bool lowInclusive, byte[]? high, bool highInclusive) =>
        new(_ordinal, new KeyRange(low, lowInclusive, high, highInclusive));

    private byte[] Encode(TValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Span<byte> small = stackalloc byte[256];
        var buffer = small;
        while (true)
        {
            var writer = new TkKeyWriter(buffer);
            if (_isNullable)
            {
                writer.WritePresent();
            }

            TkKeyWriter.WriteValue(ref writer, value, _type);
            if (!writer.IsTruncated)
            {
                return writer.Written.ToArray();
            }

            buffer = new byte[buffer.Length * 4];
        }
    }
}
