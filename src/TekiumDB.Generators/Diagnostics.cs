// Copyright (c) Tekium. Licensed under the MIT License.

using Microsoft.CodeAnalysis;

namespace TekiumDB.Generators;

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

    public static readonly DiagnosticDescriptor NotIndexable = Error(
        "TK0010",
        "An index names a property that cannot be indexed",
        "Index '{0}' cannot use '{1}': an indexed property is a column, or a public property with a getter body and no setter whose type could be stored");

    public static readonly DiagnosticDescriptor DuplicateIndex = Error(
        "TK0011",
        "Two indexes have the same name",
        "'{0}' declares more than one index named '{1}'; give one of them another Name");

    public static readonly DiagnosticDescriptor UnsupportedCondition = Error(
        "TK0012",
        "The condition of a partial index cannot be understood",
        "Index '{0}' cannot use '{1}' as its condition: it must be a bool property of the table whose body compares columns with constants (==, <, <=, >, >=, is null, is not null), joined by &&");

    public static readonly DiagnosticDescriptor IndexWithoutColumns = Error(
        "TK0013",
        "An index declared on the class must name its properties",
        "'{0}' has a [TkIndex] with no properties: name them, or put the attribute on the property to index");

    public static readonly DiagnosticDescriptor FieldsTaken = Error(
        "TK0014",
        "A table type cannot have a member named Fields",
        "'{0}' has a member named 'Fields', the name of the class generated to write conditions with; rename it");

    public static readonly DiagnosticDescriptor UnsupportedCheck = Error(
        "TK0015",
        "A CHECK constraint must be a bool property with a body",
        "'{0}' cannot be a CHECK constraint: [TkCheck] goes on an instance property of type bool that computes its value from the columns of the row");

    public static readonly DiagnosticDescriptor UnsupportedForeignKey = Error(
        "TK0016",
        "A FOREIGN KEY must be a column of the type of the key it refers to",
        "'{0}' cannot be a FOREIGN KEY to '{1}': it must be a column, '{1}' must be marked with [TkTable], and the column must have the type of its key");

    public static readonly DiagnosticDescriptor DuplicateConstraint = Error(
        "TK0017",
        "Two constraints have the same name",
        "'{0}' declares more than one constraint named '{1}'; give one of them another Name");

    public static readonly DiagnosticDescriptor UnsupportedZoneMap = Error(
        "TK0018",
        "A zone map must be on a date column",
        "'{0}' cannot have a zone map: [TkZoneMap] goes on a column of type DateTimeOffset, DateTime or DateOnly, with windows of at least one minute");

    public static readonly DiagnosticDescriptor RoutineNotCallable = Error(
        "TK0019",
        "A SQL routine must be a static method generated code can call",
        "'{0}' cannot be a SQL routine: [TkFunction] and [TkProcedure] go on a static, non-generic method that is public or internal, in types that are too, with a name");

    public static readonly DiagnosticDescriptor RoutineType = Error(
        "TK0020",
        "A SQL function uses a type SQL has no value for",
        "'{0}' cannot be a SQL function: {1}. A function takes and returns bool, int, long, double, decimal, string, Guid, DateTime, DateTimeOffset or byte[], and may take ReadOnlySpan<char>.");

    public static readonly DiagnosticDescriptor VectorShape = Error(
        "TK0021",
        "A vectorized function takes spans in and a span out",
        "'{0}' cannot be a vectorized function: it must return nothing and take one ReadOnlySpan<T> per argument followed by a Span<T> for the results, with T one of double, float, long or int");

    public static readonly DiagnosticDescriptor ProcedureShape = Error(
        "TK0022",
        "A procedure takes a TkContext first and returns a task",
        "'{0}' cannot be a SQL procedure: its first parameter must be a TkContext and it must return a ValueTask or a Task, with or without a value SQL can hold");

    public static readonly DiagnosticDescriptor DuplicateRoutine = Error(
        "TK0023",
        "Two SQL routines have the same name",
        "The assembly declares more than one SQL routine named '{0}'");

    public static readonly DiagnosticDescriptor JsonIndexed = Error(
        "TK0024",
        "A JSON document is not indexed whole",
        "'{0}' is a JSON document, which has no order of its own: index a value at a path of it with [TkJsonIndex]");

    public static readonly DiagnosticDescriptor JsonTextMisplaced = Error(
        "TK0025",
        "[TkJsonText] goes on a TkJson property",
        "'{0}' is not a TkJson: [TkJsonText] says how a JSON document is kept, and only a TkJson property is one");

    public static readonly DiagnosticDescriptor JsonIndexMisplaced = Error(
        "TK0026",
        "[TkJsonIndex] indexes a TEXT or DOUBLE value at a path of a TkJson property",
        "[TkJsonIndex] on '{0}' cannot be: {1}");

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
        "TK0010" => NotIndexable,
        "TK0011" => DuplicateIndex,
        "TK0012" => UnsupportedCondition,
        "TK0013" => IndexWithoutColumns,
        "TK0014" => FieldsTaken,
        "TK0015" => UnsupportedCheck,
        "TK0016" => UnsupportedForeignKey,
        "TK0017" => DuplicateConstraint,
        "TK0018" => UnsupportedZoneMap,
        "TK0019" => RoutineNotCallable,
        "TK0020" => RoutineType,
        "TK0021" => VectorShape,
        "TK0022" => ProcedureShape,
        "TK0023" => DuplicateRoutine,
        "TK0024" => JsonIndexed,
        "TK0025" => JsonTextMisplaced,
        "TK0026" => JsonIndexMisplaced,
        _ => null,
    };

    private static DiagnosticDescriptor Error(string id, string title, string message) =>
        new(id, title, message, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);
}
