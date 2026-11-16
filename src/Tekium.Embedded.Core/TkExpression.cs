// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded;

/// <summary>
/// A value computed from the columns of a row, which is not stored but can be indexed and filtered by.
/// </summary>
public readonly struct TkExpression : IEquatable<TkExpression>
{
    private readonly string? _name;
    private readonly string? _text;

    /// <summary>Initializes a new instance of the <see cref="TkExpression"/> struct.</summary>
    /// <param name="name">The name of the property.</param>
    /// <param name="type">The type of the value.</param>
    /// <param name="isNullable">Whether it may have no value.</param>
    /// <param name="text">The source of the expression. An index on it is rebuilt when this changes.</param>
    public TkExpression(string name, TkColumnType type, bool isNullable, string text)
    {
        // Validates the name and the type the way a column does.
        _name = new TkColumn(name, type, isNullable).Name;
        ArgumentNullException.ThrowIfNull(text);
        _text = text;
        Type = type;
        IsNullable = isNullable;
    }

    /// <summary>Gets the name of the property.</summary>
    public string Name => _name ?? string.Empty;

    /// <summary>Gets the type of the value.</summary>
    public TkColumnType Type { get; }

    /// <summary>Gets a value indicating whether the expression may have no value.</summary>
    public bool IsNullable { get; }

    /// <summary>Gets the source of the expression.</summary>
    public string Text => _text ?? string.Empty;

    /// <summary>Compares two expressions.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they are the same.</returns>
    public static bool operator ==(TkExpression left, TkExpression right) => left.Equals(right);

    /// <summary>Compares two expressions.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(TkExpression left, TkExpression right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(TkExpression other) =>
        Type == other.Type
        && IsNullable == other.IsNullable
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(Text, other.Text, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TkExpression other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Name, Type, IsNullable, Text);

    /// <inheritdoc />
    public override string ToString() => $"{Name} = {Text}";
}
