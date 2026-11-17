// Copyright (c) Tekium. Licensed under the MIT License.

using System.Globalization;
using System.Text;
using Tekium.Embedded.Sql.Parsing;
using Tekium.Embedded.Sql.Storage;

namespace Tekium.Embedded.Sql.Execution;

/// <summary>Computes an expression over a row. Null is SQL's NULL, also for "unknown" in a condition.</summary>
internal delegate object? Evaluator(object?[] row);

/// <summary>
/// Turns parsed expressions into functions over a row, by composing closures: nothing is compiled.
/// Conditions follow SQL's three-valued logic: a comparison with NULL is unknown, and only what is
/// true passes a WHERE.
/// </summary>
internal static class Binder
{
    /// <summary>How many distinct argument lists of a deterministic function are remembered before deciding whether it pays.</summary>
    private const int MemoLimit = 1024;

    private static readonly HashSet<string> Aggregates = new(StringComparer.Ordinal) { "COUNT", "SUM", "MIN", "MAX", "AVG" };

    public static bool IsAggregate(Call call) => Aggregates.Contains(call.Name);

    /// <summary>Whether an expression contains an aggregate.</summary>
    public static bool HasAggregate(SqlExpr? expression) => expression switch
    {
        null => false,
        Call call => IsAggregate(call) || call.Arguments.Any(HasAggregate),
        Binary binary => HasAggregate(binary.Left) || HasAggregate(binary.Right),
        Unary unary => HasAggregate(unary.Operand),
        IsNull isNull => HasAggregate(isNull.Operand),
        InList list => HasAggregate(list.Operand) || list.Items.Any(HasAggregate),
        Between between => HasAggregate(between.Operand) || HasAggregate(between.Low) || HasAggregate(between.High),
        Like like => HasAggregate(like.Operand) || HasAggregate(like.Pattern),
        _ => false,
    };

    /// <summary>Whether an expression reads any column.</summary>
    public static bool ReadsColumns(SqlExpr expression) => expression switch
    {
        ColumnRef => true,
        Call call => call.Arguments.Any(ReadsColumns),
        Binary binary => ReadsColumns(binary.Left) || ReadsColumns(binary.Right),
        Unary unary => ReadsColumns(unary.Operand),
        IsNull isNull => ReadsColumns(isNull.Operand),
        InList list => ReadsColumns(list.Operand) || list.Items.Any(ReadsColumns),
        Between between => ReadsColumns(between.Operand) || ReadsColumns(between.Low) || ReadsColumns(between.High),
        Like like => ReadsColumns(like.Operand) || ReadsColumns(like.Pattern),
        _ => false,
    };

    /// <summary>A text that is the same for two expressions exactly when they are the same expression.</summary>
    public static string Key(SqlExpr expression) => expression switch
    {
        Literal literal => $"L({literal.Value?.GetType().Name}:{SqlValues.Show(literal.Value)})",
        Parameter parameter => $"P({parameter.Name.ToUpperInvariant()})",
        ColumnRef column => $"C({column.Table?.ToUpperInvariant()}.{column.Name.ToUpperInvariant()})",
        Binary binary => $"B({binary.Op},{Key(binary.Left)},{Key(binary.Right)})",
        Unary unary => $"U({unary.Op},{Key(unary.Operand)})",
        IsNull isNull => $"N({isNull.Negated},{Key(isNull.Operand)})",
        InList list => $"I({list.Negated},{Key(list.Operand)},{string.Join(',', list.Items.Select(Key))})",
        Between between => $"W({between.Negated},{Key(between.Operand)},{Key(between.Low)},{Key(between.High)})",
        Like like => $"K({like.Negated},{Key(like.Operand)},{Key(like.Pattern)})",
        Call call => $"F({call.Name},{call.Star},{call.Distinct},{string.Join(',', call.Arguments.Select(Key))})",
        _ => expression.ToString(),
    };

    /// <summary>The value of an expression that reads no column: a literal, a parameter, or something computed from them.</summary>
    public static object? Constant(SqlExpr expression, StatementContext parameters) =>
        Bind(expression, static column => throw new TkSqlException($"Column '{column.Name}' cannot be used here: a value is expected."), parameters, null)([]);

