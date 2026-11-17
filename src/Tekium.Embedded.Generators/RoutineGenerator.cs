// Copyright (c) Tekium. Licensed under the MIT License.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Tekium.Embedded.Generators;

/// <summary>
/// For every static method marked with [TkFunction] or [TkProcedure], generates the code that hands it
/// its arguments, typed, and a table with all the routines of the assembly for the application to
/// register. No reflection: it is what lets SQL call C# under Native AOT.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class RoutineGenerator : IIncrementalGenerator
{
    private const string FunctionAttribute = "Tekium.Embedded.Sql.TkFunctionAttribute";
    private const string ProcedureAttribute = "Tekium.Embedded.Sql.TkProcedureAttribute";
    private const string Sql = "global::Tekium.Embedded.Sql.";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var functions = context.SyntaxProvider.ForAttributeWithMetadataName(
            FunctionAttribute,
            static (node, _) => node is MethodDeclarationSyntax,
            static (attributed, cancellationToken) => Parse(attributed, isProcedure: false, cancellationToken));
        var procedures = context.SyntaxProvider.ForAttributeWithMetadataName(
            ProcedureAttribute,
            static (node, _) => node is MethodDeclarationSyntax,
            static (attributed, cancellationToken) => Parse(attributed, isProcedure: true, cancellationToken));
        var all = functions.Collect().Combine(procedures.Collect()).Combine(context.CompilationProvider.Select(static (compilation, _) => compilation.AssemblyName ?? "Routines"));

        context.RegisterSourceOutput(all, static (output, input) =>
        {
            var routines = input.Left.Left.AddRange(input.Left.Right);
            if (routines.IsEmpty)
            {
                return;
            }

            var failed = false;
            var names = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var routine in routines)
            {
                foreach (var diagnostic in routine.Diagnostics)
                {
                    Report(output, diagnostic);
                    failed = true;
                }

                if (routine.Diagnostics.Count == 0 && !names.Add(routine.Name))
                {
                    Report(output, DiagnosticModel.Create("TK0023", routine.Location.ToLocation(), routine.Name));
                    failed = true;
                }
            }

            // With errors there is nothing sensible to generate; the diagnostics say why.
            if (!failed)
            {
                output.AddSource("TkRoutines.g.cs", SourceText.From(Emit(routines, input.Right), Encoding.UTF8));
            }
        });
    }

    private static void Report(SourceProductionContext output, DiagnosticModel diagnostic)
    {
        if (Diagnostics.Find(diagnostic.Id) is { } descriptor)
        {
            output.ReportDiagnostic(Diagnostic.Create(descriptor, diagnostic.ToLocation(), diagnostic.Argument0, diagnostic.Argument1));
        }
    }

    private static RoutineModel Parse(GeneratorAttributeSyntaxContext attributed, bool isProcedure, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var method = (IMethodSymbol)attributed.TargetSymbol;
        var attribute = attributed.Attributes[0];
        var location = method.Locations.FirstOrDefault();
        var name = attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string ?? string.Empty : string.Empty;
        var deterministic = Named(attribute, "Deterministic");
        var vectorized = Named(attribute, "Vectorized");
        var diagnostics = new List<DiagnosticModel>();
        var parameters = new List<ParameterModel>();
        var result = string.Empty;

        if (!method.IsStatic || method.IsGenericMethod || name.Length == 0 || !IsReachable(method))
        {
            diagnostics.Add(DiagnosticModel.Create("TK0019", location, method.Name));
        }
        else if (isProcedure)
        {
            var arguments = method.Parameters.ToList();
            var takesToken = arguments.Count > 0 && arguments[arguments.Count - 1].Type.ToDisplayString() == "System.Threading.CancellationToken";
            if (takesToken)
            {
                arguments.RemoveAt(arguments.Count - 1);
            }

            var awaited = Awaited(method.ReturnType, out var returnsValue);
            if (arguments.Count == 0 || arguments[0].Type.ToDisplayString() != "Tekium.Embedded.Sql.TkContext" || awaited is null
                || (returnsValue && Conversion(awaited, out _, out _) is null))
            {
                diagnostics.Add(DiagnosticModel.Create("TK0022", location, method.Name));
            }
            else
            {
                result = (returnsValue ? "V" : "N") + (takesToken ? "T" : string.Empty);
                foreach (var parameter in arguments.Skip(1))
                {
                    Add(parameter);
                }
            }
        }
        else if (vectorized)
        {
            var kinds = method.Parameters.Select(static (parameter, i) => SpanKind(parameter.Type)).ToList();
            var last = kinds.Count - 1;
            if (!method.ReturnsVoid || kinds.Count < 2 || kinds.Take(last).Any(static kind => kind is null || !kind.StartsWith("R", System.StringComparison.Ordinal))
                || kinds[last] is null || !kinds[last]!.StartsWith("W", System.StringComparison.Ordinal))
            {
                diagnostics.Add(DiagnosticModel.Create("TK0021", location, method.Name));
            }
            else
            {
                parameters.AddRange(kinds.Take(last).Select(static kind => new ParameterModel(kind!.Substring(1), false, false)));
                result = kinds[last]!.Substring(1);
            }
        }
        else
        {
            if (method.ReturnsVoid || Conversion(method.ReturnType, out _, out _) is null)
            {
                diagnostics.Add(DiagnosticModel.Create("TK0020", location, method.Name, $"it returns {method.ReturnType.ToDisplayString()}"));
            }

            foreach (var parameter in method.Parameters)
            {
                Add(parameter);
            }
        }

        return new RoutineModel(
            isProcedure ? "P" : vectorized ? "V" : "S",
            name,
            method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType)),
            deterministic,
            new EquatableArray<ParameterModel>(parameters.ToImmutableArray()),
            result,
            DiagnosticModel.Create(string.Empty, location, string.Empty),
            new EquatableArray<DiagnosticModel>(diagnostics.ToImmutableArray()));

        void Add(IParameterSymbol parameter)
        {
            if (Conversion(parameter.Type, out var nullable, out var span) is { } conversion && parameter.RefKind == RefKind.None)
            {
                parameters.Add(new ParameterModel(conversion, nullable, span));
            }
            else
            {
                diagnostics.Add(DiagnosticModel.Create("TK0020", location, method.Name, $"parameter '{parameter.Name}' is {parameter.Type.ToDisplayString()}"));
            }
        }
    }

    private static bool Named(AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(argument => argument.Key == name).Value.Value is true;

    /// <summary>Whether generated code in the same assembly can call the method: it and the types around it are public or internal.</summary>
    private static bool IsReachable(IMethodSymbol method)
    {
        for (ISymbol? symbol = method; symbol is IMethodSymbol or INamedTypeSymbol; symbol = symbol.ContainingType)
        {
            if (symbol.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal)
                || (symbol is INamedTypeSymbol { IsGenericType: true }))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The method of TkArgument that turns a SQL value into this type. Null if SQL has no value for the type.</summary>
    private static string? Conversion(ITypeSymbol type, out bool nullable, out bool span)
    {
        span = false;
        nullable = type.NullableAnnotation == NullableAnnotation.Annotated;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } wrapped)
        {
            nullable = true;
            type = wrapped.TypeArguments[0];
        }

        if (type.ToDisplayString() == "System.ReadOnlySpan<char>")
        {
            span = true;
            return "ToText";
        }

        if (type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte, Rank: 1 })
        {
            return "ToBytes";
        }

        return type.SpecialType switch
        {
            SpecialType.System_Boolean => "ToBoolean",
            SpecialType.System_Int32 => "ToInt32",
            SpecialType.System_Int64 => "ToInt64",
            SpecialType.System_Double => "ToDouble",
            SpecialType.System_Decimal => "ToDecimal",
            SpecialType.System_String => "ToText",
            SpecialType.System_DateTime => "ToDateTime",
            _ => type.ToDisplayString() switch
            {
                "System.Guid" => "ToGuid",
                "System.DateTimeOffset" => "ToDateTimeOffset",
                _ => null,
            },
        };
    }

    /// <summary>"R" or "W" (a read-only span or a span) followed by the element kind. Null if the type is neither, or of another element.</summary>
    private static string? SpanKind(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { TypeArguments.Length: 1 } named)
        {
            return null;
        }

        var kind = named.TypeArguments[0].SpecialType switch
        {
            SpecialType.System_Double => "F64",
            SpecialType.System_Single => "F32",
            SpecialType.System_Int64 => "I64",
            SpecialType.System_Int32 => "I32",
            _ => null,
        };
        return kind is null ? null : named.ConstructedFrom.ToDisplayString() switch
        {
            "System.ReadOnlySpan<T>" => "R" + kind,
            "System.Span<T>" => "W" + kind,
            _ => null,
        };
    }

    /// <summary>What awaiting the result of a procedure gives: the type itself for a task with no value. Null if it is not a task.</summary>
    private static ITypeSymbol? Awaited(ITypeSymbol type, out bool returnsValue)
    {
        returnsValue = false;
        if (type is not INamedTypeSymbol named)
        {
            return null;
        }

        var definition = named.ConstructedFrom.ToDisplayString();
        if (definition is "System.Threading.Tasks.ValueTask" or "System.Threading.Tasks.Task")
        {
            return named;
        }

        if (definition is "System.Threading.Tasks.ValueTask<TResult>" or "System.Threading.Tasks.Task<TResult>")
        {
            returnsValue = true;
            return named.TypeArguments[0];
        }

        return null;
    }

    private static string Emit(ImmutableArray<RoutineModel> routines, string assembly)
    {
        var source = new StringBuilder();
        source.AppendLine("// <auto-generated/>");
        source.AppendLine("#nullable enable");
        source.AppendLine();
        source.AppendLine($"namespace {Namespace(assembly)}");
        source.AppendLine("{");
        source.AppendLine("    /// <summary>The functions and procedures this assembly gives to SQL: register them with <c>database.UseRoutines(TkRoutines.All)</c>.</summary>");
        source.AppendLine("    [global::System.CodeDom.Compiler.GeneratedCode(\"Tekium.Embedded.Generators\", \"1.0\")]");
        source.AppendLine("    public static class TkRoutines");
        source.AppendLine("    {");
        source.AppendLine("        /// <summary>Gets every routine of the assembly.</summary>");
        source.AppendLine($"        public static global::System.Collections.Generic.IReadOnlyList<{Sql}TkRoutine> All {{ get; }} = new {Sql}TkRoutine[]");
        source.AppendLine("        {");
        for (var i = 0; i < routines.Length; i++)
        {
            var routine = routines[i];
            var id = i.ToString(CultureInfo.InvariantCulture);
            var count = routine.Parameters.Count.ToString(CultureInfo.InvariantCulture);
            var deterministic = routine.Deterministic ? "true" : "false";
            var name = Quote(routine.Name);
            source.AppendLine(routine.Kind switch
            {
                "S" => $"            {Sql}TkRoutine.Function({name}, {count}, {deterministic}, __tk{id}),",
                "V" => $"            {Sql}TkRoutine.VectorFunction({name}, new {Sql}TkVectorKind[] {{ {string.Join(", ", routine.Parameters.Select(static parameter => Sql + "TkVectorKind." + parameter.Conversion))} }}, {Sql}TkVectorKind.{routine.Result}, {deterministic}, __tk{id}),",
                _ => $"            {Sql}TkRoutine.StoredProcedure({name}, {count}, __tk{id}),",
            });
        }

        source.AppendLine("        };");
        for (var i = 0; i < routines.Length; i++)
        {
            var routine = routines[i];
            var id = i.ToString(CultureInfo.InvariantCulture);
            source.AppendLine();
            switch (routine.Kind)
            {
                case "S":
                    source.AppendLine($"        private static object? __tk{id}(global::System.ReadOnlySpan<object?> a)");
                    source.AppendLine("        {");
                    Guards(source, routine, "return null;");
                    source.AppendLine($"            return {routine.Method}({Arguments(routine)});");
                    source.AppendLine("        }");
                    break;

                case "V":
                    var spans = routine.Parameters.Select(static (parameter, n) => $"f.{Span(parameter.Conversion)}s({n.ToString(CultureInfo.InvariantCulture)})")
                        .Concat(new[] { $"f.{Span(routine.Result)}Results()" });
                    source.AppendLine($"        private static void __tk{id}({Sql}TkVectorFrame f) => {routine.Method}({string.Join(", ", spans)});");
                    break;

                default:
                    source.AppendLine($"        private static async global::System.Threading.Tasks.ValueTask<object?> __tk{id}({Sql}TkContext c, object?[] a, global::System.Threading.CancellationToken t)");
                    source.AppendLine("        {");
                    Guards(source, routine, $"throw new {Sql}TkSqlException(\"Procedure \" + {Quote(routine.Name)} + \" was given NULL for an argument that cannot be NULL.\");");
                    var arguments = string.Join(", ", new[] { "c", Arguments(routine), routine.Result.EndsWith("T", System.StringComparison.Ordinal) ? "t" : string.Empty }.Where(static part => part.Length > 0));
                    if (routine.Result.StartsWith("V", System.StringComparison.Ordinal))
                    {
                        source.AppendLine($"            return await {routine.Method}({arguments}).ConfigureAwait(false);");
                    }
                    else
                    {
                        source.AppendLine($"            await {routine.Method}({arguments}).ConfigureAwait(false);");
                        source.AppendLine("            return null;");
                    }

                    source.AppendLine("        }");
                    break;
            }
        }

        source.AppendLine("    }");
        source.AppendLine("}");
        return source.ToString();
    }

    /// <summary>NULL for a parameter that cannot be null: the routine is not called.</summary>
    private static void Guards(StringBuilder source, RoutineModel routine, string otherwise)
    {
        for (var i = 0; i < routine.Parameters.Count; i++)
        {
            if (!routine.Parameters[i].Nullable)
            {
                source.AppendLine($"            if (a[{i.ToString(CultureInfo.InvariantCulture)}] is null)");
                source.AppendLine("            {");
                source.AppendLine($"                {otherwise}");
                source.AppendLine("            }");
                source.AppendLine();
            }
        }
    }

    private static string Arguments(RoutineModel routine) =>
        string.Join(", ", routine.Parameters.Select(static (parameter, i) =>
        {
            var index = i.ToString(CultureInfo.InvariantCulture);
            var value = $"{Sql}TkArgument.{parameter.Conversion}(a[{index}]!)";
            if (parameter.Span)
            {
                value = $"global::System.MemoryExtensions.AsSpan({value})";
            }

            return parameter.Nullable && !parameter.Span ? $"a[{index}] is null ? null : {value}" : value;
        }));

    private static string Span(string kind) => kind switch
    {
        "F64" => "Double",
        "F32" => "Single",
        "I64" => "Int64",
        _ => "Int32",
    };

    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>The name of the assembly as a namespace.</summary>
    private static string Namespace(string assembly)
    {
        var parts = assembly.Split('.').Select(static part =>
        {
            var builder = new StringBuilder();
            foreach (var c in part)
            {
                builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            }

            return builder.Length == 0 || char.IsDigit(builder[0]) ? "_" + builder : builder.ToString();
        });
        return string.Join(".", parts);
    }
}

/// <summary>A method marked for SQL. <c>Kind</c> is S for a scalar function, V for a vectorized one, P for a procedure.</summary>
internal sealed record RoutineModel(
    string Kind,
    string Name,
    string Method,
    bool Deterministic,
    EquatableArray<ParameterModel> Parameters,
    string Result,
    DiagnosticModel Location,
    EquatableArray<DiagnosticModel> Diagnostics);

/// <summary>A parameter of a routine: how its value is converted (or its element kind, if vectorized), and whether it accepts NULL.</summary>
internal sealed record ParameterModel(string Conversion, bool Nullable, bool Span);
