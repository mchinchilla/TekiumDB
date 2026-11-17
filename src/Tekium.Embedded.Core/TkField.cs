// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Indexing;

namespace Tekium.Embedded;

/// <summary>
/// A field of a row type whose value type is not known where it is used: what a layer that builds
/// conditions from something other than code (a LINQ predicate, a SQL statement) gets when it asks a
/// row type for a field by name. <see cref="Accept{TResult}"/> hands it back with its value type.
/// </summary>
/// <typeparam name="TRow">The row type.</typeparam>
public abstract class TkField<TRow>
    where TRow : class, ITkRow<TRow>
{
    private protected TkField()
    {
    }

    /// <summary>Gets a value indicating whether the field may have no value.</summary>
    public abstract bool IsNullable { get; }

    /// <summary>Accepts the rows whose field has no value. None, if the field is not nullable.</summary>
    /// <returns>The condition.</returns>
    public abstract TkFilter<TRow> IsNull();

    /// <summary>Accepts the rows whose field has a value. All of them, if the field is not nullable.</summary>
    /// <returns>The condition.</returns>
    public abstract TkFilter<TRow> IsNotNull();

    /// <summary>Calls the visitor with this field as the typed field it is.</summary>
    /// <typeparam name="TResult">What the visitor returns.</typeparam>
    /// <param name="visitor">What to call.</param>
    /// <returns>What the visitor returned.</returns>
    public abstract TResult Accept<TResult>(ITkFieldVisitor<TRow, TResult> visitor);
}

/// <summary>Something to do with a field once its value type is known. See <see cref="TkField{TRow}.Accept{TResult}"/>.</summary>
/// <typeparam name="TRow">The row type.</typeparam>
/// <typeparam name="TResult">What it returns.</typeparam>
public interface ITkFieldVisitor<TRow, out TResult>
    where TRow : class, ITkRow<TRow>
{
    /// <summary>Receives the field with its value type.</summary>
    /// <typeparam name="TValue">The type of the value, without its nullable marker.</typeparam>
    /// <param name="field">The field.</param>
    /// <returns>Whatever the visitor computes.</returns>
    TResult Visit<TValue>(TkField<TRow, TValue> field);
}

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
public sealed class TkField<TRow, TValue> : TkField<TRow>
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

    /// <inheritdoc/>
    public override bool IsNullable => _isNullable;

    /// <inheritdoc/>
    public override TResult Accept<TResult>(ITkFieldVisitor<TRow, TResult> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        return visitor.Visit(this);
    }

    /// <inheritdoc/>
    public override TkFilter<TRow> IsNull() =>
        _isNullable ? Filter(Absent, true, Absent, true) : Filter(FirstPresent, true, Absent, true);

    /// <inheritdoc/>
    public override TkFilter<TRow> IsNotNull() => Filter(Lowest, true, Highest, false);

    /// <summary>Accepts the rows whose encoded value is from <paramref name="low"/> up to, not including, <paramref name="high"/>.</summary>
    internal TkFilter<TRow> Span(byte[]? low, byte[]? high) =>
        Filter(low is null ? Lowest : Present(low), true, high is null ? Highest : Present(high), false);

    private byte[]? Lowest => _isNullable ? FirstPresent : null;

    private byte[]? Highest => _isNullable ? PastPresent : null;

    private TkFilter<TRow> Filter(byte[]? low, bool lowInclusive, byte[]? high, bool highInclusive) =>
        new(_ordinal, new KeyRange(low, lowInclusive, high, highInclusive));

    private byte[] Present(byte[] encoded) => _isNullable ? [.. FirstPresent, .. encoded] : encoded;

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
