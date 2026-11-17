// Copyright (c) Tekium. Licensed under the MIT License.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Tekium.Embedded.Catalog;
using Tekium.Embedded.Sql.Parsing;
using Tekium.Embedded.Sql.Storage;

namespace Tekium.Embedded.Sql.Execution;

/// <summary>
/// Runs a SELECT. The first table is reached by its key or by the index expected to give fewest rows
/// (estimated from the shape of each tree); each joined table is looked up by key or index when the join
/// allows it, and otherwise read once and matched in memory. Every condition is checked on every row,
/// whatever path reached it: the plan only narrows what is read.
/// </summary>
internal sealed class SelectRunner(TkTransaction transaction, StatementContext parameters, CancellationToken cancellationToken)
{
    private const int MaxViewDepth = 32;
    private const int BlockSize = 1024;

    private readonly List<Source> _sources = [];

    /// <summary>Where the result of each subquery goes: a position past the columns of the tables, in every row.</summary>
    private readonly Dictionary<SqlExpr, int> _slots = new(ReferenceEqualityComparer.Instance);
    private int _width;

    /// <summary>Runs a query. <paramref name="plan"/> receives the steps taken, with their row counts, if it is given.</summary>
    public async ValueTask<SqlResult> RunAsync(SelectStatement query, PlanNode? plan, IReadOnlyList<string> views)
    {
        if (query.From is null)
        {
            // A SELECT of values: one row.
            var values = query.Items.Select(static item => item.Expression ?? throw new TkSqlException("SELECT * needs a FROM.")).ToArray();
            object?[] only = [];
            await foreach (var row in Apply(Replay([[]]), values, views).ConfigureAwait(false))
            {
                only = row;
            }

            var constants = values.Select(expression => Binder.Bind(
                expression,
                static column => throw new TkSqlException($"Column '{column.Name}' cannot be used here: the query has no FROM."),
                parameters,
                Special)(only)).ToArray();
            return Result([.. query.Items.Select((item, i) => item.Alias ?? $"column{i + 1}")], [constants], new Type?[constants.Length]);
        }

        await AddSourceAsync(query.From, views).ConfigureAwait(false);
        foreach (var join in query.Joins)
        {
            await AddSourceAsync(join.Table, views).ConfigureAwait(false);
        }

        var width = _sources.Sum(static source => source.Columns.Length);
        _width = width;
        var rows = await BaseAsync(query.Where, width).ConfigureAwait(false);
        var top = _sources[0].Node;
        for (var i = 0; i < query.Joins.Count; i++)
        {
            (rows, top) = await JoinAsync(rows, top, i + 1, query.Joins[i].On, query.Joins[i].Left).ConfigureAwait(false);
        }

        if (query.Where is not null)
        {
            // The subqueries of the WHERE are computed for each row before it is filtered.
            rows = Apply(rows, [query.Where], views);
            var condition = Binder.Bind(query.Where, Resolve, parameters, Special);
            var node = new PlanNode($"Filter: {Show(query.Where)}", null);
            node.Children.Add(top);
            (rows, top) = (Filter(rows, condition, node), node);
        }

        // What the select list is made of, and whether the rows are grouped first.
        var items = Expand(query.Items);
        var grouped = query.GroupBy.Count > 0 || items.Any(item => Binder.HasAggregate(item.Expression)) || Binder.HasAggregate(query.Having);
        Func<SqlExpr, Evaluator> bind = expression => Binder.Bind(expression, Resolve, parameters, Special);
        if (!grouped)
        {
            // Those of the select list and the ORDER BY, only for the rows that passed the filter.
            rows = Apply(rows, items.Select(static item => item.Expression).Concat(query.OrderBy.Select(static item => item.Expression)), views);
        }

        if (grouped)
        {
            var (aggregated, groupBind) = await GroupAsync(rows, query, items).ConfigureAwait(false);
            var node = new PlanNode(
                query.GroupBy.Count > 0 ? $"Group by {string.Join(", ", query.GroupBy.Select(Show))}" : "Aggregate", null) { Actual = aggregated.Count };
            node.Children.Add(top);
            (rows, top, bind) = (Replay(aggregated), node, groupBind);
        }

        var names = items.Select((item, i) => item.Alias ?? (item.Expression as ColumnRef)?.Name ?? $"column{i + 1}").ToArray();

        // The select list is computed a block of rows at a time, so that a vectorized function is called once per block.
        parameters.Batching = true;
        var select = items.Select(item => bind(item.Expression!)).ToArray();
        parameters.Batching = false;
        var vectors = parameters.VectorCalls.ToArray();
        parameters.VectorCalls.Clear();
        var order = query.OrderBy.Select(item => OrderKey(item.Expression, names, bind)).ToArray();
        var limit = query.Limit is null ? long.MaxValue : Whole(query.Limit, "LIMIT");
        var offset = query.Offset is null ? 0 : Whole(query.Offset, "OFFSET");
        var streaming = order.Length == 0 && !query.Distinct;

        var output = new List<(object?[] Values, object?[] Keys)>();
        var seen = query.Distinct ? new HashSet<SqlKey>() : null;
        if (limit > 0)
        {
            var block = new List<object?[]>(BlockSize);
            var done = false;
            await foreach (var row in rows.ConfigureAwait(false))
            {
                block.Add(row);

                // Without vectorized calls there is nothing to gain from waiting for more rows.
                if (block.Count == (vectors.Length == 0 ? 1 : BlockSize) && Project())
                {
                    done = true;
                    break;
                }
            }

            if (!done)
            {
                Project();
            }

            // Computes the block. True when nothing sorts or dedupes afterwards and the limit is reached: no need to read further.
            bool Project()
            {
                foreach (var vector in vectors)
                {
                    vector.Prepare(block);
                }

                foreach (var row in block)
                {
                    var values = Array.ConvertAll(select, evaluator => evaluator(row));
                    if (seen is not null && !seen.Add(new SqlKey(values)))
                    {
                        continue;
                    }

                    output.Add((values, Array.ConvertAll(order, key => key(row, values))));
                    if (streaming && output.Count - offset >= limit)
                    {
                        block.Clear();
                        return true;
                    }
                }

                block.Clear();
                foreach (var vector in vectors)
                {
                    vector.Clear();
                }

                return false;
            }
        }

        if (order.Length > 0)
        {
            var node = new PlanNode($"Sort by {string.Join(", ", query.OrderBy.Select(item => Show(item.Expression) + (item.Descending ? " DESC" : string.Empty)))}", null)
            {
                Actual = output.Count,
            };
            node.Children.Add(top);
            top = node;
            var directions = query.OrderBy.Select(static item => item.Descending).ToArray();
            output = [.. output.Order(Comparer<(object?[] Values, object?[] Keys)>.Create((a, b) => CompareKeys(a.Keys, b.Keys, directions)))];
        }

        var final = output.Skip((int)Math.Min(offset, int.MaxValue)).Take((int)Math.Min(limit, int.MaxValue)).Select(static pair => pair.Values).ToList();
        if (query.Limit is not null || query.Offset is not null)
        {
            var node = new PlanNode(
                $"Limit {(query.Limit is null ? "all" : limit.ToString(CultureInfo.InvariantCulture))}{(offset > 0 ? $" offset {offset.ToString(CultureInfo.InvariantCulture)}" : string.Empty)}", null)
            {
                Actual = final.Count,
            };
            node.Children.Add(top);
            top = node;
        }

        plan?.Children.Add(top);
        var guesses = items.Select(item => item.Expression is ColumnRef column && !grouped ? Type(column) : null).ToArray();
        return Result(names, final, guesses);
    }

