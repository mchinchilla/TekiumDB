// Copyright (c) Tekium. Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using Tekium.Embedded.Sql.Parsing;
using Tekium.Embedded.Sql.Storage;

namespace Tekium.Embedded.Sql.Execution;

/// <summary>
/// Reading JSON documents from SQL. What each function gives back is what SQLite gives for the same
/// document and path: a value at a path as the SQL value it is (<c>-&gt;&gt;</c>, <c>JSON_EXTRACT</c>), or
/// as a JSON document (<c>-&gt;</c>), whose text has no spaces and keeps the order of a <c>json</c> column.
/// </summary>
internal static class SqlJson
{
    public static Evaluator Arrow(Evaluator document, Evaluator path, SqlExpr pathExpression, bool asValue)
    {
        var fixedPath = pathExpression is Literal { Value: { } constant } ? PathOf(constant, arrow: true) : null;
        return row =>
        {
            if (document(row) is not { } value || (fixedPath is null && path(row) is not { }))
            {
                return null;
            }

            var json = Document(value);
            var at = fixedPath ?? PathOf(path(row)!, arrow: true);
            return json.TryGet(at, out var found) ? (asValue ? Scalar(found) : Compact(found)) : null;
        };
    }

    /// <summary>A function of a document and a path. Null if it is not one of these.</summary>
    public static Evaluator? Function(string name, Evaluator[] arguments, IReadOnlyList<SqlExpr> expressions)
    {
        switch (name)
        {
            case "JSON" when arguments.Length == 1:
                return row => arguments[0](row) is { } value ? Document(value).ToText() : null;
            case "JSONB" when arguments.Length == 1:
                return row => arguments[0](row) is { } value ? Document(value).ToBinary() : null;
            case "JSON_EXTRACT" when arguments.Length == 2:
                return Read(arguments, expressions, static found => Scalar(found));
            case "JSON_VALUE" when arguments.Length == 2:
                return Read(arguments, expressions, static found => found.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? null : Scalar(found));
            case "JSON_QUERY" when arguments.Length == 2:
                return Read(arguments, expressions, static found => found.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? Compact(found) : null);
            case "JSON_EXISTS" when arguments.Length == 2:
                var exists = Read(arguments, expressions, static _ => true);
                return row => arguments[0](row) is null ? null : exists(row) is true;
            case "JSON_TYPE" when arguments.Length is 1 or 2:
                return arguments.Length == 1
                    ? row => arguments[0](row) is { } value ? TypeOf(Document(value)) : null
                    : Read(arguments, expressions, static found => TypeOf(found));
            default:
                return null;
        }
    }

    /// <summary>A value as a document: a document is itself; text must be one.</summary>
    public static TkJson Document(object value) => value switch
    {
        TkJson json => json,
        string text => TkJson.TryParse(text, out var json) ? json : throw new TkSqlException($"{SqlValues.Show(value)} is not valid JSON: {Why(text)}"),
        _ => throw new TkSqlException($"{SqlValues.Show(value)} is not a JSON document."),
    };

    /// <summary>A value at a path as the SQL value it is: NULL, a boolean, a number, a text, or the text of an array or object.</summary>
    public static object? Scalar(TkJson found) => found.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => found.GetNumber(),
        JsonValueKind.String => found.GetString(),
        _ => Compact(found).ToString(),
    };

    private static Evaluator Read(Evaluator[] arguments, IReadOnlyList<SqlExpr> expressions, Func<TkJson, object?> answer)
    {
        var fixedPath = expressions[1] is Literal { Value: { } constant } ? PathOf(constant, arrow: false) : null;
        return row =>
        {
            if (arguments[0](row) is not { } value || (fixedPath is null && arguments[1](row) is not { }))
            {
                return null;
            }

            var at = fixedPath ?? PathOf(arguments[1](row)!, arrow: false);
            return Document(value).TryGet(at, out var found) ? answer(found) : null;
        };
    }

    /// <summary>
    /// A path: text starting with $. After an arrow a name or a position also is one, as in SQLite:
    /// <c>doc -&gt; 'a'</c> is <c>$.a</c> and <c>doc -&gt; 2</c> is <c>$[2]</c>.
    /// </summary>
    private static TkJsonPath PathOf(object value, bool arrow)
    {
        switch (value)
        {
            case string text when text.StartsWith('$'):
                try
                {
                    return TkJsonPath.Parse(text);
                }
                catch (FormatException invalid)
                {
                    throw new TkSqlException(invalid.Message, invalid);
                }

            case string name when arrow:
                return TkJsonPath.Parse("$.\"" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"");
            case long or int or short or byte when arrow && Convert.ToInt64(value, CultureInfo.InvariantCulture) is var index and >= 0 and <= int.MaxValue:
                return TkJsonPath.Parse($"$[{index.ToString(CultureInfo.InvariantCulture)}]");
            default:
                throw new TkSqlException($"{SqlValues.Show(value)} is not a JSON path: a path must start with $.");
        }
    }

    /// <summary>A value as a document with no spaces: a json column keeps its order, repeated keys and the text of its numbers.</summary>
    private static TkJson Compact(TkJson found) => found.Format == TkJsonFormat.Binary ? found : found.Compact();

    private static string TypeOf(TkJson found) => found.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "text",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => found.IsWholeNumber() ? "integer" : "real",
    };

    private static string Why(string text)
    {
        try
        {
            _ = TkJson.Parse(text);
            return "it is";
        }
        catch (FormatException invalid)
        {
            return invalid.Message;
        }
    }
}
