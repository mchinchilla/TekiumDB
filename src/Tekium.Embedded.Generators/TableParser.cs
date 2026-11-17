// Copyright (c) Tekium. Licensed under the MIT License.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Tekium.Embedded.Generators;

/// <summary>Reads a class marked with [TkTable] and turns it into a <see cref="TableModel"/>, or into diagnostics.</summary>
internal static class TableParser
{
    public const string TableAttribute = "Tekium.Embedded.TkTableAttribute";
    private const string KeyAttribute = "Tekium.Embedded.TkKeyAttribute";
    private const string ColumnAttribute = "Tekium.Embedded.TkColumnAttribute";
    private const string IgnoreAttribute = "Tekium.Embedded.TkIgnoreAttribute";
    private const string IndexAttribute = "Tekium.Embedded.TkIndexAttribute";
    internal const string CheckAttribute = "Tekium.Embedded.TkCheckAttribute";
    internal const string ZoneMapAttribute = "Tekium.Embedded.TkZoneMapAttribute";
    internal const string ForeignKeyAttribute = "Tekium.Embedded.TkForeignKeyAttribute";
    internal const string KeyAttributeName = KeyAttribute;

    private static readonly SymbolDisplayFormat FullyQualified =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    public static TableModel Parse(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        var type = (INamedTypeSymbol)context.TargetSymbol;
        var declaration = (TypeDeclarationSyntax)context.TargetNode;
        var location = declaration.Identifier.GetLocation();
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticModel>();
        var columns = ImmutableArray.CreateBuilder<ColumnModel>();
        var keyColumn = -1;

        if (type.IsAbstract || type.IsGenericType || type.IsStatic || type.TypeKind != TypeKind.Class)
        {
            diagnostics.Add(DiagnosticModel.Create("TK0007", location, type.Name));
        }

        var containing = ImmutableArray.CreateBuilder<string>();
        var partial = declaration.Modifiers.Any(SyntaxKind.PartialKeyword);
        for (var outer = declaration.Parent; outer is TypeDeclarationSyntax outerType; outer = outer.Parent)
        {
            partial &= outerType.Modifiers.Any(SyntaxKind.PartialKeyword);
            containing.Insert(0, Declare(outerType));
        }

        if (!partial)
        {
            diagnostics.Add(DiagnosticModel.Create("TK0001", location, type.Name));
        }

        if (!type.InstanceConstructors.Any(static constructor => constructor.Parameters.IsEmpty))
        {
            diagnostics.Add(DiagnosticModel.Create("TK0006", location, type.Name));
        }

        foreach (var property in Properties(type))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isKey = Has(property, KeyAttribute);
            var propertyLocation = property.Locations.FirstOrDefault();
            var column = ToColumn(property);
            if (column is null)
            {
                diagnostics.Add(DiagnosticModel.Create(
                    "TK0004", propertyLocation, property.Name, property.Type.ToDisplayString()));
                continue;
            }

            if (columns.Any(existing => existing.ColumnName == column.ColumnName))
            {
                diagnostics.Add(DiagnosticModel.Create("TK0008", propertyLocation, type.Name, column.ColumnName));
                continue;
            }

            if (isKey)
            {
                if (keyColumn >= 0)
                {
                    diagnostics.Add(DiagnosticModel.Create("TK0003", propertyLocation, type.Name, property.Name));
                }
                else if (column.IsNullable || column.IsEnum || !IsKeyType(column.StoreType))
                {
                    diagnostics.Add(DiagnosticModel.Create(
                        "TK0005", propertyLocation, property.Name, property.Type.ToDisplayString()));
                }

                keyColumn = columns.Count;
            }

            columns.Add(column);
        }

        if (columns.Count == 0)
        {
            diagnostics.Add(DiagnosticModel.Create("TK0009", location, type.Name));
        }
        else if (keyColumn < 0)
        {
            diagnostics.Add(DiagnosticModel.Create("TK0002", location, type.Name));
        }

        if (AllMembers(type).Any(static member => member.Name == "Fields"))
        {
            diagnostics.Add(DiagnosticModel.Create("TK0014", location, type.Name));
        }

        var indexes = IndexParser.Parse(
            type, columns, location, diagnostics, context.SemanticModel.Compilation, cancellationToken, out var computed, out var checks, out var foreignKeys);

        var tableName = context.Attributes[0].ConstructorArguments is { Length: 1 } arguments && arguments[0].Value is string name
            ? name
            : type.Name;

