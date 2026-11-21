// Copyright (c) Tekium. Licensed under the MIT License.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TekiumDB.Generators;

namespace TekiumDB.Tests.Generators;

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
            using TekiumDB;

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
        Assert.Contains("new global::TekiumDB.TkColumn(\"qty\", global::TekiumDB.TkColumnType.I32, false)", lineItem, StringComparison.Ordinal);
        Assert.Contains("new global::TekiumDB.TkColumn(\"Status\", global::TekiumDB.TkColumnType.I16, false)", lineItem, StringComparison.Ordinal);
        Assert.Contains("new global::TekiumDB.TkColumn(\"Shipped\", global::TekiumDB.TkColumnType.DateOnly, true)", lineItem, StringComparison.Ordinal);

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
            using TekiumDB;

            [TkTable]
            public partial class Legacy
            {
                [TkKey] public int Id { get; set; }
                public string Name { get; set; }
            }
            """);

        Assert.Empty(diagnostics);
        Assert.Contains("TkColumn(\"Name\", global::TekiumDB.TkColumnType.Text, true)", generated.Single(), StringComparison.Ordinal);
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
    [InlineData("TK0005", "[TkTable] public partial class T { [TkKey] public TkJson Id { get; set; } }", "'Id'")]
    [InlineData("TK0024", "[TkTable] public partial class T { [TkKey] public int Id { get; set; } [TkIndex] public TkJson Doc { get; set; } }", "'Doc' is a JSON document")]
    [InlineData("TK0024", "[TkTable][TkIndex(nameof(Doc), nameof(Id))] public partial class T { [TkKey] public int Id { get; set; } public TkJson Doc { get; set; } }", "'Doc'")]
    [InlineData("TK0026", "[TkTable] public partial class T { [TkKey] public int Id { get; set; } [TkJsonIndex(\"$.a\")] public string Doc { get; set; } = \"\"; }", "'Doc'")]
    [InlineData("TK0026", "[TkTable] public partial class T { [TkKey] public int Id { get; set; } [TkJsonIndex(\"a\")] public TkJson Doc { get; set; } }", "'a'")]
    [InlineData("TK0026", "[TkTable] public partial class T { [TkKey] public int Id { get; set; } [TkJsonIndex(\"$.a\", Type = TkColumnType.I32)] public TkJson Doc { get; set; } }", "TEXT or DOUBLE")]
    [InlineData("TK0025", "[TkTable] public partial class T { [TkKey] public int Id { get; set; } [TkJsonText] public string Doc { get; set; } = \"\"; }", "'Doc' is not a TkJson")]
    public void What_cannot_be_a_table_is_reported_as_an_error_and_generates_nothing(string id, string source, string says)
    {
        var (diagnostics, generated) = Run("#nullable enable\nusing TekiumDB;\n" + source, mustCompile: false);

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
            using TekiumDB;

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
        Assert.Contains("TkField<Order, global::System.DateTimeOffset> CreatedAt = new global::TekiumDB.TkField<Order, global::System.DateTimeOffset>(2, global::TekiumDB.TkColumnType.DateTimeOffset, false)", order, StringComparison.Ordinal);
        Assert.Contains("TkField<Order, int> Month = new global::TekiumDB.TkField<Order, int>(9, global::TekiumDB.TkColumnType.I32, false)", order, StringComparison.Ordinal);
        Assert.Contains("TkField<Order, string> Initial = new global::TekiumDB.TkField<Order, string>(10, global::TekiumDB.TkColumnType.Text, true)", order, StringComparison.Ordinal);
        Assert.DoesNotContain("> Open =", order, StringComparison.Ordinal);

        // El texto de la expresión viaja al catálogo: cambiarlo reconstruye el índice.
        Assert.Contains("new global::TekiumDB.TkExpression(\"Month\", global::TekiumDB.TkColumnType.I32, false, \"(CreatedAt.Year * 12) + CreatedAt.Month\")", order, StringComparison.Ordinal);
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
            using TekiumDB;

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

    [Fact]
    public void Constraints_generate_checks_unique_indexes_and_foreign_keys_that_compile_without_warnings()
    {
        var (diagnostics, generated) = Run("""
            #nullable enable
            using System;
            using TekiumDB;

            [TkTable("customers")]
            public sealed partial class Customer
            {
                [TkKey] public Guid Id { get; set; }
                [TkIndex(Unique = true)] public string Email { get; set; } = "";
            }

            [TkTable("warehouses")]
            public sealed partial class Warehouse
            {
                [TkKey] public string Code { get; set; } = "";
            }

            [TkTable("orders")]
            [TkIndex(nameof(Customer), nameof(Number), Unique = true, Name = "number per customer")]
            public sealed partial class Order
            {
                [TkKey] public long Id { get; set; }
                [TkForeignKey(typeof(Customer))] public Guid Customer { get; set; }
                [TkForeignKey(typeof(Warehouse), Name = "ships from")] public string? Warehouse { get; set; }
                public int Number { get; set; }
                public decimal Total { get; set; }
                public DateOnly Placed { get; set; }
                public DateOnly? Shipped { get; set; }

                [TkCheck] public bool TotalIsNotNegative => Total >= 0;

                [TkCheck(Name = "ships after it is placed")]
                public bool @checked
                {
                    get { return Shipped is null || Shipped >= Placed; }
                }
            }
            """);

        Assert.Empty(diagnostics);
        var order = generated.Single(static source => source.Contains("partial class Order", StringComparison.Ordinal));

        Assert.Contains("new global::TekiumDB.TkCheck(\"TotalIsNotNegative\", \"Total >= 0\")", order, StringComparison.Ordinal);
        Assert.Contains("new global::TekiumDB.TkCheck(\"ships after it is placed\", \"{", order, StringComparison.Ordinal);
        Assert.Contains("return Shipped is null || Shipped >= Placed;", order, StringComparison.Ordinal);
        Assert.Contains("case 1: return row.@checked;", order, StringComparison.Ordinal);

        // La primera clave foránea aprovecha el índice que ya empieza por su columna; la segunda trae el suyo.
        Assert.Contains("TkIndex<Order>(\"number per customer\", new int[] { 1, 3 }, null, true)", order, StringComparison.Ordinal);
        Assert.Contains("TkIndex<Order>(\"ships from\", new int[] { 2 }, null)", order, StringComparison.Ordinal);
        Assert.Contains("new global::TekiumDB.TkForeignKey(\"fk_Customer\", \"customers\", 1, 0)", order, StringComparison.Ordinal);
        Assert.Contains("new global::TekiumDB.TkForeignKey(\"ships from\", \"warehouses\", 2, 1)", order, StringComparison.Ordinal);
        Assert.Contains("return global::TekiumDB.TkKey.Write(row.Customer, destination);", order, StringComparison.Ordinal);
        Assert.Contains("return row.Warehouse is { } __k1 ? global::TekiumDB.TkKey.Write(__k1, destination) : -1;", order, StringComparison.Ordinal);

        Assert.Contains(
            "TkIndex<Customer>(\"Email\", new int[] { 1 }, null, true)",
            generated.Single(static source => source.Contains("partial class Customer", StringComparison.Ordinal)),
            StringComparison.Ordinal);
    }

    /// <summary>El ejemplo de restricciones del README, tal cual.</summary>
    [Fact]
    public void The_constraint_example_of_the_readme_compiles()
    {
        var (diagnostics, generated) = Run("""
            #nullable enable
            using System;
            using TekiumDB;

            [TkTable("customers")]
            public sealed partial class Customer
            {
                [TkKey] public Guid Id { get; set; }

                [TkIndex(Unique = true)] public string Email { get; set; } = "";
            }

            [TkTable("invoices")]
            public sealed partial class Invoice
            {
                [TkKey] public long Id { get; set; }

                [TkForeignKey(typeof(Customer))] public Guid Customer { get; set; }

                public decimal Total { get; set; }

                [TkCheck] public bool TotalIsNotNegative => Total >= 0;
            }
            """);

        Assert.Empty(diagnostics);
        Assert.Equal(2, generated.Length);
        Assert.Contains(generated, static source => source.Contains("TkForeignKey(\"fk_Customer\", \"customers\", 1, 0)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_zone_map_generates_an_index_of_its_own_kind_that_compiles_without_warnings()
    {
        var (diagnostics, generated) = Run("""
            #nullable enable
            using System;
            using TekiumDB;

            [TkTable("readings")]
            public sealed partial class Reading
            {
                [TkKey] public Guid Id { get; set; }
                [TkZoneMap] public DateTimeOffset At { get; set; }
                [TkIndex] [TkZoneMap(BucketMinutes = 1440)] public DateOnly? Day { get; set; }
                [TkZoneMap(BucketMinutes = 5)] public DateTime Seen { get; set; }
                public double Value { get; set; }
            }

            public static class Usage
            {
                public static TkFilter<Reading> LastHour(DateTimeOffset now) => Reading.Fields.At.Between(now.AddHours(-1), now);
            }
            """);

        Assert.Empty(diagnostics);
        var reading = generated.Single();
        Assert.Contains("TkIndex<Reading>(\"Day\", new int[] { 2 }, null)", reading, StringComparison.Ordinal);
        Assert.Contains("TkIndex<Reading>(\"zone_At\", new int[] { 1 }, null, false, 60)", reading, StringComparison.Ordinal);
        Assert.Contains("TkIndex<Reading>(\"zone_Day\", new int[] { 2 }, null, false, 1440)", reading, StringComparison.Ordinal);
        Assert.Contains("TkIndex<Reading>(\"zone_Seen\", new int[] { 3 }, null, false, 5)", reading, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[TkTable] public partial class T { [TkKey] public int Id { get; set; } [TkZoneMap] public long Ticks { get; set; } }", "'Ticks'")]
    [InlineData("[TkTable] public partial class T { [TkKey] public int Id { get; set; } [TkZoneMap] public System.TimeSpan Elapsed { get; set; } }", "'Elapsed'")]
    [InlineData("[TkTable] public partial class T { [TkKey] public int Id { get; set; } [TkZoneMap(BucketMinutes = 0)] public System.DateTime At { get; set; } }", "'At'")]
    [InlineData("[TkTable] public partial class T { [TkKey] public int Id { get; set; } [TkZoneMap] public System.DateTime Later => System.DateTime.MaxValue; }", "'Later'")]
    public void A_zone_map_that_is_not_on_a_date_column_is_reported_as_an_error(string source, string says)
    {
        var (diagnostics, _) = Run("#nullable enable\nusing TekiumDB;\n" + source, mustCompile: false);

        var diagnostic = Assert.Single(diagnostics, static found => found.Id == "TK0018");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(says, diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TK0015", "[TkTable] public partial class T { [TkKey] public int Id { get; set; } [TkCheck] public int Count => Id; }", "'Count'")]
    [InlineData("TK0015", "[TkTable] public partial class T { [TkKey] public int Id { get; set; } [TkCheck] public bool Stored { get; set; } }", "'Stored'")]
    [InlineData("TK0015", "[TkTable] public partial class T { [TkKey] public int Id { get; set; } [TkCheck] public static bool Shared => true; }", "'Shared'")]
    [InlineData("TK0016", "public class P { } [TkTable] public partial class T { [TkKey] public int Id { get; set; } [TkForeignKey(typeof(P))] public int Parent { get; set; } }", "'Parent' cannot be a FOREIGN KEY to 'P'")]
    [InlineData("TK0016", "[TkTable] public partial class P { [TkKey] public long Id { get; set; } } [TkTable] public partial class T { [TkKey] public int Id { get; set; } [TkForeignKey(typeof(P))] public int Parent { get; set; } }", "'Parent'")]
    [InlineData("TK0016", "[TkTable] public partial class P { [TkKey] public int Id { get; set; } } [TkTable] public partial class T { [TkKey] public int Id { get; set; } [TkForeignKey(typeof(P))] public int Parent => 1; }", "'Parent'")]
    [InlineData("TK0017", "[TkTable] public partial class T { [TkKey] public int Id { get; set; } [TkCheck] public bool A => Id > 0; [TkCheck(Name = \"A\")] public bool B => Id < 9; }", "more than one constraint named 'A'")]
    [InlineData("TK0017", "[TkTable] public partial class T { [TkKey] public int Id { get; set; } [TkForeignKey(typeof(T), Name = \"A\")] public int? Up { get; set; } [TkCheck] public bool A => Id > 0; }", "more than one constraint named 'A'")]
    public void A_constraint_that_cannot_be_kept_is_reported_as_an_error(string id, string source, string says)
    {
        var (diagnostics, _) = Run("#nullable enable\nusing TekiumDB;\n" + source, mustCompile: false);

        var diagnostic = Assert.Single(diagnostics, found => found.Id == id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(says, diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.Equal("Tables.cs", diagnostic.Location.GetLineSpan().Path);
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
        var (diagnostics, generated) = Run("#nullable enable\nusing TekiumDB;\n" + source, mustCompile: false);

        var diagnostic = Assert.Single(diagnostics, found => found.Id == id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(says, diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.Equal("Tables.cs", diagnostic.Location.GetLineSpan().Path);
        Assert.Empty(generated);
    }

    [Fact]
    public void Editing_something_else_in_the_file_does_not_run_the_generator_again()
    {
        const string Table = "using TekiumDB;\n[TkTable] public partial class T { [TkKey] public int Id { get; set; } }\n";
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
