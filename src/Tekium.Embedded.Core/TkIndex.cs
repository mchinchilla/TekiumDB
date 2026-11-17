// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded;

/// <summary>
/// A secondary index of a row type, as the generator declares it from <see cref="TkIndexAttribute"/>.
/// </summary>
/// <typeparam name="TRow">The row type.</typeparam>
public sealed class TkIndex<TRow>
    where TRow : class, ITkRow<TRow>
{
    private readonly int[] _fields;

    /// <summary>Initializes a new instance of the <see cref="TkIndex{TRow}"/> class.</summary>
    /// <param name="name">The name of the index, unique in its table.</param>
    /// <param name="fields">The indexed fields, most significant first: columns, or past the columns, expressions.</param>
    /// <param name="where">The rows that are in the index, or <see langword="null"/> for all of them.</param>
    /// <param name="isUnique">Whether two rows may not have the same values in the index.</param>
    /// <param name="zoneMinutes">For a zone map on a date field, the length of its windows in minutes. Zero for an index.</param>
    public TkIndex(string name, ReadOnlySpan<int> fields, TkFilter<TRow>? where = null, bool isUnique = false, int zoneMinutes = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(zoneMinutes);
        ZoneMinutes = zoneMinutes;
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        _fields = fields.ToArray();
        Where = where;
        IsUnique = isUnique;
    }

    /// <summary>Gets the length in minutes of the windows of a zone map. Zero if this is an index.</summary>
    public int ZoneMinutes { get; }

    /// <summary>Gets a value indicating whether the index is also a UNIQUE constraint.</summary>
    public bool IsUnique { get; }

    /// <summary>Gets the name of the index.</summary>
    public string Name { get; }

    /// <summary>Gets the indexed fields, most significant first.</summary>
    public ReadOnlySpan<int> Fields => _fields;

    /// <summary>Gets the condition a row must meet to be in the index, if it is a partial index.</summary>
    public TkFilter<TRow>? Where { get; }
}
