// Copyright (c) Tekium. Licensed under the MIT License.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TekiumDB.Generators;

namespace TekiumDB.Tests.Generators;

/// <summary>
/// El generador de rutinas, corrido dentro de la prueba: lo que genera compila sin advertencias, y lo que
/// no puede ser una rutina es un error de compilación que dice por qué.
/// </summary>
public sealed class RoutineGeneratorTests
{
    private static readonly ImmutableArray<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path)),
    ];

    [Fact]
    public void Routines_of_every_kind_generate_code_that_compiles_without_warnings()
    {
        var (diagnostics, generated) = Run("""
            #nullable enable
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using TekiumDB.Sql;

            namespace Shop.Functions
            {
                public static class Money
                {
                    [TkFunction("with_tax", Deterministic = true)]
                    public static decimal WithTax(decimal amount, double rate, bool round, int? digits) => amount;

                    [TkFunction("label")]
                    internal static string? Label(ReadOnlySpan<char> text, string? suffix, Guid id, DateTime day, DateTimeOffset at, byte[] raw, long? big) => null;

                    [TkFunction("delta", Vectorized = true)]
                    public static void Delta(ReadOnlySpan<double> a, ReadOnlySpan<float> b, ReadOnlySpan<long> c, ReadOnlySpan<int> d, Span<double> result) { }

                    [TkProcedure("archive")]
                    public static async ValueTask<int> Archive(TkContext context, DateTimeOffset before, string? note, CancellationToken token) { await Task.Yield(); return 1; }

                    [TkProcedure("touch")]
                    public static Task Touch(TkContext context) => Task.CompletedTask;

                    [TkProcedure("count_all")]
                    public static Task<long> CountAll(TkContext context, int minimum) => Task.FromResult(0L);
                }
            }
            """);

        Assert.Empty(diagnostics);
        var code = Assert.Single(generated);
        Assert.Contains("namespace Generated.Tests", code, StringComparison.Ordinal);
        Assert.Contains("public static class TkRoutines", code, StringComparison.Ordinal);
        Assert.Contains("TkRoutine.Function(\"with_tax\", 4, true, __tk0)", code, StringComparison.Ordinal);
        Assert.Contains("TkRoutine.Function(\"label\", 7, false, __tk1)", code, StringComparison.Ordinal);
        Assert.Contains("TkVectorKind.F64, global::TekiumDB.Sql.TkVectorKind.F32, global::TekiumDB.Sql.TkVectorKind.I64, global::TekiumDB.Sql.TkVectorKind.I32 }, global::TekiumDB.Sql.TkVectorKind.F64, false", code, StringComparison.Ordinal);
        Assert.Contains("f.Doubles(0), f.Singles(1), f.Int64s(2), f.Int32s(3), f.DoubleResults()", code, StringComparison.Ordinal);
        Assert.Contains("TkRoutine.StoredProcedure(\"archive\", 2, __tk3)", code, StringComparison.Ordinal);

        // Sin reflexión ni compilación en caliente: lo generado sólo llama a los métodos por su nombre.
        Assert.DoesNotContain("typeof", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Reflection", code, StringComparison.Ordinal);
        Assert.DoesNotContain(".Compile(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Expression", code, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_routines_nothing_is_generated()
    {
        var (diagnostics, generated) = Run("public static class Nothing { public static int F(int x) => x; }");
        Assert.Empty(diagnostics);
        Assert.Empty(generated);
    }

    [Theory]
    [InlineData("TK0019", "[TkFunction(\"f\")] public int F(int x) => x;")]
    [InlineData("TK0019", "[TkFunction(\"f\")] private static int F(int x) => x;")]
    [InlineData("TK0019", "[TkFunction(\"f\")] public static T F<T>(T x) => x;")]
    [InlineData("TK0019", "[TkFunction(\"\")] public static int F(int x) => x;")]
    [InlineData("TK0019", "private static class Hidden { [TkFunction(\"f\")] public static int F(int x) => x; }")]
    [InlineData("TK0020", "[TkFunction(\"f\")] public static int F(object x) => 1;")]
    [InlineData("TK0020", "[TkFunction(\"f\")] public static int F(short x) => 1;")]
    [InlineData("TK0020", "[TkFunction(\"f\")] public static void F(int x) { }")]
    [InlineData("TK0020", "[TkFunction(\"f\")] public static System.Uri F(int x) => null!;")]
    [InlineData("TK0020", "[TkFunction(\"f\")] public static int F(ref int x) => x;")]
    [InlineData("TK0021", "[TkFunction(\"f\", Vectorized = true)] public static void F(ReadOnlySpan<double> a) { }")]
    [InlineData("TK0021", "[TkFunction(\"f\", Vectorized = true)] public static int F(ReadOnlySpan<double> a, Span<double> r) => 1;")]
    [InlineData("TK0021", "[TkFunction(\"f\", Vectorized = true)] public static void F(double a, Span<double> r) { }")]
    [InlineData("TK0021", "[TkFunction(\"f\", Vectorized = true)] public static void F(ReadOnlySpan<decimal> a, Span<decimal> r) { }")]
    [InlineData("TK0021", "[TkFunction(\"f\", Vectorized = true)] public static void F(ReadOnlySpan<double> a, ReadOnlySpan<double> r) { }")]
    [InlineData("TK0022", "[TkProcedure(\"p\")] public static Task P(int x) => Task.CompletedTask;")]
    [InlineData("TK0022", "[TkProcedure(\"p\")] public static int P(TkContext c) => 1;")]
    [InlineData("TK0022", "[TkProcedure(\"p\")] public static Task<object> P(TkContext c) => null!;")]
    [InlineData("TK0020", "[TkProcedure(\"p\")] public static Task P(TkContext c, object x) => Task.CompletedTask;")]
    [InlineData("TK0023", "[TkFunction(\"same\")] public static int F(int x) => x; [TkProcedure(\"SAME\")] public static Task P(TkContext c) => Task.CompletedTask;")]
    public void What_cannot_be_a_routine_is_a_compile_error_that_says_why(string expected, string members)
    {
        var (diagnostics, generated) = Run(
            $$"""
            using System;
            using System.Threading.Tasks;
            using TekiumDB.Sql;

            public static class Routines
            {
                {{members}}
            }
            """,
            mustCompile: false);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal((expected, DiagnosticSeverity.Error), (diagnostic.Id, diagnostic.Severity));
        Assert.Empty(generated);
    }

    private static (ImmutableArray<Diagnostic> Diagnostics, ImmutableArray<string> Generated) Run(string source, bool mustCompile = true)
    {
        var compilation = CSharpCompilation.Create(
            "Generated.Tests",
            [CSharpSyntaxTree.ParseText(source, path: "Routines.cs", cancellationToken: TestContext.Current.CancellationToken)],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var driver = CSharpGeneratorDriver.Create(new RoutineGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, TestContext.Current.CancellationToken);
        var result = driver.GetRunResult();
        if (mustCompile)
        {
            // Ni errores ni advertencias: quien compile con advertencias como errores no puede tropezar aquí.
            Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken)
                .Where(static diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning));
        }

        return (result.Diagnostics, [.. result.GeneratedTrees.Select(static tree => tree.ToString())]);
    }
}