    /// <summary>
    /// Builds the function of an expression. <paramref name="resolve"/> says which position of the row a
    /// column is; <paramref name="special"/> may take over a sub-expression (an aggregate, a grouping key).
    /// </summary>
    public static Evaluator Bind(
        SqlExpr expression, Func<ColumnRef, int> resolve, StatementContext parameters, Func<SqlExpr, Evaluator?>? special)
    {
        if (special?.Invoke(expression) is { } taken)
        {
            return taken;
        }

        switch (expression)
        {
            case Literal literal:
                var constant = literal.Value;
                return _ => constant;

            case Parameter parameter:
                var value = Find(parameters, parameter.Name);
                return _ => value;

            case ColumnRef column:
                var slot = resolve(column);
                return row => row[slot];

            case Unary { Op: "NOT" } not:
                var negated = Bind(not.Operand, resolve, parameters, special);
                return row => Truth(negated(row)) is { } truth ? !truth : null;

            case Unary minus:
                var operand = Bind(minus.Operand, resolve, parameters, special);
                return row => operand(row) is { } number ? Arithmetic("-", 0L, number) : null;

            case Binary { Op: "AND" } and:
                var (andLeft, andRight) = (Bind(and.Left, resolve, parameters, special), Bind(and.Right, resolve, parameters, special));
                return row =>
                {
                    var left = Truth(andLeft(row));
                    if (left == false)
                    {
                        return false;
                    }

                    var right = Truth(andRight(row));
                    return right == false ? false : left is null || right is null ? null : true;
                };

            case Binary { Op: "OR" } or:
                var (orLeft, orRight) = (Bind(or.Left, resolve, parameters, special), Bind(or.Right, resolve, parameters, special));
                return row =>
                {
                    var left = Truth(orLeft(row));
                    if (left == true)
                    {
                        return true;
                    }

                    var right = Truth(orRight(row));
                    return right == true ? true : left is null || right is null ? null : false;
                };

            case Binary { Op: "=" or "<>" or "<" or "<=" or ">" or ">=" } comparison:
                var (compareLeft, compareRight) = (Bind(comparison.Left, resolve, parameters, special), Bind(comparison.Right, resolve, parameters, special));
                var op = comparison.Op;
                return row => compareLeft(row) is { } left && compareRight(row) is { } right ? Satisfies(op, Compare(left, right)) : null;

            case Binary { Op: "||" } concat:
                var (textLeft, textRight) = (Bind(concat.Left, resolve, parameters, special), Bind(concat.Right, resolve, parameters, special));
                return row => textLeft(row) is { } left && textRight(row) is { } right ? Text(left) + Text(right) : null;

            case Binary arithmetic:
                var (mathLeft, mathRight) = (Bind(arithmetic.Left, resolve, parameters, special), Bind(arithmetic.Right, resolve, parameters, special));
                var symbol = arithmetic.Op;
                return row => mathLeft(row) is { } left && mathRight(row) is { } right ? Arithmetic(symbol, left, right) : null;

            case IsNull isNull:
                var tested = Bind(isNull.Operand, resolve, parameters, special);
                var wantNull = !isNull.Negated;
                return row => tested(row) is null == wantNull;

            case InList list:
                var item = Bind(list.Operand, resolve, parameters, special);
                var items = list.Items.Select(candidate => Bind(candidate, resolve, parameters, special)).ToArray();
                var notIn = list.Negated;
                return row =>
                {
                    if (item(row) is not { } needle)
                    {
                        return null;
                    }

                    var unknown = false;
                    foreach (var candidate in items)
                    {
                        if (candidate(row) is not { } hay)
                        {
                            unknown = true;
                        }
                        else if (Compare(needle, hay) == 0)
                        {
                            return !notIn;
                        }
                    }

                    return unknown ? null : notIn;
                };

            case Between between:
                var (subject, low, high) = (
                    Bind(between.Operand, resolve, parameters, special), Bind(between.Low, resolve, parameters, special), Bind(between.High, resolve, parameters, special));
                var outside = between.Negated;
                return row => subject(row) is { } x && low(row) is { } from && high(row) is { } to
                    ? (Compare(x, from) >= 0 && Compare(x, to) <= 0) != outside
                    : null;

            case Like like:
                var (matched, pattern) = (Bind(like.Operand, resolve, parameters, special), Bind(like.Pattern, resolve, parameters, special));
                var unlike = like.Negated;
                return row => matched(row) is { } text && pattern(row) is { } wildcard ? Matches(Text(text), Text(wildcard)) != unlike : null;

            case Call call when IsAggregate(call):
                throw new TkSqlException($"{call.Name} is an aggregate and cannot be used here.");

            case Call call:
                return Function(call, [.. call.Arguments.Select(argument => Bind(argument, resolve, parameters, special))], parameters);

            default:
                throw new TkSqlException($"Unsupported expression: {expression}.");
        }
    }

