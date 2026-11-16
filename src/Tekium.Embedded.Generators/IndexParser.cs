// Copyright (c) Tekium. Licensed under the MIT License.

using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Tekium.Embedded.Generators;

/// <summary>Reads the [TkIndex] attributes of a table type: the indexes, and the computed properties they need.</summary>
internal sealed class IndexParser
{
    private readonly INamedTypeSymbol _type;
    private readonly IReadOnlyList<ColumnModel> _columns;
    private readonly List<ColumnModel> _computed = new List<ColumnModel>();
    private readonly Compilation _compilation;
    private readonly CancellationToken _cancellationToken;

    private IndexParser(
        INamedTypeSymbol type, IReadOnlyList<ColumnModel> columns, Compilation compilation, CancellationToken cancellationToken)
    {
        _type = type;
        _columns = columns;
        _compilation = compilation;
        _cancellationToken = cancellationToken;
    }

    public static ImmutableArray<IndexModel> Parse(
        INamedTypeSymbol type,
        IReadOnlyList<ColumnModel> columns,
        Location typeLocation,
        ImmutableArray<DiagnosticModel>.Builder diagnostics,
        Compilation compilation,
        CancellationToken cancellationToken,
        out ImmutableArray<ColumnModel> computed)
    {
        var parser = new IndexParser(type, columns, compilation, cancellationToken);
        var indexes = ImmutableArray.CreateBuilder<IndexModel>();

        // Those on the class first, then those on the properties, base classes first.
        var declared = new List<(AttributeData Attribute, IPropertySymbol? Property)>();
        declared.AddRange(TableParser.IndexAttributes(type).Select(static data => (data, (IPropertySymbol?)null)));
        foreach (var member in TableParser.AllMembers(type).Reverse())
        {
            if (member is IPropertySymbol property)
            {
                declared.AddRange(TableParser.IndexAttributes(property).Select(data => (data, (IPropertySymbol?)property)));
            }
        }

        foreach (var (attribute, property) in declared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = property?.Locations.FirstOrDefault() ?? typeLocation;
            var names = attribute.ConstructorArguments is { Length: 1 } arguments && arguments[0].Kind == TypedConstantKind.Array
                ? arguments[0].Values.Select(static value => value.Value as string ?? string.Empty).ToList()
                : new List<string>();
            if (names.Count == 0 && property is not null)
            {
                names.Add(property.Name);
            }

            if (names.Count == 0)
            {
                diagnostics.Add(DiagnosticModel.Create("TK0013", location, type.Name));
                continue;
            }

            var name = Named(attribute, "Name") is { Length: > 0 } given ? given : string.Join("_", names);
            var fields = ImmutableArray.CreateBuilder<int>();
            var valid = true;
            foreach (var member in names)
            {
                var field = parser.Field(member);
                if (field < 0 || fields.Contains(field))
                {
                    diagnostics.Add(DiagnosticModel.Create("TK0010", location, name, member));
                    valid = false;
                }

                fields.Add(field);
            }

            string? where = null;
            if (Named(attribute, "Where") is { Length: > 0 } condition)
            {
                where = parser.Condition(condition);
                if (where is null)
                {
                    diagnostics.Add(DiagnosticModel.Create("TK0012", location, name, condition));
                    valid = false;
                }
            }

            if (indexes.Any(existing => existing.Name == name))
            {
                diagnostics.Add(DiagnosticModel.Create("TK0011", location, type.Name, name));
                valid = false;
            }

            if (valid)
            {
                indexes.Add(new IndexModel(name, new EquatableArray<int>(fields.ToImmutable()), where));
            }
        }

        computed = parser._computed.ToImmutableArray();
        return indexes.ToImmutable();
    }

