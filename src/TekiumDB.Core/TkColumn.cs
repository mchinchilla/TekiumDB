// Copyright (c) Tekium. Licensed under the MIT License.

namespace TekiumDB;

/// <summary>One column of a table: what the source generator declares and what the catalog stores.</summary>
public readonly struct TkColumn : IEquatable<TkColumn>
{
    /// <summary>The longest column name, in UTF-8 bytes.</summary>
    public const int MaxNameBytes = 128;

    private readonly string? _name;

    /// <summary>Initializes a new instance of the <see cref="TkColumn"/> struct.</summary>
    /// <param name="name">Name the column is stored under. Rows of older schema versions are matched by it.</param>
    /// <param name="type">How the value is stored.</param>
    /// <param name="isNullable">Whether a row may hold no value for the column.</param>
    public TkColumn(string name, TkColumnType type, bool isNullable)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (type is <= TkColumnType.None or > TkColumnType.Jsonb)
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown column type.");
        }

        if (System.Text.Encoding.UTF8.GetByteCount(name) > MaxNameBytes)
        {
            throw new ArgumentException($"A column name must not exceed {MaxNameBytes} UTF-8 bytes.", nameof(name));
        }

        _name = name;
        Type = type;
        IsNullable = isNullable;
    }

    /// <summary>Gets the name the column is stored under.</summary>
    public string Name => _name ?? string.Empty;

    /// <summary>Gets how the value is stored.</summary>
    public TkColumnType Type { get; }

    /// <summary>Gets a value indicating whether a row may hold no value for the column.</summary>
    public bool IsNullable { get; }

    /// <summary>Compares two columns.</summary>
    public static bool operator ==(TkColumn left, TkColumn right) => left.Equals(right);

    /// <summary>Compares two columns.</summary>
    public static bool operator !=(TkColumn left, TkColumn right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(TkColumn other) =>
        Type == other.Type && IsNullable == other.IsNullable && string.Equals(Name, other.Name, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TkColumn other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Name, Type, IsNullable);

    /// <inheritdoc />
    public override string ToString() => $"{Name} {Type}{(IsNullable ? "?" : string.Empty)}";
}
