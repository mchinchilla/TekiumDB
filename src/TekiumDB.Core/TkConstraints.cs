// Copyright (c) Tekium. Licensed under the MIT License.

namespace TekiumDB;

/// <summary>A CHECK constraint of a row type, as the generator declares it from <see cref="TkCheckAttribute"/>.</summary>
public readonly struct TkCheck : IEquatable<TkCheck>
{
    private readonly string? _name;
    private readonly string? _text;

    /// <summary>Initializes a new instance of the <see cref="TkCheck"/> struct.</summary>
    /// <param name="name">The name of the constraint, unique among the constraints of its table.</param>
    /// <param name="text">The source of the condition. The rows already stored are checked again when it changes.</param>
    public TkCheck(string name, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(text);
        _name = name;
        _text = text;
    }

    /// <summary>Gets the name of the constraint.</summary>
    public string Name => _name ?? string.Empty;

    /// <summary>Gets the source of the condition.</summary>
    public string Text => _text ?? string.Empty;

    /// <summary>Compares two constraints.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they are the same.</returns>
    public static bool operator ==(TkCheck left, TkCheck right) => left.Equals(right);

    /// <summary>Compares two constraints.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(TkCheck left, TkCheck right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(TkCheck other) =>
        string.Equals(Name, other.Name, StringComparison.Ordinal) && string.Equals(Text, other.Text, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TkCheck other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Name, Text);

    /// <inheritdoc />
    public override string ToString() => $"CHECK {Name} ({Text})";
}

/// <summary>
/// A FOREIGN KEY constraint of a row type, as the generator declares it from <see cref="TkForeignKeyAttribute"/>.
/// </summary>
public readonly struct TkForeignKey : IEquatable<TkForeignKey>
{
    private readonly string? _name;
    private readonly string? _table;

    /// <summary>Initializes a new instance of the <see cref="TkForeignKey"/> struct.</summary>
    /// <param name="name">The name of the constraint, unique among the constraints of its table.</param>
    /// <param name="table">The name of the table referred to.</param>
    /// <param name="column">The position of the column that holds the key of the row referred to.</param>
    /// <param name="index">The position, among the indexes of the row type, of one that starts with that column.</param>
    public TkForeignKey(string name, string table, int column, int index)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(table);
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        _name = name;
        _table = table;
        Column = column;
        Index = index;
    }

    /// <summary>Gets the name of the constraint.</summary>
    public string Name => _name ?? string.Empty;

    /// <summary>Gets the name of the table referred to.</summary>
    public string Table => _table ?? string.Empty;

    /// <summary>Gets the position of the column that holds the key of the row referred to.</summary>
    public int Column { get; }

    /// <summary>Gets the position of the index that finds the rows that refer to a given row.</summary>
    public int Index { get; }

    /// <summary>Compares two constraints.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they are the same.</returns>
    public static bool operator ==(TkForeignKey left, TkForeignKey right) => left.Equals(right);

    /// <summary>Compares two constraints.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(TkForeignKey left, TkForeignKey right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(TkForeignKey other) =>
        Column == other.Column
        && Index == other.Index
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(Table, other.Table, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TkForeignKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Name, Table, Column, Index);

    /// <inheritdoc />
    public override string ToString() => $"FOREIGN KEY {Name} REFERENCES {Table}";
}