    private static string? Named(AttributeData attribute, string name)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key == name)
            {
                return argument.Value.Value as string;
            }
        }

        return null;
    }

    private static string Plain(string identifier) => identifier.TrimStart('@');

    private static string? Literal(object value)
    {
        switch (value)
        {
            case bool boolean:
                return boolean ? "true" : "false";
            case string text:
                return SymbolDisplay.FormatLiteral(text, quote: true);
            case char character:
                return SymbolDisplay.FormatLiteral(character, quote: true);
            case sbyte or byte or short or ushort or int or long:
                return Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + "L";
            case uint or ulong:
                return Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + "UL";
            case float single when !float.IsNaN(single) && !float.IsInfinity(single):
                return single.ToString("R", CultureInfo.InvariantCulture) + "F";
            case double number when !double.IsNaN(number) && !double.IsInfinity(number):
                return number.ToString("R", CultureInfo.InvariantCulture) + "D";
            case decimal exact:
                return exact.ToString(CultureInfo.InvariantCulture) + "M";
            default:
                return null;
        }
    }

    private ColumnModel Model(int field) => field < _columns.Count ? _columns[field] : _computed[field - _columns.Count];

    /// <summary>The field a property is, or -1: a column, or a computed property, which becomes a field on first use.</summary>
    private int Field(string propertyName)
    {
        for (var i = 0; i < _columns.Count; i++)
        {
            if (Plain(_columns[i].Property) == propertyName)
            {
                return i;
            }
        }

        for (var i = 0; i < _computed.Count; i++)
        {
            if (Plain(_computed[i].Property) == propertyName)
            {
                return _columns.Count + i;
            }
        }

        var property = Property(propertyName);
        if (property is not { IsStatic: false, IsIndexer: false, DeclaredAccessibility: Accessibility.Public, GetMethod: not null, SetMethod: null })
        {
            return -1;
        }

        var body = Body(property);
        if (body is null || TableParser.ToColumn(property, body.NormalizeWhitespace(eol: "\n").ToFullString()) is not { } model)
        {
            return -1;
        }

        _computed.Add(model);
        return _columns.Count + _computed.Count - 1;
    }

    private IPropertySymbol? Property(string name) =>
        TableParser.AllMembers(_type).OfType<IPropertySymbol>().FirstOrDefault(property => property.Name == name);

    /// <summary>The expression or block a property computes its value with, if it has one.</summary>
    private SyntaxNode? Body(IPropertySymbol property)
    {
        if (property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(_cancellationToken) is not PropertyDeclarationSyntax syntax)
        {
            return null;
        }

        if (syntax.ExpressionBody is { } arrow)
        {
            return arrow.Expression;
        }

        var getter = syntax.AccessorList?.Accessors.FirstOrDefault(static accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration));
        return getter?.ExpressionBody?.Expression ?? (SyntaxNode?)getter?.Body;
    }

    /// <summary>The C# expression that builds the filter a bool property stands for, or null if it cannot be one.</summary>
    private string? Condition(string propertyName)
    {
        if (Property(propertyName) is not { IsStatic: false, Type.SpecialType: SpecialType.System_Boolean } property
            || Body(property) is not ExpressionSyntax body)
        {
            return null;
        }

        var terms = new List<string>();
        var model = _compilation.GetSemanticModel(body.SyntaxTree);
        if (!Terms(body, model, terms) || terms.Count == 0)
        {
            return null;
        }

        var filter = terms[0];
        for (var i = 1; i < terms.Count; i++)
        {
            filter += ".And(" + terms[i] + ")";
        }

        return filter;
    }

    private bool Terms(ExpressionSyntax expression, SemanticModel model, List<string> terms)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return Terms(parenthesized.Expression, model, terms);

            case BinaryExpressionSyntax both when both.IsKind(SyntaxKind.LogicalAndExpression):
                return Terms(both.Left, model, terms) && Terms(both.Right, model, terms);

            case PrefixUnaryExpressionSyntax negated when negated.IsKind(SyntaxKind.LogicalNotExpression):
                return Compare(negated.Operand, "EqualTo", false, model, terms, mustBeBoolean: true);

            case IsPatternExpressionSyntax { Pattern: ConstantPatternSyntax constant } isNull when IsNullLiteral(constant.Expression):
                return Test(isNull.Expression, "IsNull", model, terms);

            case IsPatternExpressionSyntax { Pattern: UnaryPatternSyntax { Pattern: ConstantPatternSyntax constant } } isNotNull
                when IsNullLiteral(constant.Expression):
                return Test(isNotNull.Expression, "IsNotNull", model, terms);

            case BinaryExpressionSyntax binary:
                return Comparison(binary, model, terms);

            default:
                return Compare(expression, "EqualTo", true, model, terms, mustBeBoolean: true);
        }
    }

    private bool Comparison(BinaryExpressionSyntax binary, SemanticModel model, List<string> terms)
    {
        var (leftFirst, flipped) = binary.Kind() switch
        {
            SyntaxKind.EqualsExpression => ("EqualTo", "EqualTo"),
            SyntaxKind.NotEqualsExpression => ("NotEqual", "NotEqual"),
            SyntaxKind.LessThanExpression => ("LessThan", "GreaterThan"),
            SyntaxKind.LessThanOrEqualExpression => ("AtMost", "AtLeast"),
            SyntaxKind.GreaterThanExpression => ("GreaterThan", "LessThan"),
            SyntaxKind.GreaterThanOrEqualExpression => ("AtLeast", "AtMost"),
            _ => (null, null),
        };
        if (leftFirst is null)
        {
            return false;
        }

        // The constant may be on either side: 5 < Age is Age > 5.
        var (member, constant, method) = FieldOf(binary.Left, model) >= 0
            ? (binary.Left, binary.Right, leftFirst)
            : (binary.Right, binary.Left, flipped!);
        if (IsNullLiteral(constant))
        {
            return method switch
            {
                "EqualTo" => Test(member, "IsNull", model, terms),
                "NotEqual" => Test(member, "IsNotNull", model, terms),
                _ => false,
            };
        }

        return method != "NotEqual"
            && model.GetConstantValue(constant, _cancellationToken) is { HasValue: true, Value: { } value }
            && Compare(member, method, value, model, terms, mustBeBoolean: false);
    }

    private static bool IsNullLiteral(ExpressionSyntax expression) => expression.IsKind(SyntaxKind.NullLiteralExpression);

    private bool Test(ExpressionSyntax member, string method, SemanticModel model, List<string> terms)
    {
        var field = FieldOf(member, model);
        if (field < 0)
        {
            return false;
        }

        terms.Add($"Fields.{Model(field).Property}.{method}()");
        return true;
    }

    private bool Compare(ExpressionSyntax member, string method, object value, SemanticModel model, List<string> terms, bool mustBeBoolean)
    {
        var field = FieldOf(member, model);
        if (field < 0 || Literal(value) is not { } literal || (mustBeBoolean && Model(field).StoreType != "Boolean"))
        {
            return false;
        }

        var column = Model(field);
        var cast = column.IsEnum ? $"({column.PropertyType})({column.StoredAs})" : $"({column.PropertyType})";
        terms.Add($"Fields.{column.Property}.{method}({cast}({literal}))");
        return true;
    }

    /// <summary>The field an expression reads, if it is a plain reference to a property of the table.</summary>
    private int FieldOf(ExpressionSyntax expression, SemanticModel model)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        var isReference = expression is IdentifierNameSyntax
            || expression is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax };
        return isReference && model.GetSymbolInfo(expression, _cancellationToken).Symbol is IPropertySymbol { IsStatic: false } property
            ? Field(property.Name)
            : -1;
    }
}