    /// <summary>What a condition says: true, false, or unknown (null).</summary>
    public static bool? Truth(object? value) => value switch
    {
        null => null,
        bool flag => flag,
        _ when SqlValues.IsNumber(value) => Convert.ToDecimal(value, CultureInfo.InvariantCulture) != 0,
        _ => throw new TkSqlException($"{SqlValues.Show(value)} is not a condition: true or false is expected."),
    };

    /// <summary>Orders two values that are not null. A text compared with something else is read as that something.</summary>
    public static int Compare(object left, object right)
    {
        if (left is string && right is not (string or char) && SqlValues.KindOf(right) is { } kind)
        {
            left = SqlValues.Convert(left, kind, "the value compared");
        }
        else if (right is string && left is not (string or char) && SqlValues.KindOf(left) is { } other)
        {
            right = SqlValues.Convert(right, other, "the value compared");
        }

        return SqlValues.Compare(left, right);
    }

    public static bool Satisfies(string op, int comparison) => op switch
    {
        "=" => comparison == 0,
        "<>" => comparison != 0,
        "<" => comparison < 0,
        "<=" => comparison <= 0,
        ">" => comparison > 0,
        _ => comparison >= 0,
    };

    /// <summary>Arithmetic between two numbers: whole numbers stay whole, a decimal makes the result exact, a floating number makes it floating.</summary>
    public static object Arithmetic(string op, object left, object right)
    {
        if (!SqlValues.IsNumber(left) || !SqlValues.IsNumber(right))
        {
            throw new TkSqlException($"{SqlValues.Show(left)} {op} {SqlValues.Show(right)}: arithmetic needs numbers.");
        }

        try
        {
            if (left is double or float || right is double or float)
            {
                var (a, b) = (Convert.ToDouble(left, CultureInfo.InvariantCulture), Convert.ToDouble(right, CultureInfo.InvariantCulture));
                return op switch { "+" => a + b, "-" => a - b, "*" => a * b, "/" => a / b, _ => a % b };
            }

            if (left is decimal || right is decimal)
            {
                var (a, b) = (Convert.ToDecimal(left, CultureInfo.InvariantCulture), Convert.ToDecimal(right, CultureInfo.InvariantCulture));
                return op switch { "+" => a + b, "-" => a - b, "*" => a * b, "/" => a / b, _ => a % b };
            }

            var (x, y) = (Convert.ToInt64(left, CultureInfo.InvariantCulture), Convert.ToInt64(right, CultureInfo.InvariantCulture));
            return checked(op switch { "+" => x + y, "-" => x - y, "*" => x * y, "/" => x / y, _ => x % y });
        }
        catch (DivideByZeroException)
        {
            throw new TkSqlException("Division by zero.");
        }
        catch (OverflowException)
        {
            throw new TkSqlException($"{SqlValues.Show(left)} {op} {SqlValues.Show(right)} is out of range.");
        }
    }

