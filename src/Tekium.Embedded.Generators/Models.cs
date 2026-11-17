// Copyright (c) Tekium. Licensed under the MIT License.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Tekium.Embedded.Generators;

/// <summary>Everything the generator needs to know about one table, with no reference to the compilation.</summary>
internal sealed record TableModel(
    string HintName,
    string? Namespace,
    EquatableArray<string> ContainingTypes,
    string Declaration,
    string Name,
    string TableName,
    EquatableArray<ColumnModel> Columns,
    int KeyColumn,
    EquatableArray<ColumnModel> Computed,
    EquatableArray<IndexModel> Indexes,
    EquatableArray<CheckModel> Checks,
    EquatableArray<ForeignKeyModel> ForeignKeys,
    EquatableArray<DiagnosticModel> Diagnostics);

/// <summary>One CHECK constraint: its name, the bool property that evaluates it and the source of that property.</summary>
internal sealed record CheckModel(string Name, string Property, string Text);

/// <summary>One FOREIGN KEY constraint.</summary>
/// <param name="Name">Name of the constraint.</param>
/// <param name="Table">Name of the table referred to.</param>
/// <param name="Column">Position of the column that holds the key.</param>
/// <param name="Index">Position of the index that starts with that column.</param>
internal sealed record ForeignKeyModel(string Name, string Table, int Column, int Index);

/// <summary>One secondary index.</summary>
/// <param name="Name">Name of the index.</param>
/// <param name="Fields">The indexed fields: positions among the columns, or past them, among the computed properties.</param>
/// <param name="Where">The C# expression that builds the filter of a partial index, or nothing.</param>
/// <param name="Unique">Whether it is also a UNIQUE constraint.</param>
/// <param name="ZoneMinutes">For a zone map, the length of its windows in minutes. Zero for an index.</param>
internal sealed record IndexModel(string Name, EquatableArray<int> Fields, string? Where, bool Unique = false, int ZoneMinutes = 0);

/// <summary>One column: the property it comes from and how it is stored.</summary>
/// <param name="Property">Name of the property.</param>
/// <param name="ColumnName">Name the column is stored under.</param>
/// <param name="StoreType">Member of <c>TkColumnType</c>.</param>
/// <param name="Method">Suffix of the reader and writer methods, as in <c>ReadInt32</c>.</param>
/// <param name="StoredAs">C# type the reader returns.</param>
/// <param name="PropertyType">C# type of the property, fully qualified, without the nullable marker.</param>
/// <param name="IsNullable">Whether the property may hold no value.</param>
/// <param name="IsValueType">Whether the property type is a value type.</param>
/// <param name="IsEnum">Whether the property is an enum stored as its underlying integer.</param>
/// <param name="Expression">For a computed property, the source of its body. Empty for a column.</param>
internal sealed record ColumnModel(
    string Property,
    string ColumnName,
    string StoreType,
    string Method,
    string StoredAs,
    string PropertyType,
    bool IsNullable,
    bool IsValueType,
    bool IsEnum,
    string Expression = "");

/// <summary>A diagnostic to report, kept as plain data so that the model stays comparable.</summary>
internal sealed record DiagnosticModel(string Id, string Argument0, string Argument1, string FilePath, TextSpan Span, LinePositionSpan LineSpan)
{
    public static DiagnosticModel Create(string id, Location? location, string argument0, string argument1 = "")
    {
        var span = location?.GetLineSpan();
        return new DiagnosticModel(
            id,
            argument0,
            argument1,
            span?.Path ?? string.Empty,
            location?.SourceSpan ?? default,
            span?.Span ?? default);
    }

    public Location ToLocation() =>
        FilePath.Length == 0 ? Location.None : Location.Create(FilePath, Span, LineSpan);
}
