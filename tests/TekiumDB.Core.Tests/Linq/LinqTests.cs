// Copyright (c) Tekium. Licensed under the MIT License.

using System.Linq.Expressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TekiumDB.Linq;
using TekiumDB.Tests.Database;
using TekiumDB.Tests.Storage;

namespace TekiumDB.Tests.Linq;

/// <summary>
/// La capa LINQ traduce predicados de C# a filtros del motor. Criterio de la Fase 11: los predicados
/// soportados se traducen; los no soportados fallan en compilación o lanzan claro, y nunca se evalúan
/// del lado del cliente en silencio.
/// </summary>
public abstract class LinqTests : DatabaseTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly long StaticLimit = 150;

    private readonly int _instanceLimit = 3;

    private int _calls;

    private int InstanceProperty => 2;

    [Fact]
    public async Task Every_supported_predicate_gives_the_rows_the_same_lambda_gives_over_objects()
    {
        await using var database = await OpenAsync(NewDevice());
        var rows = await FillAsync(database);
        var tickets = database.Table<Ticket>();
        var owner = "owner3";
        long low = 40;
        var high = 120;
        int? priority = 2;
        var due = Epoch.AddHours(300);
        var state = TicketState.Closed;
        var holder = new { Owner = "owner5", Nested = new { Cost = 250m } };

        // Cada caso: el predicado como árbol de expresión (lo que se traduce) y como delegado (lo que debe dar).
        var cases = new (Expression<Func<Ticket, bool>> Predicate, Func<Ticket, bool> Expected)[]
        {
            (t => t.Id == 7, t => t.Id == 7),
            (t => t.Id >= low && t.Id < high, t => t.Id >= low && t.Id < high),
            (t => low <= t.Id && high > t.Id, t => low <= t.Id && high > t.Id),
            (t => t.Owner == owner, t => t.Owner == owner),
            (t => t.Owner == "owner1" && t.Priority > 2, t => t.Owner == "owner1" && t.Priority > 2),
            (t => t.Priority <= priority, t => t.Priority <= priority),
            (t => t.Priority == _instanceLimit, t => t.Priority == _instanceLimit),
            (t => t.Priority == InstanceProperty, t => t.Priority == InstanceProperty),
            (t => t.Id > StaticLimit, t => t.Id > StaticLimit),
            (t => t.Owner == holder.Owner && t.Cost < holder.Nested.Cost, t => t.Owner == holder.Owner && t.Cost < holder.Nested.Cost),
            (t => t.Cost >= 100.5m, t => t.Cost >= 100.5m),
            (t => t.Cost > 100, t => t.Cost > 100),
            (t => t.Due == null, t => t.Due == null),
            (t => t.Due != null, t => t.Due != null),
            (t => t.Due.HasValue, t => t.Due.HasValue),
            (t => !t.Due.HasValue, t => !t.Due.HasValue),
            (t => t.Due > due, t => t.Due > due),
            (t => t.Due <= Epoch.AddHours(100), t => t.Due <= Epoch.AddHours(100)),
            (t => t.Due!.Value >= due, t => t.Due.HasValue && t.Due.Value >= due),
            (t => t.State == TicketState.Open, t => t.State == TicketState.Open),
            (t => t.State == state, t => t.State == state),
            (t => t.Deleted, t => t.Deleted),
            (t => !t.Deleted, t => !t.Deleted),
            (t => t.Deleted == false && t.State == TicketState.Open, t => !t.Deleted && t.State == TicketState.Open),
            (t => t.TitleLower == "ticket 42", t => t.TitleLower == "ticket 42"),
            (t => t.Id > 10 && t.Id > 50 && t.Id < 60, t => t.Id is > 50 and < 60),
            (t => t.Id > 100 && t.Id < 50, static _ => false),
            (t => (t.Id > 20 && t.Owner == owner) && (t.Priority >= 1 && !t.Deleted), t => t.Id > 20 && t.Owner == owner && t.Priority >= 1 && !t.Deleted),
        };

        foreach (var (predicate, expected) in cases)
        {
            var found = await tickets.Where(predicate).ToListAsync(Ct);
            Assert.True(
                rows.Where(expected).Select(static t => t.Id).Order().SequenceEqual(found.Select(static t => t.Id).Order()),
                $"{predicate}: {found.Count} rows found, {rows.Count(expected)} expected.");
        }
    }

    [Fact]
    public async Task A_translated_predicate_is_the_same_filter_as_the_one_written_by_hand_and_uses_the_same_index()
    {
        await using var database = await OpenAsync(NewDevice());
        await FillAsync(database);
        var tickets = database.Table<Ticket>();
        var due = Epoch.AddHours(300);

        Assert.Equal(new TkPlan(TkAccess.KeyLookup, null), await tickets.Where(t => t.Id == 7).ExplainAsync(Ct));
        Assert.Equal(new TkPlan(TkAccess.KeyRange, null), await tickets.Where(t => t.Id >= 7 && t.Id < 90).ExplainAsync(Ct));
        Assert.Equal(new TkPlan(TkAccess.IndexScan, "Due"), await tickets.Where(t => t.Due > due).ExplainAsync(Ct));
        Assert.Equal(new TkPlan(TkAccess.IndexScan, "TitleLower"), await tickets.Where(t => t.TitleLower == "ticket 42").ExplainAsync(Ct));
        Assert.Equal(new TkPlan(TkAccess.IndexScan, "open"), await tickets.Where(t => !t.Deleted && t.State == TicketState.Open).ExplainAsync(Ct));
        Assert.Equal(new TkPlan(TkAccess.TableScan, null), await tickets.Where(t => t.Cost > 5).ExplainAsync(Ct));
        Assert.Equal(new TkPlan(TkAccess.Nothing, null), await tickets.Where(t => t.Id > 100 && t.Id < 50).ExplainAsync(Ct));
        Assert.Equal(new TkPlan(TkAccess.TableScan, null), await tickets.Query().ExplainAsync(Ct));

        // El filtro solo, para usarlo con la API del núcleo.
        var filter = TkLinq.ToFilter<Ticket>(t => t.Due > due);
        Assert.Equal(await tickets.ExplainAsync(Ticket.Fields.Due.GreaterThan(due), Ct), await tickets.ExplainAsync(filter, Ct));
    }

    /// <summary>La mitad del criterio que lanza: cada cosa que el motor no puede responder se rechaza al construir la consulta, diciendo qué.</summary>
    [Fact]
    public async Task What_the_engine_cannot_answer_is_refused_when_the_query_is_built_saying_what()
    {
        await using var database = await OpenAsync(NewDevice());
        await FillAsync(database);
        var tickets = database.Table<Ticket>();
        var owners = new List<string> { "owner1" };
        var text = "owner1";

        Refused(t => t.Owner == "a" || t.Owner == "b", "||");
        Refused(t => t.Owner != "a", "not equal");
        Refused(t => t.Priority != 3 && t.Id > 1, "not equal");
        Refused(t => t.Owner.StartsWith("own", StringComparison.Ordinal), "StartsWith");
        Refused(t => owners.Contains(t.Owner), "Contains");
        Refused(t => t.Priority + 1 > 3, "Priority + 1");
        Refused(t => t.Priority > t.Id, "two properties");
        Refused(t => t.Id == t.Priority + 1, "two properties");
        Refused(t => t.Title.Length > 3, "Length");
        Refused(t => t.Body == "x" && t.IsOpen, "IsOpen");
        Refused(t => !(t.Id > 5), "Not");
        Refused(t => t.Owner.CompareTo(text) > 0, "CompareTo");
        Refused(t => true, "True");
        Refused(t => t.Priority > int.Parse(text, System.Globalization.CultureInfo.InvariantCulture), "could not be computed");
        Refused(t => t.Priority > (text.Length > 100 ? 1 : 2), "without compiling");

        // Nada se ejecutó: construir la consulta ya falla, antes de leer una sola fila.
        void Refused(Expression<Func<Ticket, bool>> predicate, string mention)
        {
            var failure = Assert.Throws<NotSupportedException>(() => tickets.Where(predicate));
            Assert.Contains(mention, failure.Message, StringComparison.Ordinal);
            Assert.Contains("cannot be translated", failure.Message, StringComparison.Ordinal);
            Assert.Same(failure.GetType(), Assert.Throws<NotSupportedException>(() => TkLinq.ToFilter(predicate)).GetType());
        }
    }

    [Fact]
    public async Task Values_computed_outside_the_row_are_computed_once_and_sent_as_values()
    {
        await using var database = await OpenAsync(NewDevice());
        var rows = await FillAsync(database);
        var tickets = database.Table<Ticket>();

        // Una llamada del lado del valor se evalúa una vez, al construir la consulta: no por fila.
        var query = tickets.Where(t => t.Id < Limit() && t.Due >= Epoch.AddHours(50).AddMinutes(30) && t.Cost <= Math.Round(250.4m));
        Assert.Equal(1, _calls);
        var expected = rows.Where(t => t.Id < 90 && t.Due >= Epoch.AddHours(50).AddMinutes(30) && t.Cost <= 250m).Select(static t => t.Id).Order();
        Assert.Equal(expected, (await query.ToListAsync(Ct)).Select(static t => t.Id).Order());
        Assert.Equal(expected, (await query.ToListAsync(Ct)).Select(static t => t.Id).Order());
        Assert.Equal(1, _calls);
    }

    [Fact]
    public async Task Queries_compose_take_skip_and_project_and_read_through_a_transaction()
    {
        await using var database = await OpenAsync(NewDevice(), changeRetention: 5);
        var rows = await FillAsync(database);
        var tickets = database.Table<Ticket>();
        var before = database.LastTransactionId;

        var query = tickets.Query().Where(t => t.Id >= 10).Where(t => t.Id < 100);
        Assert.Equal(Enumerable.Range(10, 90).Select(static i => (long)i), (await query.ToListAsync(Ct)).Select(static t => t.Id));
        Assert.Equal([10L, 11L, 12L], (await query.Take(3).ToListAsync(Ct)).Select(static t => t.Id));
        Assert.Equal([15L, 16L], (await query.Skip(5).Take(2).ToListAsync(Ct)).Select(static t => t.Id));
        Assert.Equal(3, (await query.Take(3).Take(10).ToListAsync(Ct)).Count);
        Assert.Equal(2, (await query.Take(3).Take(2).ToListAsync(Ct)).Count);
        Assert.Empty(await query.Take(0).ToListAsync(Ct));
        Assert.False(await query.Take(0).AnyAsync(Ct));
        Assert.Equal(90, await query.CountAsync(Ct));
        Assert.True(await query.AnyAsync(Ct));
        Assert.False(await tickets.Where(t => t.Id > 100_000).AnyAsync(Ct));
        Assert.Equal(10, (await query.FirstOrDefaultAsync(Ct))!.Id);
        Assert.Null(await tickets.Where(t => t.Id > 100_000).FirstOrDefaultAsync(Ct));
        Assert.Equal(["Ticket 10", "Ticket 11"], await query.Take(2).Select(static t => t.Title).ToListAsync(Ct));
        Assert.Equal("Ticket 10", await query.Select(static t => t.Title).FirstOrDefaultAsync(Ct));

        // await foreach directo, y con la sintaxis de consulta de C#.
        var seen = new List<long>();
        await foreach (var ticket in query.Take(4))
        {
            seen.Add(ticket.Id);
        }

        Assert.Equal([10L, 11L, 12L, 13L], seen);
        var titles = from t in tickets.Query() where t.Id == 42 select t.Title;
        Assert.Equal(["Ticket 42"], await titles.ToListAsync(Ct));
        var same = from t in tickets.Query() where t.Id == 42 && !t.Deleted select t;
        Assert.Equal(42, Assert.Single(await same.ToListAsync(Ct)).Id);

        // Un filtro después de recortar cambiaría el significado: se rechaza.
        Assert.Throws<InvalidOperationException>(() => query.Take(3).Where(t => t.Id > 11));
        Assert.Throws<ArgumentOutOfRangeException>(() => query.Take(-1));

        // Dentro de una transacción de escritura ve lo suyo; en el pasado, lo de entonces.
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await tickets.DeleteAsync(writer, 10L, Ct);
            Assert.Equal(11, (await tickets.Query(writer).Where(t => t.Id >= 10).FirstOrDefaultAsync(Ct))!.Id);
            Assert.Equal(10, (await query.FirstOrDefaultAsync(Ct))!.Id);
            await writer.CommitAsync(Ct);
        }

        Assert.Equal(11, (await query.FirstOrDefaultAsync(Ct))!.Id);
        await using var past = await database.AsOfAsync(before, Ct);
        Assert.Equal(10, (await tickets.Query(past).Where(t => t.Id >= 10).FirstOrDefaultAsync(Ct))!.Id);
        Assert.Equal(rows.Count, await tickets.Query(past).CountAsync(Ct));
    }

    /// <summary>
    /// La mitad del criterio que falla en compilación: una consulta no es un <c>IAsyncEnumerable</c> ni un
    /// <c>IQueryable</c>, así que los operadores que el motor no tiene (ordenar, agrupar, unir) no existen
    /// sobre ella y el compilador los rechaza, en vez de resolverlos en memoria sin avisar.
    /// </summary>
    [Fact]
    public void Operators_the_engine_does_not_have_do_not_compile_on_a_query()
    {
        Assert.Empty(Compile("var q = tickets.Where(t => t.Id > 5).Take(3); await foreach (var t in q) { }"));
        Assert.Empty(Compile("var list = await tickets.Query().AsAsyncEnumerable().OrderBy(t => t.Owner).ToListAsync();"));

        foreach (var statement in new[]
        {
            "var q = tickets.Query().OrderBy(t => t.Owner);",
            "var q = tickets.Where(t => t.Id > 5).OrderByDescending(t => t.Id);",
            "var q = tickets.Query().GroupBy(t => t.Owner);",
            "var q = tickets.Query().Join(tickets.Query(), a => a.Id, b => b.Id, (a, b) => a);",
            "var q = tickets.Query().Distinct();",
            "var q = tickets.Query().Where(t => t.Id > 5).Sum(t => t.Priority);",
            "var q = from t in tickets.Query() orderby t.Id select t;",
            "foreach (var t in tickets.Query()) { }",
            "IQueryable<Ticket> q = tickets.Query();",
            "IAsyncEnumerable<Ticket> q = tickets.Query();",
        })
        {
            var errors = Compile(statement);
            Assert.True(errors.Count > 0, $"This compiled, and it must not: {statement}");
            // Y por lo que debe: el operador no existe sobre la consulta, o la conversión no existe.
            Assert.All(errors, static error => Assert.Matches("CS(1061|1936|8414|0029)", error));
        }

        static List<string> Compile(string statement)
        {
            var source = $$"""
                using System;
                using System.Collections.Generic;
                using System.Linq;
                using System.Threading.Tasks;
                using TekiumDB;
                using TekiumDB.Linq;
                using TekiumDB.Tests.Database;

                public static class Probe
                {
                    public static async Task RunAsync(Table<Ticket> tickets)
                    {
                        await Task.Yield();
                        {{statement}}
                    }
                }
                """;
            var references = AppDomain.CurrentDomain.GetAssemblies()
                .Append(typeof(TkLinq).Assembly)
                .Append(typeof(TekiumDb).Assembly)
                .Append(typeof(System.Linq.AsyncEnumerable).Assembly)
                .Append(typeof(Expression).Assembly)
                .Append(typeof(Queryable).Assembly)
                .Distinct()
                .Where(static assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
                .Select(static assembly => (MetadataReference)MetadataReference.CreateFromFile(assembly.Location))
                .ToList();
            var compilation = CSharpCompilation.Create(
                "Probe", [CSharpSyntaxTree.ParseText(source)], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            return [.. compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).Select(static d => d.ToString())];
        }
    }

    private long Limit()
    {
        _calls++;
        return 90;
    }

    private static async Task<List<Ticket>> FillAsync(TekiumDb database)
    {
        var random = new Random(11);
        var rows = new List<Ticket>();
        await using var writer = await database.BeginWriteAsync(Ct);
        for (var i = 0; i < 200; i++)
        {
            var ticket = new Ticket
            {
                Id = i,
                Owner = $"owner{random.Next(8)}",
                Priority = random.Next(5),
                Due = random.Next(4) == 0 ? null : Epoch.AddHours(random.Next(600)),
                State = random.Next(3) == 0 ? TicketState.Closed : TicketState.Open,
                Deleted = random.Next(5) == 0,
                Cost = random.Next(5000) / 10m,
                Title = $"Ticket {i}",
                Body = "body",
            };
            rows.Add(ticket);
            await database.Table<Ticket>().InsertAsync(writer, ticket, Ct);
        }

        await writer.CommitAsync(Ct);
        return rows;
    }
}

public sealed class LinqTests_File : LinqTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class LinqTests_Memory : LinqTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