    private static object? Find(StatementContext parameters, string name)
    {
        foreach (var (key, value) in parameters.Values)
        {
            if (string.Equals(key.TrimStart('@', ':', '$'), name, StringComparison.OrdinalIgnoreCase))
            {
                return value is DBNull ? null : value is Enum ? Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType()), CultureInfo.InvariantCulture) : value;
            }
        }

        throw new TkSqlException($"The statement uses parameter @{name}, which was not given.");
    }

    private static Evaluator Function(Call call, Evaluator[] arguments, StatementContext context)
    {
        switch (call.Name)
        {
            case "LOWER" when arguments.Length == 1:
                return row => arguments[0](row) is { } text ? Text(text).ToLowerInvariant() : null;
            case "UPPER" when arguments.Length == 1:
                return row => arguments[0](row) is { } text ? Text(text).ToUpperInvariant() : null;
            case "LENGTH" when arguments.Length == 1:
                return row => arguments[0](row) switch
                {
                    null => null,
                    byte[] bytes => (long)bytes.Length,
                    var text => (long)Text(text).EnumerateRunes().Count(),
                };
            case "ABS" when arguments.Length == 1:
                return row => arguments[0](row) is { } number ? Compare(number, 0L) < 0 ? Arithmetic("-", 0L, number) : number : null;
            case "COALESCE" when arguments.Length > 0:
                return row =>
                {
                    foreach (var argument in arguments)
                    {
                        if (argument(row) is { } value)
                        {
                            return value;
                        }
                    }

                    return null;
                };
            default:
                return Routine(call, arguments, context);
        }
    }

    /// <summary>A function the application registered. One it did not is an error now, while the query is being prepared, not while it runs.</summary>
    private static Evaluator Routine(Call call, Evaluator[] arguments, StatementContext context)
    {
        if (!context.Routines.TryGetValue(call.Name, out var routine))
        {
            throw new TkSqlException(
                $"Unknown function {call.Name} with {arguments.Length} arguments. There are LOWER, UPPER, LENGTH, ABS and COALESCE, the aggregates COUNT, SUM, MIN, MAX and AVG, and the functions the application registers.");
        }

        if (routine.Procedure is not null)
        {
            throw new TkSqlException($"{routine.Name} is a procedure: run it with CALL {routine.Name}(…).");
        }

        if (routine.ParameterCount != arguments.Length)
        {
            throw new TkSqlException($"Function {routine.Name} takes {routine.ParameterCount} arguments; {arguments.Length} were given.");
        }

        if (routine.Vector is not null)
        {
            var site = new VectorCall(routine, arguments);
            if (context.Batching)
            {
                context.VectorCalls.Add(site);
            }

            return site.Evaluate;
        }

        var invoke = routine.Scalar!;
        object? Call(object?[] row)
        {
            var values = new object?[arguments.Length];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = arguments[i](row);
            }

            return Invoke(values);
        }

        object? Invoke(object?[] values)
        {
            try
            {
                return invoke(values);
            }
            catch (Exception exception) when (exception is not TkSqlException and not OperationCanceledException)
            {
                throw new TkSqlException($"Function {routine.Name} failed: {exception.Message}", exception);
            }
        }

        if (!routine.IsDeterministic)
        {
            return Call;
        }

        // The same arguments always give the same result: with no column among them, it is computed once, now.
        if (!call.Arguments.Any(ReadsColumns))
        {
            var text = Key(call);
            if (!context.Folded.TryGetValue(text, out var constant))
            {
                constant = Call([]);
                context.Folded.Add(text, constant);
            }

            return _ => constant;
        }

        // And within the query, a result already computed for the same arguments is reused: until the
        // arguments turn out to be mostly different, when remembering costs more than calling (measured).
        var known = new Dictionary<SqlKey, object?>();
        var hits = 0;
        var remember = true;
        return row =>
        {
            var values = new object?[arguments.Length];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = arguments[i](row);
            }

            if (!remember)
            {
                return Invoke(values);
            }

            var key = new SqlKey(values);
            if (known.TryGetValue(key, out var result))
            {
                hits++;
                return result;
            }

            result = Invoke(values);
            known.Add(key, result);
            if (known.Count >= MemoLimit && hits < known.Count)
            {
                remember = false;
                known.Clear();
            }

            return result;
        };
    }

    private static string Text(object value) => value switch
    {
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>LIKE: % is any run of characters and _ is any one.</summary>
    private static bool Matches(string text, string pattern)
    {
        var (t, p, star, mark) = (0, 0, -1, 0);
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '_' || pattern[p] == text[t]) && pattern[p] != '%')
            {
                t++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '%')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '%')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
