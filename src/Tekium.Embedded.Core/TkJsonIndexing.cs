// Copyright (c) Tekium. Licensed under the MIT License.

using System.ComponentModel;
using System.Text.Json;

namespace Tekium.Embedded;

/// <summary>
/// The value an index on a path holds for a document. It is the one function both the row type and SQL
/// use, so the two keep the same entries: a string (or the text of an array or object, as <c>-&gt;&gt;</c>
/// gives it) for a TEXT index, a number for a DOUBLE one, and nothing otherwise.
/// </summary>
public static class TkJsonIndexing
{
    /// <summary>Writes the value of a document at a path as a field of an index key. Used by generated code.</summary>
    /// <param name="writer">The key being written.</param>
    /// <param name="document">The document, or null if the column has none.</param>
    /// <param name="path">The path.</param>
    /// <param name="type">The kind of value the index holds.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void Write(ref TkKeyWriter writer, TkJson? document, TkJsonPath path, TkColumnType type)
    {
        switch (Value(document, path, type))
        {
            case string text:
                writer.WritePresent();
                writer.Write(text);
                break;
            case double number:
                writer.WritePresent();
                writer.Write(number);
                break;
            default:
                writer.WriteNull();
                break;
        }
    }

    /// <summary>The value an index on a path holds: a <see cref="string"/>, a <see cref="double"/>, or null.</summary>
    internal static object? Value(TkJson? document, TkJsonPath path, TkColumnType type)
    {
        if (document is not { } json || !json.TryGet(path, out var found))
        {
            return null;
        }

        return (type, found.ValueKind) switch
        {
            (TkColumnType.Text, JsonValueKind.String) => found.GetString(),
            (TkColumnType.Text, JsonValueKind.Object or JsonValueKind.Array) => found.Compact().ToString(),
            (TkColumnType.F64, JsonValueKind.Number) => found.GetDouble(),
            _ => null,
        };
    }
}
