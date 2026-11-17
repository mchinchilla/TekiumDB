// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Sql.Parsing;

/// <summary>An expression of a statement, as parsed.</summary>
internal abstract record SqlExpr;

internal sealed record Literal(object? Value) : SqlExpr;

internal sealed record Parameter(string Name) : SqlExpr;

internal sealed record ColumnRef(string? Table, string Name) : SqlExpr;

/// <summary>An operator between two expressions: =, &lt;&gt;, &lt;, &lt;=, &gt;, &gt;=, AND, OR, +, -, *, /, %, ||.</summary>
internal sealed record Binary(string Op, SqlExpr Left, SqlExpr Right) : SqlExpr;

/// <summary>NOT or the unary minus.</summary>
internal sealed record Unary(string Op, SqlExpr Operand) : SqlExpr;

internal sealed record IsNull(SqlExpr Operand, bool Negated) : SqlExpr;

internal sealed record InList(SqlExpr Operand, IReadOnlyList<SqlExpr> Items, bool Negated) : SqlExpr;

internal sealed record Between(SqlExpr Operand, SqlExpr Low, SqlExpr High, bool Negated) : SqlExpr;

internal sealed record Like(SqlExpr Operand, SqlExpr Pattern, bool Negated) : SqlExpr;

/// <summary>A function: an aggregate (COUNT, SUM, MIN, MAX, AVG) or a scalar one.</summary>
internal sealed record Call(string Name, IReadOnlyList<SqlExpr> Arguments, bool Star, bool Distinct) : SqlExpr;

/// <summary>One thing a SELECT returns: an expression, <c>*</c>, or <c>table.*</c>.</summary>
internal sealed record SelectItem(SqlExpr? Expression, string? Alias, bool Star, string? StarOf);

internal sealed record TableRef(string Name, string Alias);

internal sealed record JoinClause(TableRef Table, SqlExpr On);

internal sealed record OrderItem(SqlExpr Expression, bool Descending);

internal abstract record SqlStatement;

internal sealed record SelectStatement(
    IReadOnlyList<SelectItem> Items,
    TableRef? From,
    IReadOnlyList<JoinClause> Joins,
    SqlExpr? Where,
    IReadOnlyList<SqlExpr> GroupBy,
    SqlExpr? Having,
    IReadOnlyList<OrderItem> OrderBy,
    SqlExpr? Limit,
    SqlExpr? Offset,
    bool Distinct) : SqlStatement;

internal sealed record InsertStatement(string Table, IReadOnlyList<string>? Columns, IReadOnlyList<IReadOnlyList<SqlExpr>> Rows) : SqlStatement;

internal sealed record UpdateStatement(string Table, IReadOnlyList<(string Column, SqlExpr Value)> Set, SqlExpr? Where) : SqlStatement;

internal sealed record DeleteStatement(string Table, SqlExpr? Where) : SqlStatement;

internal sealed record ColumnDefinition(string Name, TkColumnType Type, bool NotNull, bool PrimaryKey, bool Unique);

internal sealed record CreateTableStatement(string Name, IReadOnlyList<ColumnDefinition> Columns, bool IfNotExists) : SqlStatement;

internal sealed record CreateIndexStatement(string Name, string Table, IReadOnlyList<string> Columns, bool Unique, bool IfNotExists) : SqlStatement;

internal sealed record CreateViewStatement(string Name, SelectStatement Query, string Text, bool OrReplace) : SqlStatement;

/// <summary>DROP TABLE, DROP INDEX (which names its table) or DROP VIEW.</summary>
internal sealed record DropStatement(string Kind, string Name, string? Table, bool IfExists) : SqlStatement;

internal sealed record ExplainStatement(SelectStatement Query) : SqlStatement;

/// <summary>CALL of a procedure the application registered.</summary>
internal sealed record CallStatement(string Name, IReadOnlyList<SqlExpr> Arguments) : SqlStatement;
