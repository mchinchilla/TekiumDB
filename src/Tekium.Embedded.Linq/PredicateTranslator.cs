// Copyright (c) Tekium. Licensed under the MIT License.

using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace Tekium.Embedded.Linq;

/// <summary>
/// Turns a C# predicate over a row type into the filter the engine runs. The expression tree is read,
/// never compiled. What the engine cannot answer (an "or", a "not equal", a method called on a column) is
/// refused with <see cref="NotSupportedException"/>: nothing is ever left to be filtered in memory.
/// </summary>
/// <remarks>
/// A predicate is conditions joined by <c>&amp;&amp;</c>, each comparing a stored property with a value:
/// <c>==</c>, <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>, <c>== null</c>, <c>!= null</c>,
/// <c>HasValue</c>, or a <see langword="bool"/> property by itself or negated. The value is anything that
/// does not depend on the row: a constant, a variable, a property, a method call. It is computed once,
/// when the predicate is translated.
/// </remarks>
internal static class PredicateTranslator<TRow>
    where TRow : class, ITkRow<TRow>
{
    public static TkFilter<TRow> Translate(Expression<Func<TRow, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return Condition(predicate.Body, predicate.Parameters[0], predicate);
    }

    private static TkFilter<TRow> Condition(Expression expression, ParameterExpression row, LambdaExpression whole)
    {
        switch (expression)
        {
            case BinaryExpression { NodeType: ExpressionType.AndAlso } both:
                return Condition(both.Left, row, whole).And(Condition(both.Right, row, whole));

            case BinaryExpression { NodeType: ExpressionType.OrElse or ExpressionType.Or }:
                throw Refuse(whole, expression, "it joins conditions with || and the engine only joins them with &&. Run one query for each side");

            case BinaryExpression { NodeType: ExpressionType.NotEqual } different when !IsNull(different.Left) && !IsNull(different.Right):
                throw Refuse(whole, expression, "it uses != and the engine has no \"not equal\" condition. Use a range on each side, or filter the rows after reading them");

            case BinaryExpression comparison when IsComparison(comparison.NodeType):
                return Comparison(comparison, row, whole);

            case UnaryExpression { NodeType: ExpressionType.Not } not:
                if (Field(not.Operand, row) is { } negated && not.Operand.Type == typeof(bool))
                {
                    return Compare(negated, ExpressionType.Equal, false, whole, expression);
                }

                if (HasValue(not.Operand, row) is { } absent)
                {
                    return absent.IsNull();
                }

                throw Refuse(whole, expression, "it negates a condition (Not) and the engine cannot. Write the opposite condition instead");

            case MemberExpression when HasValue(expression, row) is { } present:
                return present.IsNotNull();

            case MemberExpression when expression.Type == typeof(bool) && Field(expression, row) is { } flag:
                return Compare(flag, ExpressionType.Equal, true, whole, expression);

            default:
                throw Refuse(whole, expression, Unknown(expression, row));
        }
    }

    private static TkFilter<TRow> Comparison(BinaryExpression comparison, ParameterExpression row, LambdaExpression whole)
    {
        var left = Field(comparison.Left, row);
        var right = Field(comparison.Right, row);
        if (left is null && right is null)
        {
            throw Refuse(whole, comparison, Unknown(DependsOn(comparison.Left, row) ? comparison.Left : comparison.Right, row));
        }

        // The property on the right: the same comparison read from the other side.
        var (field, valueSide, operation) = left is not null
            ? (left, comparison.Right, comparison.NodeType)
            : (right!, comparison.Left, Flip(comparison.NodeType));
        if (DependsOn(valueSide, row))
        {
            throw Refuse(whole, comparison, "it compares two properties of the row and the engine compares a property with a value");
        }

        var value = Value(valueSide, whole);
        if (value is null)
        {
            return operation switch
            {
                ExpressionType.Equal => field.IsNull(),
                ExpressionType.NotEqual => field.IsNotNull(),
                _ => throw Refuse(whole, comparison, "it orders a property against null, which no row can satisfy"),
            };
        }

        return Compare(field, operation, value, whole, comparison);
    }

    private static TkFilter<TRow> Compare(TkField<TRow> field, ExpressionType operation, object value, LambdaExpression whole, Expression part)
    {
        try
        {
            return field.Accept(new Comparer(operation, value));
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            throw Refuse(whole, part, $"the value {value} ({value.GetType().Name}) cannot be compared with that property: {exception.Message}");
        }
    }

    /// <summary>The field of a stored property of the row, seen through the conversions the compiler adds. Null if it is not one.</summary>
    private static TkField<TRow>? Field(Expression expression, ParameterExpression row)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked, Method: null } converted)
        {
            expression = converted.Operand;
        }

        // property.Value of a nullable property is the property.
        if (expression is MemberExpression { Member.Name: "Value", Expression: { } inner } && Nullable.GetUnderlyingType(inner.Type) is not null)
        {
            expression = inner;
        }

        return expression is MemberExpression { Expression: { } target } member && target == row ? TRow.FindField(member.Member.Name) : null;
    }

    /// <summary>The field of <c>row.Property.HasValue</c>. Null if the expression is not that.</summary>
    private static TkField<TRow>? HasValue(Expression expression, ParameterExpression row) =>
        expression is MemberExpression { Member.Name: "HasValue", Expression: { } inner } && Nullable.GetUnderlyingType(inner.Type) is not null
            ? Field(inner, row)
            : null;

    private static bool IsComparison(ExpressionType type) =>
        type is ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual
            or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual;

    private static ExpressionType Flip(ExpressionType type) => type switch
    {
        ExpressionType.LessThan => ExpressionType.GreaterThan,
        ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
        ExpressionType.GreaterThan => ExpressionType.LessThan,
        ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
        _ => type,
    };

    private static bool IsNull(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert } converted)
        {
            expression = converted.Operand;
        }

        return expression is ConstantExpression { Value: null } or DefaultExpression;
    }

    private static bool DependsOn(Expression expression, ParameterExpression row)
    {
        var finder = new RowFinder(row);
        finder.Visit(expression);
        return finder.Found;
    }

    /// <summary>Why an expression that involves the row is not a condition the engine has.</summary>
    private static string Unknown(Expression expression, ParameterExpression row)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } converted)
        {
            expression = converted.Operand;
        }

        return expression switch
        {
            MemberExpression { Expression: { } target } member when target == row =>
                $"'{member.Member.Name}' is not a stored column nor an indexed expression of {typeof(TRow).Name}",
            MemberExpression member when DependsOn(member, row) =>
                $"it reads '{member.Member.Name}' of a property of the row, and the engine compares the property itself",
            MethodCallExpression call when DependsOn(call, row) =>
                $"it calls {call.Method.Name} on the row, and the engine only compares stored properties with values",
            BinaryExpression binary when DependsOn(binary, row) =>
                $"it computes '{binary}' from the row, and the engine only compares stored properties with values. Move the arithmetic to the other side",
            ConstantExpression constant => $"it is the constant {constant.Value}, not a condition on the row",
            _ => $"'{expression}' is not a condition the engine has",
        };
    }

    /// <summary>
    /// Computes the side of a comparison that does not depend on the row, by walking it: constants,
    /// variables and members are read, calls and constructors are invoked. Never compiled.
    /// </summary>
    private static object? Value(Expression expression, LambdaExpression whole)
    {
        try
        {
            return Evaluate(expression);
        }
        catch (TargetInvocationException exception)
        {
            throw Refuse(whole, expression, $"its value could not be computed: {exception.InnerException?.Message}");
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException or NullReferenceException)
        {
            throw Refuse(whole, expression, $"its value could not be computed: {exception.Message}");
        }

        object? Evaluate(Expression node)
        {
            switch (node)
            {
                case ConstantExpression constant:
                    return constant.Value;

                case DefaultExpression:
                    return null;

                case MemberExpression { Member: FieldInfo field } read:
                    return field.GetValue(read.Expression is null ? null : Evaluate(read.Expression));

                case MemberExpression { Member: PropertyInfo property } read:
                    return property.GetValue(read.Expression is null ? null : Evaluate(read.Expression));

                case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } converted:
                    return Convert(Evaluate(converted.Operand), converted);

                case MethodCallExpression call:
                    return call.Method.Invoke(call.Object is null ? null : Evaluate(call.Object), [.. call.Arguments.Select(Evaluate)]);

                case NewExpression { Constructor: { } constructor } created:
                    return constructor.Invoke([.. created.Arguments.Select(Evaluate)]);

                case BinaryExpression { Method: { } operation } binary:
                    return operation.Invoke(null, [Evaluate(binary.Left), Evaluate(binary.Right)]);

                default:
                    throw Refuse(whole, node, "its value cannot be worked out without compiling it. Compute it into a variable before the query");
            }
        }

        static object? Convert(object? value, UnaryExpression conversion)
        {
            if (value is null)
            {
                return null;
            }

            if (conversion.Method is { } method)
            {
                return method.Invoke(null, [value]);
            }

            var target = Nullable.GetUnderlyingType(conversion.Type) ?? conversion.Type;
            return target.IsInstanceOfType(value)
                ? value
                : target.IsEnum ? Enum.ToObject(target, value) : System.Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }
    }

    private static NotSupportedException Refuse(LambdaExpression whole, Expression part, string why) =>
        new($"The predicate {whole.Body} cannot be translated: in '{part}', {why}.");

    /// <summary>Applies a comparison to a field once its value type is known.</summary>
    private sealed class Comparer(ExpressionType operation, object value) : ITkFieldVisitor<TRow, TkFilter<TRow>>
    {
        public TkFilter<TRow> Visit<TValue>(TkField<TRow, TValue> field)
        {
            var typed = value is TValue same
                ? same
                : typeof(TValue).IsEnum
                    ? (TValue)Enum.ToObject(typeof(TValue), value)
                    : (TValue)System.Convert.ChangeType(value, typeof(TValue), CultureInfo.InvariantCulture);
            return operation switch
            {
                ExpressionType.Equal => field.EqualTo(typed),
                ExpressionType.LessThan => field.LessThan(typed),
                ExpressionType.LessThanOrEqual => field.AtMost(typed),
                ExpressionType.GreaterThan => field.GreaterThan(typed),
                ExpressionType.GreaterThanOrEqual => field.AtLeast(typed),
                _ => throw new ArgumentException($"There is no condition for {operation}."),
            };
        }
    }

    private sealed class RowFinder(ParameterExpression row) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            Found |= node == row;
            return node;
        }
    }
}
