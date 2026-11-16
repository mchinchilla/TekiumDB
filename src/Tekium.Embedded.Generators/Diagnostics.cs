// Copyright (c) Tekium. Licensed under the MIT License.

using Microsoft.CodeAnalysis;

namespace Tekium.Embedded.Generators;

internal static class Diagnostics
{
    private const string Category = "TekiumDB";

    public static readonly DiagnosticDescriptor NotPartial = Error(
        "TK0001",
        "A table type must be partial",
        "'{0}' is marked with [TkTable] but is not partial, so its serializer cannot be generated; add the 'partial' modifier, and to every type that contains it");

    public static readonly DiagnosticDescriptor NoKey = Error(
        "TK0002",
        "A table needs a key",
        "'{0}' has no key: mark exactly one property with [TkKey]");

    public static readonly DiagnosticDescriptor SeveralKeys = Error(
        "TK0003",
        "A table has one key",
        "'{0}' marks more than one property with [TkKey]: '{1}' is one too many");

    public static readonly DiagnosticDescriptor UnsupportedColumn = Error(
        "TK0004",
        "A property has a type that cannot be stored",
        "Property '{0}' is of type '{1}', which cannot be stored in a column; change its type or mark it with [TkIgnore]");

    public static readonly DiagnosticDescriptor UnsupportedKey = Error(
        "TK0005",
        "A property has a type that cannot be a key",
        "Property '{0}' is of type '{1}' and cannot be the key: a key is a non-nullable Guid, int, long, uint, ulong or string");

    public static readonly DiagnosticDescriptor NoConstructor = Error(
        "TK0006",
        "A table type needs a parameterless constructor",
        "'{0}' cannot be read back from the database because it has no parameterless constructor");

    public static readonly DiagnosticDescriptor UnsupportedType = Error(
        "TK0007",
        "This kind of type cannot be a table",
        "'{0}' cannot be a table: a table type is a non-abstract, non-generic class or record class");

    public static readonly DiagnosticDescriptor DuplicateColumn = Error(
        "TK0008",
        "Two properties are stored under the same name",
        "'{0}' stores more than one property as column '{1}'");

    public static readonly DiagnosticDescriptor NoColumns = Error(
        "TK0009",
        "A table needs columns",
        "'{0}' has no property that can be stored: a column is a public instance property with a getter and a setter");

    public static DiagnosticDescriptor? Find(string id) => id switch
    {
        "TK0001" => NotPartial,
        "TK0002" => NoKey,
        "TK0003" => SeveralKeys,
        "TK0004" => UnsupportedColumn,
        "TK0005" => UnsupportedKey,
        "TK0006" => NoConstructor,
        "TK0007" => UnsupportedType,
        "TK0008" => DuplicateColumn,
        "TK0009" => NoColumns,
        _ => null,
    };

    private static DiagnosticDescriptor Error(string id, string title, string message) =>
        new(id, title, message, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);
}
