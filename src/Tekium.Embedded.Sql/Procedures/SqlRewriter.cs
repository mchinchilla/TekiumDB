// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Sql.Parsing;

namespace Tekium.Embedded.Sql.Procedures;

/// <summary>
/// Replaces column references of an expression or a statement: how the variables of a procedure, which
/// are parsed as columns, become the parameters of its SQL statements.
/// </summary>
internal static class SqlRewriter
{
    public static SqlExpr Expression(SqlExpr expression, Func<ColumnRef, SqlExpr?> replace) => expression switch
    {
        ColumnRef column => replace(column) ?? column,
        Binary binary => binary with { Left = Expression(binary.Left, replace), Right = Expression(binary.Right, replace) },
        Unary unary => unary with { Operand = Expression(unary.Operand, replace) },
        IsNull isNull => isNull with { Operand = Expression(isNull.Operand, replace) },
        InList list => list with { Operand = Expression(list.Operand, replace), Items = [.. list.Items.Select(item => Expression(item, replace))] },
        Between between => between with
        {
            Operand = Expression(between.Operand, replace), Low = Expression(between.Low, replace), High = Expression(between.High, replace),
        },
        Like like => like with { Operand = Expression(like.Operand, replace), Pattern = Expression(like.Pattern, replace) },
        Call call => call with { Arguments = [.. call.Arguments.Select(argument => Expression(argument, replace))] },
        Case choice => choice with
        {
            Branches = [.. choice.Branches.Select(branch => (Expression(branch.When, replace), Expression(branch.Then, replace)))],
            Otherwise = choice.Otherwise is null ? null : Expression(choice.Otherwise, replace),
        },
        Subquery subquery => subquery with { Query = (SelectStatement)Statement(subquery.Query, replace) },
        InQuery inQuery => inQuery with { Operand = Expression(inQuery.Operand, replace), Query = (SelectStatement)Statement(inQuery.Query, replace) },
        _ => expression,
    };

    public static SqlStatement Statement(SqlStatement statement, Func<ColumnRef, SqlExpr?> replace)
    {
        SqlExpr? Maybe(SqlExpr? expression) => expression is null ? null : Expression(expression, replace);
        IReadOnlyList<SelectItem>? Items(IReadOnlyList<SelectItem>? items) => items is null ? null : [.. items.Select(item => item with { Expression = Maybe(item.Expression) })];
        return statement switch
        {
            SelectStatement select => select with
            {
                Items = [.. select.Items.Select(item => item with { Expression = Maybe(item.Expression) })],
                From = select.From is { Query: { } derived } first ? first with { Query = (SelectStatement)Statement(derived, replace) } : select.From,
                Joins = [.. select.Joins.Select(join => join with
                {
                    On = Expression(join.On, replace),
                    Table = join.Table.Query is { } joined ? join.Table with { Query = (SelectStatement)Statement(joined, replace) } : join.Table,
                })],
                Where = Maybe(select.Where),
                GroupBy = [.. select.GroupBy.Select(key => Expression(key, replace))],
                Having = Maybe(select.Having),
                OrderBy = [.. select.OrderBy.Select(item => item with { Expression = Expression(item.Expression, replace) })],
                Limit = Maybe(select.Limit),
                Offset = Maybe(select.Offset),
            },
            InsertStatement insert => insert with
            {
                Rows = [.. insert.Rows.Select(row => (IReadOnlyList<SqlExpr>)[.. row.Select(value => Expression(value, replace))])],
                Returning = Items(insert.Returning),
            },
            UpdateStatement update => update with
            {
                Set = [.. update.Set.Select(set => (set.Column, Expression(set.Value, replace)))],
                Where = Maybe(update.Where),
                Returning = Items(update.Returning),
            },
            DeleteStatement delete => delete with { Where = Maybe(delete.Where), Returning = Items(delete.Returning) },
            _ => statement,
        };
    }

    /// <summary>The tables a statement names.</summary>
    public static IEnumerable<string> Tables(SqlStatement statement) => statement switch
    {
        SelectStatement select => (select.From is null ? [] : new[] { select.From }).Concat(select.Joins.Select(static join => join.Table))
            .SelectMany(static table => table.Query is null ? [table.Name] : Tables(table.Query)),
        InsertStatement insert => [insert.Table],
        UpdateStatement update => [update.Table],
        DeleteStatement delete => [delete.Table],
        _ => [],
    };
}
