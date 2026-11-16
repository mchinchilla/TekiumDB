// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Catalog;

/// <summary>
/// How a type reads rows written with one schema version: for each stored column, which column of the
/// type receives it. Built once per version and reused for every row.
/// </summary>
internal sealed class ReadPlan
{
    private ReadPlan(TkColumn[] stored, int[] members)
    {
        Stored = stored;
        Members = members;
    }

    public TkColumn[] Stored { get; }

    /// <summary>Gets, for each stored column, the index of the column of the type that receives it, or -1 if the type no longer has it.</summary>
    public int[] Members { get; }

    /// <summary>
    /// Matches columns by name. A column the type added reads as its default; one it dropped is skipped.
    /// What cannot be bridged is refused here, naming the table and the column, rather than misread later.
    /// </summary>
    public static ReadPlan Build(string table, TableSchema stored, ReadOnlySpan<TkColumn> columns, int keyColumn)
    {
        var members = new int[stored.Columns.Length];
        for (var i = 0; i < members.Length; i++)
        {
            var column = stored.Columns[i];
            members[i] = -1;
            for (var member = 0; member < columns.Length; member++)
            {
                if (!string.Equals(columns[member].Name, column.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                if (columns[member].Type != column.Type)
                {
                    throw new TkSchemaException(
                        $"Table '{table}': column '{column.Name}' is stored as {column.Type} (schema version {stored.Version}) " +
                        $"but the type declares it as {columns[member].Type}. Changing the type of a column is not supported.");
                }

                if (column.IsNullable && !columns[member].IsNullable)
                {
                    throw new TkSchemaException(
                        $"Table '{table}': column '{column.Name}' may be null in stored rows (schema version {stored.Version}) " +
                        "but the type declares it as not nullable.");
                }

                members[i] = member;
                break;
            }
        }

        var storedKey = stored.Columns[stored.KeyColumn];
        if (members[stored.KeyColumn] != keyColumn)
        {
            throw new TkSchemaException(
                $"Table '{table}': its key is column '{storedKey.Name}' but the type declares '{columns[keyColumn].Name}' as the key. " +
                "The key of a table cannot change.");
        }

        return new ReadPlan(stored.Columns, members);
    }
}