        return new TableModel(
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted))
                .Replace('<', '_').Replace('>', '_') + ".TkTable.g.cs",
            type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
            new EquatableArray<string>(containing.ToImmutable()),
            Declare(declaration),
            type.Name,
            tableName,
            new EquatableArray<ColumnModel>(columns.ToImmutable()),
            keyColumn,
            new EquatableArray<ColumnModel>(computed),
            new EquatableArray<IndexModel>(indexes),
            new EquatableArray<CheckModel>(checks),
            new EquatableArray<ForeignKeyModel>(foreignKeys),
            new EquatableArray<DiagnosticModel>(diagnostics.ToImmutable()));
    }

    private static string Declare(TypeDeclarationSyntax type)
    {
        var keyword = type is RecordDeclarationSyntax record
            ? "record " + record.ClassOrStructKeyword.ValueText
            : type.Keyword.ValueText;
        return $"partial {keyword.Trim()} {type.Identifier.ValueText}{type.TypeParameterList}";
    }

    /// <summary>Public instance properties with a getter and a setter, base classes first, in declaration order.</summary>
    private static IEnumerable<IPropertySymbol> Properties(INamedTypeSymbol type)
    {
        var chain = new Stack<INamedTypeSymbol>();
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            chain.Push(current);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (chain.Count > 0)
        {
            foreach (var member in chain.Pop().GetMembers())
            {
                if (member is IPropertySymbol
                    {
                        IsStatic: false,
                        IsIndexer: false,
                        DeclaredAccessibility: Accessibility.Public,
                        GetMethod: not null,
                        SetMethod: not null,
                    } property
                    && !Has(property, IgnoreAttribute)
                    && seen.Add(property.Name))
                {
                    yield return property;
                }
            }
        }
    }

    /// <summary>Every member of the type and of its base classes.</summary>
    internal static IEnumerable<ISymbol> AllMembers(INamedTypeSymbol type)
    {
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                yield return member;
            }
        }
    }

    /// <summary>The properties of the type and of its base classes, base classes first, in declaration order.</summary>
    internal static IEnumerable<IPropertySymbol> PropertiesInOrder(INamedTypeSymbol type)
    {
        var chain = new Stack<INamedTypeSymbol>();
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            chain.Push(current);
        }

        while (chain.Count > 0)
        {
            foreach (var property in chain.Pop().GetMembers().OfType<IPropertySymbol>())
            {
                yield return property;
            }
        }
    }

    internal static IEnumerable<AttributeData> IndexAttributes(ISymbol symbol) =>
        symbol.GetAttributes().Where(static data => data.AttributeClass?.ToDisplayString() == IndexAttribute);

    internal static bool Has(ISymbol symbol, string attribute) =>
        symbol.GetAttributes().Any(data => data.AttributeClass?.ToDisplayString() == attribute);

    private static bool IsKeyType(string storeType) =>
        storeType is "Uuid" or "I32" or "I64" or "U32" or "U64" or "Text";

    internal static ColumnModel? ToColumn(IPropertySymbol property, string expression = "")
    {
        var type = property.Type;
        var nullable = false;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } wrapped)
        {
            type = wrapped.TypeArguments[0];
            nullable = true;
        }
        else if (type.IsReferenceType)
        {
            // Without nullable annotations a reference may be null, and it is stored as such.
            nullable = property.NullableAnnotation != NullableAnnotation.NotAnnotated;
        }

        var stored = type;
        var isEnum = false;
        if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying })
        {
            stored = underlying;
            isEnum = true;
        }

        var mapping = Map(stored);
        if (mapping is null)
        {
            return null;
        }

        var columnName = property.Name;
        foreach (var attribute in property.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == ColumnAttribute
                && attribute.ConstructorArguments is { Length: 1 } arguments
                && arguments[0].Value is string name
                && name.Length > 0)
            {
                columnName = name;
            }
        }

        // A property named like a keyword is written @class in code, and stored as class.
        var identifier = SyntaxFacts.GetKeywordKind(property.Name) != SyntaxKind.None
            || SyntaxFacts.GetContextualKeywordKind(property.Name) != SyntaxKind.None
            ? "@" + property.Name
            : property.Name;

        return new ColumnModel(
            identifier,
            columnName,
            mapping.Value.StoreType,
            mapping.Value.Method,
            stored.ToDisplayString(FullyQualified),
            type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(FullyQualified),
            nullable,
            type.IsValueType,
            isEnum,
            expression);
    }

    private static (string StoreType, string Method)? Map(ITypeSymbol type)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean: return ("Boolean", "Boolean");
            case SpecialType.System_Byte: return ("U8", "Byte");
            case SpecialType.System_SByte: return ("I8", "SByte");
            case SpecialType.System_Int16: return ("I16", "Int16");
            case SpecialType.System_UInt16: return ("U16", "UInt16");
            case SpecialType.System_Int32: return ("I32", "Int32");
            case SpecialType.System_UInt32: return ("U32", "UInt32");
            case SpecialType.System_Int64: return ("I64", "Int64");
            case SpecialType.System_UInt64: return ("U64", "UInt64");
            case SpecialType.System_Single: return ("F32", "Single");
            case SpecialType.System_Double: return ("F64", "Double");
            case SpecialType.System_Decimal: return ("Numeric", "Decimal");
            case SpecialType.System_Char: return ("Character", "Char");
            case SpecialType.System_String: return ("Text", "String");
            case SpecialType.System_DateTime: return ("DateTime", "DateTime");
        }

        if (type is IArrayTypeSymbol { IsSZArray: true, ElementType.SpecialType: SpecialType.System_Byte })
        {
            return ("Bytes", "Bytes");
        }

        return type.ToDisplayString() switch
        {
            "System.Guid" => ("Uuid", "Guid"),
            "System.DateTimeOffset" => ("DateTimeOffset", "DateTimeOffset"),
            "System.TimeSpan" => ("TimeSpan", "TimeSpan"),
            "System.DateOnly" => ("DateOnly", "DateOnly"),
            "System.TimeOnly" => ("TimeOnly", "TimeOnly"),
            _ => null,
        };
    }
}
