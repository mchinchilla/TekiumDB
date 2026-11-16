// Copyright (c) Tekium. Licensed under the MIT License.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tekium.Embedded.Generators;

namespace Tekium.Embedded.Tests.Generators;

/// <summary>
/// El generador corrido dentro de la prueba: qué acepta, qué rechaza y con qué mensaje, y que lo que
/// escribe compila limpio. Su comportamiento en tiempo de ejecución lo cubren las pruebas de la base,
/// que usan tipos generados de verdad.
/// </summary>
public sealed class TableGeneratorTests
{
    private static readonly ImmutableArray<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path)),
    ];

    [Fact]
    public void A_table_with_every_feature_generates_code_that_compiles_without_warnings()
    {
        var (diagnostics, generated) = Run("""
            #nullable enable
            using System;
            using Tekium.Embedded;

            namespace Shop.Data
            {
                public enum Status : short { New, Paid }

                public abstract record Entity
                {
                    [TkKey] public Guid Id { get; init; }
                }

                public partial class Container
                {
                    [TkTable("line \"items\"")]
                    public sealed partial record LineItem : Entity
                    {
                        public required string @class { get; init; }
                        [TkColumn("qty")] public int Quantity { get; set; }
                        public decimal? Discount { get; set; }
                        public Status Status { get; set; }
                        public Status? Previous { get; set; }
                        public byte[]? Image { get; set; }
                        public byte[] Hash { get; set; } = Array.Empty<byte>();
                        public DateOnly? Shipped { get; private set; }
                        public int Computed => Quantity * 2;
                        [TkIgnore] public object? Tag { get; set; }
                        public static int Shared { get; set; }
                        internal int Hidden { get; set; }
                    }
                }
            }

            [TkTable]
            public partial class Global
            {
                [TkKey] public string Name { get; set; } = "";
            }
            """);

        Assert.Empty(diagnostics);
        Assert.Equal(2, generated.Length);
        var lineItem = generated.Single(static source => source.Contains("LineItem", StringComparison.Ordinal));

        // El nombre de la tabla y el de la columna vienen de los atributos; lo demás, de las propiedades.
        Assert.Contains("TableName => \"line \\\"items\\\"\"", lineItem, StringComparison.Ordinal);
        Assert.Contains("new global::Tekium.Embedded.TkColumn(\"qty\", global::Tekium.Embedded.TkColumnType.I32, false)", lineItem, StringComparison.Ordinal);
        Assert.Contains("new global::Tekium.Embedded.TkColumn(\"Status\", global::Tekium.Embedded.TkColumnType.I16, false)", lineItem, StringComparison.Ordinal);
        Assert.Contains("new global::Tekium.Embedded.TkColumn(\"Shipped\", global::Tekium.Embedded.TkColumnType.DateOnly, true)", lineItem, StringComparison.Ordinal);

        // La clave heredada va primero; lo calculado, ignorado, estático o no público no es columna.
        Assert.Contains("KeyColumn => 0", lineItem, StringComparison.Ordinal);
        foreach (var notAColumn in new[] { "Computed", "Tag", "Shared", "Hidden" })
        {
            Assert.DoesNotContain(notAColumn, lineItem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Without_nullable_annotations_a_reference_is_stored_as_nullable()
    {
        var (diagnostics, generated) = Run("""
            #nullable disable
            using Tekium.Embedded;

            [TkTable]
            public partial class Legacy
            {
                [TkKey] public int Id { get; set; }
                public string Name { get; set; }
            }
            """);

        Assert.Empty(diagnostics);
        Assert.Contains("TkColumn(\"Name\", global::Tekium.Embedded.TkColumnType.Text, true)", generated.Single(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TK0001", "[TkTable] public class T { [TkKey] public int Id { get; set; } }", "'T'")]
    [InlineData("TK0001", "public class Outer { [TkTable] public partial class T { [TkKey] public int Id { get; set; } } }", "'T'")]
    [InlineData("TK0002", "[TkTable] public partial class T { public int Id { get; set; } }", "no key")]
    [InlineData("TK0003", "[TkTable] public partial class T { [TkKey] public int A { get; set; } [TkKey] public int B { get; set; } }", "'B'")]
    [InlineData("TK0004", "[TkTable] public partial class T { [TkKey] public int Id { get; set; } public System.Uri Link { get; set; } }", "'Link' is of type 'System.Uri'")]
    [InlineData("TK0004", "[TkTable] public partial class T { [TkKey] public int Id { get; set; } public int[] Numbers { get; set; } }", "'Numbers'")]
    [InlineData("TK0005", "[TkTable] public partial class T { [TkKey] public double Id { get; set; } }", "'Id' is of type 'double'")]
    [InlineData("TK0005", "[TkTable] public partial class T { [TkKey] public int? Id { get; set; } }", "'Id'")]
    [InlineData("TK0005", "[TkTable] public partial class T { [TkKey] public string? Id { get; set; } }", "'Id'")]
    [InlineData("TK0006", "[TkTable] public partial record T([property: TkKey] int Id);", "parameterless constructor")]
    [InlineData("TK0007", "[TkTable] public abstract partial class T { [TkKey] public int Id { get; set; } }", "'T' cannot be a table")]
    [InlineData("TK0007", "[TkTable] public partial class T<X> { [TkKey] public int Id { get; set; } }", "'T' cannot be a table")]
    [InlineData("TK0008", "[TkTable] public partial class T { [TkKey] public int Id { get; set; } [TkColumn(\"Id\")] public int Other { get; set; } }", "column 'Id'")]
    [InlineData("TK0009", "[TkTable] public partial class T { public int Id => 1; }", "no property that can be stored")]
    public void What_cannot_be_a_table_is_reported_as_an_error_and_generates_nothing(string id, string source, string says)
    {
        var (diagnostics, generated) = Run("#nullable enable\nusing Tekium.Embedded;\n" + source, mustCompile: false);

        var diagnostic = Assert.Single(diagnostics, found => found.Id == id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(says, diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

        // Apunta al código que lo causó: el archivo, y una línea que existe en él.
        var where = diagnostic.Location.GetLineSpan();
        Assert.Equal("Tables.cs", where.Path);
        Assert.True(where.StartLinePosition.Line >= 2 && where.EndLinePosition.Character > where.StartLinePosition.Character);
        Assert.Empty(generated);
    }

    [Fact]
    public void Indexes_generate_fields_expressions_and_conditions_that_compile_without_warnings()
    {
        var (diagnostics, generated) = Run("""
            #nullable enable
            using System;
            using Tekium.Embedded;

            public enum Status : short { New, Paid = 7 }

            [TkTable("orders")]
            [TkIndex(nameof(Customer), nameof(CreatedAt))]
            [TkIndex(nameof(Month), nameof(@class), Name = "by month", Where = nameof(Open))]
            public sealed partial class Order
            {
                [TkKey] public Guid Id { get; set; }
                [TkIndex] public string Customer { get; set; } = "";
                [TkIndex(Name = "recent", Where = nameof(Recent))] public DateTimeOffset CreatedAt { get; set; }
                public decimal Total { get; set; }
                public int? Discount { get; set; }
                public bool Deleted { get; set; }
                public Status Status { get; set; }
                public string? Note { get; set; }
                public string @class { get; set; } = "";

                [TkIndex] public int Month => (CreatedAt.Year * 12) + CreatedAt.Month;

                [TkIndex]
                public string? Initial
                {
                    get
                    {
                        return Customer.Length == 0 ? null : Customer.Substring(0, 1);
                    }
                }

                public bool Open => !Deleted && Status == Status.Paid && (100 <= Total) && this.Discount is not null;

                public bool Recent => Deleted == false && Note is null && Discount != null && Month > 24000 && Total < 9.5m;
            }
            """);

        Assert.Empty(diagnostics);
        var order = generated.Single();

        // Un campo por columna y por propiedad calculada que algún índice usa; las calculadas van después.
        Assert.Contains("TkField<Order, global::System.DateTimeOffset> CreatedAt = new global::Tekium.Embedded.TkField<Order, global::System.DateTimeOffset>(2, global::Tekium.Embedded.TkColumnType.DateTimeOffset, false)", order, StringComparison.Ordinal);
        Assert.Contains("TkField<Order, int> Month = new global::Tekium.Embedded.TkField<Order, int>(9, global::Tekium.Embedded.TkColumnType.I32, false)", order, StringComparison.Ordinal);
        Assert.Contains("TkField<Order, string> Initial = new global::Tekium.Embedded.TkField<Order, string>(10, global::Tekium.Embedded.TkColumnType.Text, true)", order, StringComparison.Ordinal);
        Assert.DoesNotContain("> Open =", order, StringComparison.Ordinal);

        // El texto de la expresión viaja al catálogo: cambiarlo reconstruye el índice.
        Assert.Contains("new global::Tekium.Embedded.TkExpression(\"Month\", global::Tekium.Embedded.TkColumnType.I32, false, \"(CreatedAt.Year * 12) + CreatedAt.Month\")", order, StringComparison.Ordinal);
        Assert.Contains("return Customer.Length == 0 ? null : Customer.Substring(0, 1);", order, StringComparison.Ordinal);

        // Los de la clase primero. El nombre por defecto son las propiedades unidas.
        Assert.Contains("TkIndex<Order>(\"Customer_CreatedAt\", new int[] { 1, 2 }, null)", order, StringComparison.Ordinal);
        Assert.Contains(
            "TkIndex<Order>(\"by month\", new int[] { 9, 8 }, Fields.Deleted.EqualTo((bool)(false))"
            + ".And(Fields.Status.EqualTo((global::Status)(short)(7L)))"
            + ".And(Fields.Total.AtLeast((decimal)(100L)))"
            + ".And(Fields.Discount.IsNotNull()))",
            order,
            StringComparison.Ordinal);
        Assert.Contains("TkIndex<Order>(\"Customer\", new int[] { 1 }, null)", order, StringComparison.Ordinal);
        Assert.Contains(
            "TkIndex<Order>(\"recent\", new int[] { 2 }, Fields.Deleted.EqualTo((bool)(false))"
            + ".And(Fields.Note.IsNull())"
            + ".And(Fields.Discount.IsNotNull())"
            + ".And(Fields.Month.GreaterThan((int)(24000L)))"
            + ".And(Fields.Total.LessThan((decimal)(9.5M))))",
            order,
            StringComparison.Ordinal);
        Assert.Contains("TkIndex<Order>(\"Month\", new int[] { 9 }, null)", order, StringComparison.Ordinal);
        Assert.Contains("TkIndex<Order>(\"Initial\", new int[] { 10 }, null)", order, StringComparison.Ordinal);
    }

    /// <summary>El ejemplo de índices del README, tal cual: si deja de compilar, el README miente.</summary>
    [Fact]
    public void The_index_example_of_the_readme_compiles()
    {
        var (diagnostics, generated) = Run("""
            #nullable enable
            using System;
            using Tekium.Embedded;

            [TkTable("orders")]
            [TkIndex(nameof(Customer), nameof(CreatedAt))]
            [TkIndex(nameof(CreatedAt), Name = "open", Where = nameof(IsOpen))]
            public sealed partial class Order
            {
                [TkKey] public Guid Id { get; set; }

                [TkIndex] public string Customer { get; set; } = "";

                public DateTimeOffset CreatedAt { get; set; }
                public decimal Total { get; set; }
                public bool Deleted { get; set; }

                [TkIndex] public int Month => (CreatedAt.Year * 12) + CreatedAt.Month;

                public bool IsOpen => !Deleted && Total > 0;
            }

            public static class Usage
            {
                public static TkFilter<Order> Recent(DateTimeOffset from, DateTimeOffset to) =>
                    Order.Fields.Customer.EqualTo("ana").And(Order.Fields.CreatedAt.Between(from, to));
            }
            """);

        Assert.Empty(diagnostics);
        Assert.Contains("TkIndex<Order>(\"open\", new int[] { 2 }, Fields.Deleted.EqualTo((bool)(false)).And(Fields.Total.GreaterThan((decimal)(0L))))", generated.Single(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TK0010", "[TkIndex(\"Missing\")] [TkTable] public partial class T { [TkKey] public int Id { get; set; } }", "'Missing'")]
    [InlineData("TK0010", "[TkIndex(\"Id\", \"Id\")] [TkTable] public partial class T { [TkKey] public int Id { get; set; } }", "'Id'")]
    [InlineData("TK0010", "[TkTable] public partial class T { [TkKey] public int Id { get; set; } [TkIndex] public System.Uri? Link => null; }", "'Link'")]
    [InlineData("TK0010", "[TkTable] public partial class T { [TkKey] public int Id { get; set; } [TkIndex] public int Fixed { get; } }", "'Fixed'")]
    [InlineData("TK0010", "[TkTable] public partial class T { [TkKey] public int Id { get; set; } [TkIndex] [TkIgnore] public int Loose { get; set; } }", "'Loose'")]
    [InlineData("TK0011", "[TkIndex(\"Id\")] [TkTable] public partial class T { [TkKey] [TkIndex] public int Id { get; set; } }", "more than one index named 'Id'")]
    [InlineData("TK0012", "[TkTable] public partial class T { [TkKey] [TkIndex(Where = \"Nope\")] public int Id { get; set; } }", "'Nope'")]
    [InlineData("TK0012", "[TkTable] public partial class T { [TkKey] [TkIndex(Where = nameof(Odd))] public int Id { get; set; } public bool Odd => Id % 2 == 1; }", "'Odd'")]
    [InlineData("TK0012", "[TkTable] public partial class T { [TkKey] [TkIndex(Where = nameof(Either))] public int Id { get; set; } public bool Either => Id == 1 || Id == 2; }", "'Either'")]
    [InlineData("TK0012", "[TkTable] public partial class T { [TkKey] [TkIndex(Where = nameof(Not))] public int Id { get; set; } public bool Not => Id != 1; }", "'Not'")]
    [InlineData("TK0012", "[TkTable] public partial class T { [TkKey] [TkIndex(Where = nameof(Now))] public int Id { get; set; } public bool Now => Id > System.Environment.TickCount; }", "'Now'")]
    [InlineData("TK0012", "[TkTable] public partial class T { [TkKey] [TkIndex(Where = nameof(Count))] public int Id { get; set; } public int Count => Id; }", "'Count'")]
    [InlineData("TK0012", "[TkTable] public partial class T { [TkKey] [TkIndex(Where = nameof(Plain))] public int Id { get; set; } public bool Plain => Id; }", "'Plain'")]
    [InlineData("TK0013", "[TkIndex] [TkTable] public partial class T { [TkKey] public int Id { get; set; } }", "no properties")]
    [InlineData("TK0014", "[TkTable] public partial class T { [TkKey] public int Id { get; set; } public int Fields { get; set; } }", "'Fields'")]
    public void An_index_that_cannot_be_kept_is_reported_as_an_error_and_generates_nothing(string id, string source, string says)
    {
        var (diagnostics, generated) = Run("#nullable enable\nusing Tekium.Embedded;\n" + source, mustCompile: false);

        var diagnostic = Assert.Single(diagnostics, found => found.Id == id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(says, diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.Equal("Tables.cs", diagnostic.Location.GetLineSpan().Path);
        Assert.Empty(generated);
    }

    [Fact]
    public void Editing_something_else_in_the_file_does_not_run_the_generator_again()
    {
        const string Table = "using Tekium.Embedded;\n[TkTable] public partial class T { [TkKey] public int Id { get; set; } }\n";
        var compilation = Compile(Table + "public class Other { }");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new TableGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);

        var edited = compilation.ReplaceSyntaxTree(
            compilation.SyntaxTrees.Single(),
            CSharpSyntaxTree.ParseText(Table + "public class Other { public int Added; }", path: "Tables.cs", cancellationToken: TestContext.Current.CancellationToken));
        var result = driver.RunGenerators(edited, TestContext.Current.CancellationToken).GetRunResult().Results.Single();

        var outputs = result.TrackedOutputSteps.SelectMany(static step => step.Value).SelectMany(static step => step.Outputs);
        Assert.All(outputs, static output => Assert.Equal(IncrementalStepRunReason.Cached, output.Reason));
    }

    private static CSharpCompilation Compile(string source) =>
        CSharpCompilation.Create(
            "Generated.Tests",
            [CSharpSyntaxTree.ParseText(source, path: "Tables.cs", cancellationToken: TestContext.Current.CancellationToken)],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    private static (ImmutableArray<Diagnostic> Diagnostics, ImmutableArray<string> Generated) Run(string source, bool mustCompile = true)
    {
        var driver = CSharpGeneratorDriver.Create(new TableGenerator())
            .RunGeneratorsAndUpdateCompilation(Compile(source), out var output, out _, TestContext.Current.CancellationToken);
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
