// Copyright (c) Tekium. Licensed under the MIT License.

using Microsoft.EntityFrameworkCore;

namespace TekiumDB.EntityFrameworkCore;

/// <summary>
/// The JSON functions of TekiumDB, for queries: <c>EF.Functions.JsonValue(entity.Document, "$.sku")</c>.
/// They only run inside a query, translated to SQL; called directly they throw.
/// </summary>
public static class TkDbFunctionsExtensions
{
    private const string OnlyInQueries = "This function only runs in a query, as SQL.";

    /// <summary>JSON_VALUE: the value at a path as text, when it is a string, a number, true or false; null for nothing, null, an array or an object.</summary>
    /// <param name="functions">EF.Functions.</param>
    /// <param name="document">The JSON column.</param>
    /// <param name="path">The path, starting with $.</param>
    /// <returns>The value.</returns>
    public static string? JsonValue(this DbFunctions functions, TkJson document, string path) => throw new InvalidOperationException(OnlyInQueries);

    /// <summary>JSON_VALUE over a text property whose column is JSON or JSONB.</summary>
    /// <param name="functions">EF.Functions.</param>
    /// <param name="document">The JSON column.</param>
    /// <param name="path">The path, starting with $.</param>
    /// <returns>The value.</returns>
    public static string? JsonValue(this DbFunctions functions, string document, string path) => throw new InvalidOperationException(OnlyInQueries);

    /// <summary>JSON_EXISTS: whether the document has a value at the path, even null.</summary>
    /// <param name="functions">EF.Functions.</param>
    /// <param name="document">The JSON column.</param>
    /// <param name="path">The path, starting with $.</param>
    /// <returns>Whether it has one.</returns>
    public static bool JsonExists(this DbFunctions functions, TkJson document, string path) => throw new InvalidOperationException(OnlyInQueries);

    /// <summary>JSON_EXISTS over a text property whose column is JSON or JSONB.</summary>
    /// <param name="functions">EF.Functions.</param>
    /// <param name="document">The JSON column.</param>
    /// <param name="path">The path, starting with $.</param>
    /// <returns>Whether it has one.</returns>
    public static bool JsonExists(this DbFunctions functions, string document, string path) => throw new InvalidOperationException(OnlyInQueries);
}
