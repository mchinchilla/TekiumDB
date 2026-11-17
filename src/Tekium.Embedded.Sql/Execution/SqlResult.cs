// Copyright (c) Tekium. Licensed under the MIT License.

using System.Globalization;
using Tekium.Embedded.Sql.Storage;

namespace Tekium.Embedded.Sql.Execution;

/// <summary>What a statement returned: rows with named columns, or how many rows it changed.</summary>
internal sealed class SqlResult
{
    public string[] Names { get; init; } = [];

    public Type[] Types { get; init; } = [];

    public List<object?[]> Rows { get; init; } = [];

    /// <summary>Gets the rows an INSERT, UPDATE or DELETE changed; -1 for anything else.</summary>
    public int RecordsAffected { get; init; } = -1;
}

/// <summary>A step of the plan of a query: what it does, how many rows it was expected to give, and how many it gave.</summary>
internal sealed class PlanNode(string text, long? estimated)
{
    public string Text { get; } = text;

    public long? Estimated { get; } = estimated;

    public long Actual { get; set; }

    public List<PlanNode> Children { get; } = [];
}

/// <summary>Values that are equal as SQL sees them: 1, 1.0 and 1e0 are one group, one join key, one DISTINCT row.</summary>
internal sealed class SqlKey(object?[] values) : IEquatable<SqlKey>
{
    private readonly object[] _values = Array.ConvertAll(values, Normalize);

    public bool Equals(SqlKey? other) => other is not null && _values.AsSpan().SequenceEqual(other._values);

    public override bool Equals(object? obj) => Equals(obj as SqlKey);

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (var value in _values)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    private static object Normalize(object? value) => value switch
    {
        null => DBNull.Value,
        float or double => Convert.ToDouble(value, CultureInfo.InvariantCulture) is var floating && floating == Math.Floor(floating) && Math.Abs(floating) < 1e15
            ? (decimal)floating
            : floating,
        _ when SqlValues.IsNumber(value) => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToHexString(bytes),
        DateTimeOffset moment => moment.UtcDateTime,
        char one => one.ToString(),
        _ => value,
    };
}