    /// <summary>The rows of one table that meet a condition, for UPDATE and DELETE: reached like the first table of a query.</summary>
    public async ValueTask<List<object?[]>> MatchingAsync(SqlTable table, SqlExpr? where, string? alias = null)
    {
        _sources.Add(new Source(alias ?? table.Name, table) { Offset = 0 });
        var rows = await BaseAsync(where, table.Columns.Length).ConfigureAwait(false);
        if (where is not null)
        {
            rows = Filter(rows, Binder.Bind(where, Resolve, parameters, null), new PlanNode("Filter", null));
        }

        var matching = new List<object?[]>();
        await foreach (var row in rows.ConfigureAwait(false))
        {
            matching.Add(row);
        }

        return matching;
    }

    /// <summary>The position of a column of the sources in the combined row.</summary>
    public int Resolve(ColumnRef column)
    {
        var found = -1;
        foreach (var source in _sources)
        {
            if (column.Table is not null
                && !string.Equals(source.Alias, column.Table, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(source.Name, column.Table, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var at = Array.FindIndex(source.Columns, name => string.Equals(name, column.Name, StringComparison.OrdinalIgnoreCase));
            if (at < 0)
            {
                continue;
            }

            if (found >= 0)
            {
                throw new TkSqlException($"Column '{column.Name}' is in more than one table of the query: say which, as in table.{column.Name}.");
            }

            found = source.Offset + at;
        }

        return found >= 0
            ? found
            : throw new TkSqlException(
                column.Table is null ? $"There is no column '{column.Name}' in the tables of the query." : $"There is no column '{column.Table}.{column.Name}' in the query.");
    }

    /// <summary>
    /// Computes, for each row, the subqueries of some expressions, and leaves each result in a position of
    /// its own past the columns of the row. A subquery that reads nothing of the row is computed once.
    /// </summary>
    private IAsyncEnumerable<object?[]> Apply(IAsyncEnumerable<object?[]> rows, IEnumerable<SqlExpr?> expressions, IReadOnlyList<string> views)
    {
        var found = new List<SqlExpr>();
        foreach (var expression in expressions)
        {
            Subqueries(expression, found);
        }

        if (found.Count == 0)
        {
            return rows;
        }

        var prepared = found.Select(Prepare).ToArray();
        var width = _width;
        return Extended();

        async IAsyncEnumerable<object?[]> Extended()
        {
            var once = new object?[prepared.Length];
            var known = new bool[prepared.Length];
            await foreach (var row in rows.ConfigureAwait(false))
            {
                var extended = new object?[Math.Max(width, row.Length)];
                row.CopyTo(extended, 0);
                for (var i = 0; i < prepared.Length; i++)
                {
                    var (node, query, outer, slot) = prepared[i];
                    if (outer.Count > 0)
                    {
                        extended[slot] = await ComputeAsync(node, query, outer, row, views).ConfigureAwait(false);
                        continue;
                    }

                    if (!known[i])
                    {
                        once[i] = await ComputeAsync(node, query, outer, row, views).ConfigureAwait(false);
                        known[i] = true;
                    }

                    extended[slot] = once[i];
                }

                yield return extended;
            }
        }
    }

    /// <summary>Gives a subquery its position in the row, and turns what it reads of the outer query into parameters.</summary>
    private (SqlExpr Node, SelectStatement Query, List<int> Outer, int Slot) Prepare(SqlExpr node)
    {
        var query = node is Subquery subquery ? subquery.Query : ((InQuery)node).Query;
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Names(query, own);
        var outer = new List<int>();
        var rewritten = (SelectStatement)Procedures.SqlRewriter.Statement(query, column =>
        {
            // A column belongs to the outer query when it names a table the subquery does not have and the outer one does.
            if (column.Table is null || own.Contains(column.Table) || Find(column) is not { } position)
            {
                return null;
            }

            if (!outer.Contains(position))
            {
                outer.Add(position);
            }

            return new Parameter(OuterName(position));
        });

        // EXISTS only asks whether there is a row: one is enough.
        if (node is Subquery { Exists: true } && rewritten.Limit is null && rewritten.GroupBy.Count == 0
            && !rewritten.Items.Any(static item => Binder.HasAggregate(item.Expression)))
        {
            rewritten = rewritten with { Limit = new Literal(1L) };
        }

        var slot = _width++;
        _slots[node] = slot;
        return (node, rewritten, outer, slot);
    }

    private async ValueTask<object?> ComputeAsync(SqlExpr node, SelectStatement query, List<int> outer, object?[] row, IReadOnlyList<string> views)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in parameters.Values)
        {
            values[name] = value;
        }

        foreach (var position in outer)
        {
            values[OuterName(position)] = row[position];
        }

        var result = await new SelectRunner(transaction, new StatementContext(values, parameters.Routines), cancellationToken)
            .RunAsync(query, null, views).ConfigureAwait(false);
        if (node is Subquery { Exists: true })
        {
            return result.Rows.Count > 0;
        }

        if (result.Names.Length != 1)
        {
            throw new TkSqlException($"A subquery used as a value must return one column; this one returns {result.Names.Length}.");
        }

        if (node is InQuery)
        {
            return result.Rows.Select(static found => found[0]).ToArray();
        }

        return result.Rows.Count switch
        {
            0 => null,
            1 => result.Rows[0][0],
            _ => throw new TkSqlException($"A subquery used as a value must return at most one row; this one returned {result.Rows.Count}."),
        };
    }

    /// <summary>How an expression that is a subquery is evaluated: by reading the position its result was left in.</summary>
    private Evaluator? Special(SqlExpr expression)
    {
        if (!_slots.TryGetValue(expression, out var slot))
        {
            return null;
        }

        if (expression is not InQuery inQuery)
        {
            return row => row[slot];
        }

        var operand = Binder.Bind(inQuery.Operand, Resolve, parameters, Special);
        var negated = inQuery.Negated;
        return row =>
        {
            if (operand(row) is not { } needle)
            {
                return null;
            }

            var unknown = false;
            foreach (var candidate in (object?[])row[slot]!)
            {
                if (candidate is null)
                {
                    unknown = true;
                }
                else if (Binder.Compare(needle, candidate) == 0)
                {
                    return !negated;
                }
            }

            // Not found among values of which one is unknown: it cannot be said that it is not there.
            return unknown ? null : negated;
        };
    }

    private static string OuterName(int position) => string.Create(CultureInfo.InvariantCulture, $"\u0001outer{position}");

    private int? Find(ColumnRef column)
    {
        try
        {
            return Resolve(column);
        }
        catch (TkSqlException)
        {
            return null;
        }
    }

    /// <summary>The subqueries of an expression, not counting those inside other subqueries.</summary>
    private static void Subqueries(SqlExpr? expression, List<SqlExpr> found)
    {
        switch (expression)
        {
            case Subquery:
                found.Add(expression);
                break;
            case InQuery inQuery:
                found.Add(expression);
                Subqueries(inQuery.Operand, found);
                break;
            case Binary binary:
                Subqueries(binary.Left, found);
                Subqueries(binary.Right, found);
                break;
            case Unary unary:
                Subqueries(unary.Operand, found);
                break;
            case IsNull isNull:
                Subqueries(isNull.Operand, found);
                break;
            case InList list:
                Subqueries(list.Operand, found);
                list.Items.ToList().ForEach(item => Subqueries(item, found));
                break;
            case Between between:
                Subqueries(between.Operand, found);
                Subqueries(between.Low, found);
                Subqueries(between.High, found);
                break;
            case Like like:
                Subqueries(like.Operand, found);
                Subqueries(like.Pattern, found);
                break;
            case Call call:
                call.Arguments.ToList().ForEach(argument => Subqueries(argument, found));
                break;
            case Case choice:
                foreach (var (when, then) in choice.Branches)
                {
                    Subqueries(when, found);
                    Subqueries(then, found);
                }

                Subqueries(choice.Otherwise, found);
                break;
            default:
                break;
        }
    }

    /// <summary>The names a query, and the queries inside it, give to their own tables.</summary>
    private static void Names(SelectStatement query, HashSet<string> names)
    {
        foreach (var table in (query.From is null ? [] : new[] { query.From }).Concat(query.Joins.Select(static join => join.Table)))
        {
            names.Add(table.Alias);
            names.Add(table.Name);
            if (table.Query is not null)
            {
                Names(table.Query, names);
            }
        }

        var inside = new List<SqlExpr>();
        foreach (var expression in query.Items.Select(static item => item.Expression)
            .Append(query.Where).Append(query.Having)
            .Concat(query.Joins.Select(static join => join.On))
            .Concat(query.GroupBy)
            .Concat(query.OrderBy.Select(static item => item.Expression)))
        {
            Subqueries(expression, inside);
        }

        foreach (var nested in inside)
        {
            Names(nested is Subquery subquery ? subquery.Query : ((InQuery)nested).Query, names);
        }
    }

    private static string Show(SqlExpr expression) => expression switch
    {
        Subquery { Exists: true } => "EXISTS (subquery)",
        Subquery => "(subquery)",
        InQuery inQuery => $"{Show(inQuery.Operand)} {(inQuery.Negated ? "NOT " : string.Empty)}IN (subquery)",
        Case => "CASE … END",
        Literal literal => SqlValues.Show(literal.Value),
        Parameter parameter => $"@{parameter.Name}",
        ColumnRef column => column.Table is null ? column.Name : $"{column.Table}.{column.Name}",
        Binary binary => $"{Show(binary.Left)} {binary.Op} {Show(binary.Right)}",
        Unary unary => unary.Op == "NOT" ? $"NOT ({Show(unary.Operand)})" : $"-{Show(unary.Operand)}",
        IsNull isNull => $"{Show(isNull.Operand)} IS {(isNull.Negated ? "NOT " : string.Empty)}NULL",
        InList list => $"{Show(list.Operand)} {(list.Negated ? "NOT " : string.Empty)}IN ({string.Join(", ", list.Items.Select(Show))})",
        Between between => $"{Show(between.Operand)} {(between.Negated ? "NOT " : string.Empty)}BETWEEN {Show(between.Low)} AND {Show(between.High)}",
        Like like => $"{Show(like.Operand)} {(like.Negated ? "NOT " : string.Empty)}LIKE {Show(like.Pattern)}",
        Call call => $"{call.Name}({(call.Star ? "*" : string.Join(", ", call.Arguments.Select(Show)))})",
        _ => expression.ToString(),
    };

    private static SqlResult Result(string[] names, List<object?[]> rows, Type?[] guesses)
    {
        // The type of a column is that of its first value; with none, that of the column it reads, if it is one.
        var types = new Type[names.Length];
        for (var i = 0; i < types.Length; i++)
        {
            types[i] = rows.Select(row => row[i]).FirstOrDefault(static value => value is not null)?.GetType() ?? guesses[i] ?? typeof(object);
        }

        return new SqlResult { Names = names, Types = types, Rows = rows };
    }

    private static int CompareKeys(object?[] left, object?[] right, bool[] descending)
    {
        for (var i = 0; i < left.Length; i++)
        {
            // NULL sorts first, as in an index.
            var comparison = (left[i], right[i]) switch
            {
                (null, null) => 0,
                (null, _) => -1,
                (_, null) => 1,
                var (a, b) => Binder.Compare(a, b),
            };
            if (comparison != 0)
            {
                return descending[i] ? -comparison : comparison;
            }
        }

        return 0;
    }

    private static async IAsyncEnumerable<object?[]> Replay(List<object?[]> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static IEnumerable<SqlExpr> Conjuncts(SqlExpr? expression) => expression switch
    {
        null => [],
        Binary { Op: "AND" } and => Conjuncts(and.Left).Concat(Conjuncts(and.Right)),
        _ => [expression],
    };

    private static async IAsyncEnumerable<object?[]> Filter(IAsyncEnumerable<object?[]> rows, Evaluator condition, PlanNode node)
    {
        await foreach (var row in rows.ConfigureAwait(false))
        {
            if (Binder.Truth(condition(row)) == true)
            {
                node.Actual++;
                yield return row;
            }
        }
    }

    private Type? Type(ColumnRef column)
    {
        var slot = Resolve(column);
        var source = _sources.Last(candidate => candidate.Offset <= slot);
        return source.Kinds[slot - source.Offset] is { } kind ? SqlValues.ClrType(kind) : null;
    }

    private long Whole(SqlExpr expression, string what) =>
        Binder.Constant(expression, parameters) is { } value && SqlValues.IsNumber(value) && Convert.ToDecimal(value, CultureInfo.InvariantCulture) is >= 0 and var number
            ? (long)number
            : throw new TkSqlException($"{what} needs a number that is not negative.");

    /// <summary>ORDER BY a name or a position of the select list sorts by that column; anything else is computed from the row.</summary>
    private static Func<object?[], object?[], object?> OrderKey(SqlExpr expression, string[] names, Func<SqlExpr, Evaluator> bind)
    {
        if (expression is ColumnRef { Table: null } named
            && Array.FindIndex(names, name => string.Equals(name, named.Name, StringComparison.OrdinalIgnoreCase)) is >= 0 and var byName)
        {
            return (_, values) => values[byName];
        }

        if (expression is Literal { Value: long position })
        {
            return position >= 1 && position <= names.Length
                ? (_, values) => values[position - 1]
                : throw new TkSqlException($"ORDER BY {position}: the query returns {names.Length} columns.");
        }

        var evaluator = bind(expression);
        return (row, _) => evaluator(row);
    }

    private List<SelectItem> Expand(IReadOnlyList<SelectItem> items)
    {
        var expanded = new List<SelectItem>();
        foreach (var item in items)
        {
            if (!item.Star)
            {
                expanded.Add(item);
                continue;
            }

            var any = false;
            foreach (var source in _sources)
            {
                if (item.StarOf is null
                    || string.Equals(source.Alias, item.StarOf, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(source.Name, item.StarOf, StringComparison.OrdinalIgnoreCase))
                {
                    any = true;
                    expanded.AddRange(source.Columns.Select(column => new SelectItem(new ColumnRef(source.Alias, column), column, false, null)));
                }
            }

            if (!any)
            {
                throw new TkSqlException($"{item.StarOf}.*: there is no table '{item.StarOf}' in the query.");
            }
        }

        return expanded;
    }

    private async ValueTask AddSourceAsync(TableRef reference, IReadOnlyList<string> views)
    {
        if (_sources.Exists(source => string.Equals(source.Alias, reference.Alias, StringComparison.OrdinalIgnoreCase)))
        {
            throw new TkSqlException($"'{reference.Alias}' names two tables of the query: give one of them another alias.");
        }

        var offset = _sources.Sum(static source => source.Columns.Length);
        if (reference.Query is { } derived)
        {
            // A query in the place of a table: run whole, and read like a table.
            var inside = new PlanNode($"Query {reference.Alias}", null);
            var result = await new SelectRunner(transaction, parameters, cancellationToken).RunAsync(derived, inside, views).ConfigureAwait(false);
            inside.Actual = result.Rows.Count;
            _sources.Add(new Source(reference.Alias, reference.Alias, result.Names, result.Rows, inside) { Offset = offset });
            return;
        }

        if (await SqlTable.OpenAsync(transaction, reference.Name, cancellationToken).ConfigureAwait(false) is { } table)
        {
            _sources.Add(new Source(reference.Alias, table) { Offset = offset });
            return;
        }

        if (await ViewAsync(transaction, reference.Name, cancellationToken).ConfigureAwait(false) is not var (name, text))
        {
            throw new TkSqlException($"There is no table or view '{reference.Name}'.");
        }

        if (views.Contains(name, StringComparer.OrdinalIgnoreCase) || views.Count >= MaxViewDepth)
        {
            throw new TkSqlException($"View '{name}' refers to itself: {string.Join(" -> ", views.Append(name))}.");
        }

        // A view is its query, run and read like a table.
        var node = new PlanNode($"View {name}", null);
        var inner = await new SelectRunner(transaction, parameters, cancellationToken)
            .RunAsync((SelectStatement)SqlParser.Parse(text), node, [.. views, name]).ConfigureAwait(false);
        node.Actual = inner.Rows.Count;
        _sources.Add(new Source(reference.Alias, name, inner.Names, inner.Rows, node) { Offset = offset });
    }

    /// <summary>The text of a view, by name, exactly or ignoring case. Null if there is none.</summary>
    internal static async ValueTask<(string Name, string Text)?> ViewAsync(TkTransaction transaction, string name, CancellationToken cancellationToken)
    {
        string? actual = null;
        await foreach (var existing in SqlTable.NamesAsync(transaction, 8, cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(existing, name, StringComparison.Ordinal))
            {
                actual = existing;
                break;
            }

            actual = string.Equals(existing, name, StringComparison.OrdinalIgnoreCase) ? existing : actual;
        }

        if (actual is null)
        {
            return null;
        }

        var text = await CatalogStore.ReadAsync(transaction.Source, transaction.CatalogRoot, CatalogStore.ViewKey(actual), cancellationToken).ConfigureAwait(false);
        return (actual, Encoding.UTF8.GetString(text));
    }

    /// <summary>The rows of the first source, each at the start of a combined row. How to reach them is decided here, before any is read.</summary>
    private async ValueTask<IAsyncEnumerable<object?[]>> BaseAsync(SqlExpr? where, int width)
    {
        var source = _sources[0];
        IAsyncEnumerable<object?[]> rows;
        if (source.Table is { } table)
        {
            (rows, source.Node) = await ChooseAsync(table, source, where).ConfigureAwait(false);
        }
        else
        {
            rows = Replay(source.Rows!);
        }

        return Widen();

        async IAsyncEnumerable<object?[]> Widen()
        {
            await foreach (var row in rows.ConfigureAwait(false))
            {
                source.Node.Actual += source.Table is null ? 0 : 1;
                if (width == row.Length)
                {
                    yield return row;
                    continue;
                }

                var combined = new object?[width];
                row.CopyTo(combined, 0);
                yield return combined;
            }
        }
    }

    /// <summary>
    /// Picks how to reach the rows of a table for a WHERE: by key, by a range of the key, by the index
    /// expected to give fewest rows, or all of them. The estimates come from the shape of each tree.
    /// </summary>
    private async ValueTask<(IAsyncEnumerable<object?[]> Rows, PlanNode Node)> ChooseAsync(SqlTable table, Source source, SqlExpr? where)
    {
        // What the WHERE fixes for each column, from the conditions that compare a column with a value; and
        // what it says of a value at a path of a JSON column, kept as written until an index says its kind.
        var bounds = new Dictionary<int, Bound>();
        var atPaths = new Dictionary<(int Column, string Path), List<(string Op, object Value)>>();
        foreach (var conjunct in Conjuncts(where))
        {
            var parts = conjunct switch
            {
                Binary { Op: "=" or "<" or "<=" or ">" or ">=" } binary => new[] { (binary.Op, binary.Left, binary.Right) },
                Between { Negated: false } between => [(">=", between.Operand, between.Low), ("<=", between.Operand, between.High)],
                _ => [],
            };
            foreach (var (op, left, right) in parts)
            {
                var (path, other, flipped) = (JsonTerm(left), right, op);
                if (path is null && JsonTerm(right) is { } reversed)
                {
                    (path, other, flipped) = (reversed, left, op switch { "<" => ">", "<=" => ">=", ">" => "<", ">=" => "<=", _ => op });
                }

                if (path is var (jsonColumn, jsonPath) && !Binder.ReadsColumns(other) && Resolve(jsonColumn) is var at
                    && at >= source.Offset && at < source.Offset + table.Columns.Length && Binder.Constant(other, parameters) is { } compared)
                {
                    var pathKey = (at - source.Offset, jsonPath.Text);
                    (atPaths.TryGetValue(pathKey, out var known) ? known : atPaths[pathKey] = []).Add((flipped, compared));
                    continue;
                }

                var (column, value, comparison) = (left, right) switch
                {
                    (ColumnRef c, _) when !Binder.ReadsColumns(right) => (c, right, op),
                    (_, ColumnRef c) when !Binder.ReadsColumns(left) => (c, left, op switch { "<" => ">", "<=" => ">=", ">" => "<", ">=" => "<=", _ => op }),
                    _ => (null, null, op),
                };
                if (column is null || Resolve(column) is var slot && (slot < source.Offset || slot >= source.Offset + table.Columns.Length))
                {
                    continue;
                }

                var ordinal = Resolve(column) - source.Offset;
                if (Binder.Constant(value!, parameters) is not { } constant)
                {
                    continue;
                }

                object typed;
                try
                {
                    typed = SqlValues.Convert(constant, table.Columns[ordinal].Type, table.Columns[ordinal].Name);
                }
                catch (TkSqlException)
                {
                    continue;
                }

                // A value that does not survive the conversion (1.5 against a whole column) cannot bound a search.
                if (Binder.Compare(typed, constant) != 0)
                {
                    continue;
                }

                var bound = bounds.GetValueOrDefault(ordinal) ?? new Bound();
                bounds[ordinal] = bound;
                bound.Add(comparison, typed);
            }
        }

        var candidates = new List<(int Rank, long Estimate, string Text, Func<IAsyncEnumerable<object?[]>> Rows)>();
        var total = await table.EstimateAsync(table.Root, default, default, cancellationToken).ConfigureAwait(false);
        candidates.Add((4, total, $"Scan of {table.Name}", () => table.ScanAsync(default, default, cancellationToken)));

        if (bounds.TryGetValue(table.KeyColumn, out var key))
        {
            var keyName = table.Columns[table.KeyColumn].Name;
            if (key.Equal is { } exact)
            {
                var bytes = table.Key(exact);
                candidates.Add((0, 1, $"Key lookup in {table.Name} ({keyName} = {SqlValues.Show(exact)})", () => One(table, bytes)));
            }
            else
            {
                var from = key.Low is null ? [] : key.LowInclusive ? table.Key(key.Low) : [.. table.Key(key.Low), 0];
                var to = key.High is null ? [] : key.HighInclusive ? [.. table.Key(key.High), 0] : table.Key(key.High);
                var estimate = await table.EstimateAsync(table.Root, from, to, cancellationToken).ConfigureAwait(false);
                candidates.Add((3, estimate, $"Key range of {table.Name} ({key.Describe(keyName)})", () => table.ScanAsync(from, to, cancellationToken)));
            }
        }

        foreach (var index in table.Indexes)
        {
            // A partial index holds only some rows, and one over an expression cannot be searched by column.
            if (index.IsZoneMap || index.Where.Length > 0 || index.Columns.Contains(-1))
            {
                continue;
            }

            var fieldBounds = Enumerable.Range(0, index.Columns.Length).Select(i => BoundOf(index, i)).ToArray();
            var equal = 0;
            while (equal < index.Columns.Length && fieldBounds[equal] is { Equal: not null })
            {
                equal++;
            }

            var range = equal < index.Columns.Length ? fieldBounds[equal] : null;
            range = range is { Equal: null } ? range : null;
            if (equal == 0 && range is null)
            {
                continue;
            }

            var values = Enumerable.Range(0, equal).Select(i => fieldBounds[i]!.Equal).ToArray();
            var prefix = SqlTable.IndexPrefix(index, values, equal);
            byte[] start;
            byte[] end;
            var described = string.Join(", ", Enumerable.Range(0, equal).Select(i => $"{FieldName(index, i)} = {SqlValues.Show(values[i])}"));
            if (range is null)
            {
                (start, end) = (prefix, SqlTable.After(prefix));
            }
            else
            {
                // A nullable field starts with its mark: the values are those marked 1, after the NULLs.
                var nullable = index.Nullable[equal];
                var low = range.Low is null ? null : SqlTable.IndexPrefix(index, [.. values, range.Low], equal + 1);
                var high = range.High is null ? null : SqlTable.IndexPrefix(index, [.. values, range.High], equal + 1);
                start = low is null ? (nullable ? [.. prefix, 1] : prefix) : range.LowInclusive ? low : SqlTable.After(low);
                end = high is null ? (nullable ? [.. prefix, 2] : SqlTable.After(prefix)) : range.HighInclusive ? SqlTable.After(high) : high;
                described += (described.Length > 0 ? ", " : string.Empty) + range.Describe(FieldName(index, equal));
            }

            var estimate = await table.EstimateAsync(index.CurrentRoot, start, end, cancellationToken).ConfigureAwait(false);
            candidates.Add((
                index.IsUnique && equal == index.Columns.Length ? 1 : 2,
                index.IsUnique && equal == index.Columns.Length ? Math.Min(estimate, 1) : estimate,
                $"Index {index.Name} of {table.Name} ({described})",
                () => table.IndexAsync(index, start, end, cancellationToken)));
        }

        var best = candidates.OrderBy(static candidate => candidate.Estimate).ThenBy(static candidate => candidate.Rank).First();
        return (best.Rows(), new PlanNode(best.Text, best.Estimate));

        string FieldName(StoredIndex index, int field) =>
            index.Paths[field] is { } path ? $"{table.Columns[index.Columns[field]].Name} at {path.Text}" : table.Columns[index.Columns[field]].Name;

        // What the WHERE says of a field of an index: its column, or the value at a path, in the kind the index holds.
        Bound? BoundOf(StoredIndex index, int field)
        {
            if (index.Paths[field] is not { } path)
            {
                return bounds.GetValueOrDefault(index.Columns[field]);
            }

            if (!atPaths.TryGetValue((index.Columns[field], path.Text), out var conditions))
            {
                return null;
            }

            Bound? bound = null;
            foreach (var (op, value) in conditions)
            {
                // Text is compared with text. A number is held as the nearest DOUBLE, which may round: the bounds
                // are made inclusive, so that the index gives what might match and the WHERE decides.
                object? typed = (index.Types[field], value) switch
                {
                    (TkColumnType.Text, string text) => text,
                    (TkColumnType.F64, _) when SqlValues.IsNumber(value) => Convert.ToDouble(value, CultureInfo.InvariantCulture),
                    _ => null,
                };
                if (typed is null)
                {
                    continue;
                }

                bound ??= new Bound();
                bound.Add(index.Types[field] == TkColumnType.F64 ? op switch { ">" => ">=", "<" => "<=", _ => op } : op, typed);
            }

            return bound;
        }
    }

    /// <summary>A value at a path of a column, as SQL reads it: <c>column -&gt;&gt; 'path'</c>, <c>JSON_VALUE</c> or <c>JSON_EXTRACT</c>.</summary>
    private static (ColumnRef Column, TkJsonPath Path)? JsonTerm(SqlExpr expression)
    {
        var (column, path) = expression switch
        {
            Binary { Op: "->>", Left: ColumnRef c, Right: Literal { Value: string text } } => (c, text.StartsWith('$') ? text : "$.\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""),
            Binary { Op: "->>", Left: ColumnRef c, Right: Literal { Value: long position and >= 0 and <= int.MaxValue } } => (c, $"$[{position.ToString(CultureInfo.InvariantCulture)}]"),
            Call { Name: "JSON_VALUE" or "JSON_EXTRACT", Arguments: [ColumnRef c, Literal { Value: string text }] } when text.StartsWith('$') => (c, text),
            _ => ((ColumnRef?)null, (string?)null),
        };
        return column is not null && TkJsonPath.TryParse(path!, out var parsed) ? (column, parsed) : null;
    }

    private async IAsyncEnumerable<object?[]> One(SqlTable table, byte[] key)
    {
        if (await table.GetAsync(key, cancellationToken).ConfigureAwait(false) is { } row)
        {
            yield return row;
        }
    }

    /// <summary>Joins the rows so far with one more source, by the conditions of its ON.</summary>
    private async ValueTask<(IAsyncEnumerable<object?[]> Rows, PlanNode Node)> JoinAsync(
        IAsyncEnumerable<object?[]> left, PlanNode leftNode, int index, SqlExpr on, bool keepUnmatched)
    {
        var source = _sources[index];
        var condition = Binder.Bind(on, Resolve, parameters, null);

        // An equality between a column of this source and something the earlier ones give is what a lookup needs.
        (int Column, Evaluator Value)? equality = null;
        foreach (var conjunct in Conjuncts(on))
        {
            if (conjunct is not Binary { Op: "=" } binary)
            {
                continue;
            }

            foreach (var (mine, theirs) in new[] { (binary.Left, binary.Right), (binary.Right, binary.Left) })
            {
                if (mine is ColumnRef column && Resolve(column) is var slot && slot >= source.Offset && slot < source.Offset + source.Columns.Length
                    && !Uses(theirs, source.Offset, int.MaxValue))
                {
                    equality ??= (slot - source.Offset, Binder.Bind(theirs, Resolve, parameters, null));
                }
            }
        }

        PlanNode node;
        Func<object?[], IAsyncEnumerable<object?[]>> matches;
        if (equality is var (joined, value) && source.Table is { } table && joined == table.KeyColumn)
        {
            node = new PlanNode($"Join: key lookup in {table.Name} on {Show(on)}", null);
            matches = row => Lookup(table, value(row));
        }
        else if (equality is var (indexed, by) && source.Table is { } searched
            && searched.Indexes.Find(candidate => !candidate.IsZoneMap && candidate.Where.Length == 0 && candidate.Columns.Length > 0 && candidate.Columns[0] == indexed) is { } usable)
        {
            node = new PlanNode($"Join: index {usable.Name} of {searched.Name} on {Show(on)}", null);
            matches = row => Probe(searched, usable, by(row));
        }
        else
        {
            // Read once and kept in memory, by the joined value if there is one.
            var all = new List<object?[]>();
            if (source.Table is { } scanned)
            {
                await foreach (var row in scanned.ScanAsync(default, default, cancellationToken).ConfigureAwait(false))
                {
                    all.Add(row);
                }
            }
            else
            {
                all = source.Rows!;
            }

            if (equality is var (hashed, probe))
            {
                var byValue = all.Where(row => row[hashed] is not null).ToLookup(row => new SqlKey([row[hashed]]));
                node = new PlanNode($"Join: {source.Name} read once ({all.Count} rows) and matched by value on {Show(on)}", all.Count);
                matches = row => probe(row) is { } needle ? Replay([.. byValue[new SqlKey([needle])]]) : Replay([]);
            }
            else
            {
                node = new PlanNode($"Join: {source.Name} read once ({all.Count} rows) and compared with every row on {Show(on)}", all.Count);
                matches = _ => Replay(all);
            }
        }

        node.Children.Add(leftNode);
        if (source.Table is null)
        {
            node.Children.Add(source.Node);
        }

        return (Joined(), node);

        async IAsyncEnumerable<object?[]> Joined()
        {
            await foreach (var row in left.ConfigureAwait(false))
            {
                var matched = false;
                await foreach (var match in matches(row).ConfigureAwait(false))
                {
                    var combined = (object?[])row.Clone();
                    match.CopyTo(combined, source.Offset);
                    if (Binder.Truth(condition(combined)) == true)
                    {
                        matched = true;
                        node.Actual++;
                        yield return combined;
                    }
                }

                // LEFT JOIN: a row with nothing to match stays, with NULLs in the columns of this source.
                if (keepUnmatched && !matched)
                {
                    node.Actual++;
                    yield return (object?[])row.Clone();
                }
            }
        }
    }

    private IAsyncEnumerable<object?[]> Lookup(SqlTable table, object? value)
    {
        if (value is null)
        {
            return Replay([]);
        }

        try
        {
            return One(table, table.Key(SqlValues.Convert(value, table.Columns[table.KeyColumn].Type, table.Columns[table.KeyColumn].Name)));
        }
        catch (TkSqlException)
        {
            // A value the key cannot hold matches no row.
            return Replay([]);
        }
    }

    private IAsyncEnumerable<object?[]> Probe(SqlTable table, StoredIndex index, object? value)
    {
        if (value is null)
        {
            return Replay([]);
        }

        try
        {
            var column = table.Columns[index.Columns[0]];
            var prefix = SqlTable.IndexPrefix(index, [SqlValues.Convert(value, column.Type, column.Name)], 1);
            return table.IndexAsync(index, prefix, SqlTable.After(prefix), cancellationToken);
        }
        catch (TkSqlException)
        {
            return Replay([]);
        }
    }

    /// <summary>Whether an expression reads a column at a position of the combined row from <paramref name="from"/> on.</summary>
    private bool Uses(SqlExpr expression, int from, int to) => expression switch
    {
        ColumnRef column => Resolve(column) is var slot && slot >= from && slot < to,
        Call call => call.Arguments.Any(argument => Uses(argument, from, to)),
        Binary binary => Uses(binary.Left, from, to) || Uses(binary.Right, from, to),
        Unary unary => Uses(unary.Operand, from, to),
        IsNull isNull => Uses(isNull.Operand, from, to),
        InList list => Uses(list.Operand, from, to) || list.Items.Any(item => Uses(item, from, to)),
        Between between => Uses(between.Operand, from, to) || Uses(between.Low, from, to) || Uses(between.High, from, to),
        Like like => Uses(like.Operand, from, to) || Uses(like.Pattern, from, to),
        _ => false,
    };

    /// <summary>
    /// Groups the rows and computes the aggregates. Returns one row per group (the grouping values, then
    /// the aggregates) and how to bind an expression of the select list against those rows.
    /// </summary>
    private async ValueTask<(List<object?[]> Rows, Func<SqlExpr, Evaluator> Bind)> GroupAsync(
        IAsyncEnumerable<object?[]> rows, SelectStatement query, List<SelectItem> items)
    {
        // Keys and the arguments of the aggregates are computed a block of rows at a time, like the select list.
        parameters.Batching = true;
        var keys = query.GroupBy.Select(expression => Binder.Bind(expression, Resolve, parameters, null)).ToArray();
        var keyTexts = query.GroupBy.Select(Binder.Key).ToList();
        var calls = new List<Call>();
        foreach (var expression in items.Select(static item => item.Expression).Append(query.Having).Concat(query.OrderBy.Select(static item => item.Expression)))
        {
            Collect(expression);
        }

        var arguments = calls.Select(call => call.Star ? null : call.Arguments.Count == 1
            ? Binder.Bind(call.Arguments[0], Resolve, parameters, null)
            : throw new TkSqlException($"{call.Name} takes one argument.")).ToArray();

        parameters.Batching = false;
        var vectors = parameters.VectorCalls.ToArray();
        parameters.VectorCalls.Clear();

        var groups = new Dictionary<SqlKey, (object?[] Key, Accumulator[] Aggregates)>();
        var ordered = new List<(object?[] Key, Accumulator[] Aggregates)>();
        var block = new List<object?[]>(vectors.Length == 0 ? 1 : BlockSize);
        await foreach (var row in rows.ConfigureAwait(false))
        {
            block.Add(row);
            if (block.Count == block.Capacity)
            {
                Accumulate();
            }
        }

        Accumulate();

        void Accumulate()
        {
            foreach (var vector in vectors)
            {
                vector.Prepare(block);
            }

            foreach (var row in block)
            {
                var values = Array.ConvertAll(keys, key => key(row));
                var identity = new SqlKey(values);
                if (!groups.TryGetValue(identity, out var group))
                {
                    group = (values, [.. calls.Select(static call => new Accumulator(call))]);
                    groups.Add(identity, group);
                    ordered.Add(group);
                }

                for (var i = 0; i < calls.Count; i++)
                {
                    group.Aggregates[i].Add(arguments[i] is { } argument ? argument(row) : true);
                }
            }

            block.Clear();
            foreach (var vector in vectors)
            {
                vector.Clear();
            }
        }

        // With no GROUP BY there is one group even if there were no rows: COUNT is 0 and the rest NULL.
        if (keys.Length == 0 && ordered.Count == 0)
        {
            ordered.Add(([], [.. calls.Select(static call => new Accumulator(call))]));
        }

        var output = ordered.Select(group => (object?[])[.. group.Key, .. group.Aggregates.Select(static aggregate => aggregate.Result())]).ToList();
        Func<SqlExpr, Evaluator> bind = expression => Binder.Bind(
            expression,
            column => throw new TkSqlException($"Column '{column.Name}' must be in the GROUP BY or inside an aggregate."),
            parameters,
            part =>
            {
                var slot = keyTexts.IndexOf(Binder.Key(part));
                if (slot < 0 && part is Call call && Binder.IsAggregate(call))
                {
                    slot = keys.Length + calls.FindIndex(known => Binder.Key(known) == Binder.Key(call));
                }

                return slot < 0 ? null : row => row[slot];
            });

        if (query.Having is not null)
        {
            var having = bind(query.Having);
            output = [.. output.Where(row => Binder.Truth(having(row)) == true)];
        }

        return (output, bind);

        void Collect(SqlExpr? expression)
        {
            switch (expression)
            {
                case Call call when Binder.IsAggregate(call):
                    if (!calls.Exists(known => Binder.Key(known) == Binder.Key(call)))
                    {
                        calls.Add(call);
                    }

                    break;
                case Call call:
                    foreach (var argument in call.Arguments)
                    {
                        Collect(argument);
                    }

                    break;
                case Binary binary:
                    Collect(binary.Left);
                    Collect(binary.Right);
                    break;
                case Unary unary:
                    Collect(unary.Operand);
                    break;
                case IsNull isNull:
                    Collect(isNull.Operand);
                    break;
                case Between between:
                    Collect(between.Operand);
                    Collect(between.Low);
                    Collect(between.High);
                    break;
                case InList list:
                    Collect(list.Operand);
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>What a WHERE fixes for one column: a value, or the ends of a range.</summary>
    private sealed class Bound
    {
        public object? Equal { get; private set; }

        public object? Low { get; private set; }

        public bool LowInclusive { get; private set; }

        public object? High { get; private set; }

        public bool HighInclusive { get; private set; }

        public void Add(string op, object value)
        {
            switch (op)
            {
                case "=":
                    Equal ??= value;
                    break;
                case ">" or ">=":
                    if (Low is null || Binder.Compare(value, Low) is var lower && (lower > 0 || (lower == 0 && op == ">")))
                    {
                        (Low, LowInclusive) = (value, op == ">=");
                    }

                    break;
                default:
                    if (High is null || Binder.Compare(value, High) is var upper && (upper < 0 || (upper == 0 && op == "<")))
                    {
                        (High, HighInclusive) = (value, op == "<=");
                    }

                    break;
            }
        }

        public string Describe(string column) =>
            string.Join(" and ", new[]
            {
                Low is null ? null : $"{column} {(LowInclusive ? ">=" : ">")} {SqlValues.Show(Low)}",
                High is null ? null : $"{column} {(HighInclusive ? "<=" : "<")} {SqlValues.Show(High)}",
            }.Where(static part => part is not null));
    }

    /// <summary>One aggregate over one group.</summary>
    private sealed class Accumulator(Call call)
    {
        private readonly HashSet<SqlKey>? _distinct = call.Distinct ? [] : null;
        private long _count;
        private object? _value;

        public void Add(object? value)
        {
            if (value is null || (_distinct is not null && !_distinct.Add(new SqlKey([value]))))
            {
                return;
            }

            _count++;
            _value = call.Name switch
            {
                "SUM" or "AVG" => _value is null ? value : Binder.Arithmetic("+", _value, value),
                "MIN" => _value is null || Binder.Compare(value, _value) < 0 ? value : _value,
                "MAX" => _value is null || Binder.Compare(value, _value) > 0 ? value : _value,
                _ => null,
            };
        }

        public object? Result() => call.Name switch
        {
            "COUNT" => _count,
            "AVG" when _value is not null => _value is double or float
                ? Convert.ToDouble(_value, CultureInfo.InvariantCulture) / _count
                : Convert.ToDecimal(_value, CultureInfo.InvariantCulture) / _count,
            _ => _value,
        };
    }

    /// <summary>Something a query reads rows from: a table, or a view already run.</summary>
    private sealed class Source
    {
        public Source(string alias, SqlTable table)
        {
            Alias = alias;
            Name = table.Name;
            Table = table;
            Columns = [.. table.Columns.Select(static column => column.Name)];
            Kinds = [.. table.Columns.Select(static column => (TkColumnType?)column.Type)];
            Node = new PlanNode($"Scan of {table.Name}", null);
        }

        public Source(string alias, string name, string[] columns, List<object?[]> rows, PlanNode node)
        {
            Alias = alias;
            Name = name;
            Rows = rows;
            Columns = columns;
            Kinds = new TkColumnType?[columns.Length];
            Node = node;
        }

        public string Alias { get; }

        public string Name { get; }

        public SqlTable? Table { get; }

        public List<object?[]>? Rows { get; }

        public string[] Columns { get; }

        public TkColumnType?[] Kinds { get; }

        public int Offset { get; init; }

        public PlanNode Node { get; set; }
    }
}
