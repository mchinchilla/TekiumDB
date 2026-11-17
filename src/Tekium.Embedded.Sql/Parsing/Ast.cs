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

/// <summary>A query used as a value: <c>EXISTS (SELECT …)</c>, or <c>(SELECT …)</c> giving one value.</summary>
internal sealed record Subquery(SelectStatement Query, bool Exists) : SqlExpr;

/// <summary><c>x IN (SELECT …)</c>.</summary>
internal sealed record InQuery(SqlExpr Operand, SelectStatement Query, bool Negated) : SqlExpr;

/// <summary><c>CASE WHEN … THEN … ELSE … END</c>.</summary>
internal sealed record Case(IReadOnlyList<(SqlExpr When, SqlExpr Then)> Branches, SqlExpr? Otherwise) : SqlExpr;

/// <summary>A function: an aggregate (COUNT, SUM, MIN, MAX, AVG) or a scalar one.</summary>
internal sealed record Call(string Name, IReadOnlyList<SqlExpr> Arguments, bool Star, bool Distinct) : SqlExpr;

/// <summary>One thing a SELECT returns: an expression, <c>*</c>, or <c>table.*</c>.</summary>
internal sealed record SelectItem(SqlExpr? Expression, string? Alias, bool Star, string? StarOf);

/// <summary>A table or view by name, or a query in parentheses (then <c>Name</c> is its alias).</summary>
internal sealed record TableRef(string Name, string Alias, SelectStatement? Query = null);

/// <summary>INNER JOIN, or LEFT JOIN: every row of what came before stays, with NULLs where nothing matches.</summary>
internal sealed record JoinClause(TableRef Table, SqlExpr On, bool Left = false);

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

/// <summary>INSERT. <c>Returning</c>, here and in UPDATE and DELETE, is what to give back of each row written.</summary>
internal sealed record InsertStatement(
    string Table, IReadOnlyList<string>? Columns, IReadOnlyList<IReadOnlyList<SqlExpr>> Rows, IReadOnlyList<SelectItem>? Returning = null) : SqlStatement;

internal sealed record UpdateStatement(
    string Table, IReadOnlyList<(string Column, SqlExpr Value)> Set, SqlExpr? Where, IReadOnlyList<SelectItem>? Returning = null, string? Alias = null) : SqlStatement;

internal sealed record DeleteStatement(string Table, SqlExpr? Where, IReadOnlyList<SelectItem>? Returning = null, string? Alias = null) : SqlStatement;

internal sealed record ColumnDefinition(string Name, TkColumnType Type, bool NotNull, bool PrimaryKey, bool Unique);

/// <summary>A FOREIGN KEY as it is declared: a column that refers to the primary key of a table. Without a name it is <c>fk_</c> and the column.</summary>
internal sealed record ForeignKeyDefinition(string? Name, string Column, string Table, string? TargetColumn);

internal sealed record CreateTableStatement(
    string Name, IReadOnlyList<ColumnDefinition> Columns, bool IfNotExists, IReadOnlyList<ForeignKeyDefinition> ForeignKeys) : SqlStatement;

/// <summary>ALTER TABLE (or ALTER INDEX, which names its table too): one change to one table.</summary>
internal sealed record AlterTableStatement(string Table, AlterAction Action) : SqlStatement;

internal abstract record AlterAction;

/// <summary>A column to add, with the value the rows there are take (which may be NULL) if one is given.</summary>
internal sealed record AddColumnAction(ColumnDefinition Column, bool HasDefault, object? Default) : AlterAction;

internal sealed record AddForeignKeyAction(ForeignKeyDefinition ForeignKey) : AlterAction;

internal sealed record DropConstraintAction(string Name, bool IfExists) : AlterAction;

/// <summary>ADD PRIMARY KEY (column): accepted only when the column already is the key, which never changes.</summary>
internal sealed record AddPrimaryKeyAction(string Column) : AlterAction;

internal sealed record DropColumnAction(string Name) : AlterAction;

internal sealed record RenameColumnAction(string Name, string NewName) : AlterAction;

internal sealed record RenameTableAction(string NewName) : AlterAction;

internal sealed record RenameIndexAction(string Name, string NewName) : AlterAction;

/// <summary>
/// A new type for a column, and whether it can be NULL (null keeps what it was); the rows that have NULL
/// in it take <c>Default</c> if one is given.
/// </summary>
internal sealed record AlterColumnAction(string Name, TkColumnType Type, bool? NotNull, bool HasDefault, object? Default) : AlterAction;

/// <summary>CREATE INDEX. A field with a path is the value at that path of a JSON column, as TEXT or DOUBLE.</summary>
internal sealed record CreateIndexStatement(string Name, string Table, IReadOnlyList<string> Columns, bool Unique, bool IfNotExists) : SqlStatement
{
    /// <summary>Gets, for each column, the path and kind of value indexed; null for a column indexed as it is.</summary>
    public IReadOnlyList<(string Path, TkColumnType Type)?>? Paths { get; init; }
}

internal sealed record CreateViewStatement(string Name, SelectStatement Query, string Text, bool OrReplace) : SqlStatement;

/// <summary>CREATE PROCEDURE: its name, parameters, what it returns if anything, and its body, still as text.</summary>
internal sealed record CreateProcedureStatement(
    string Name, IReadOnlyList<(string Name, TkColumnType Type)> Parameters, TkColumnType? Returns, string Body, bool OrReplace) : SqlStatement;

/// <summary>DROP TABLE, DROP INDEX (which names its table), DROP VIEW or DROP PROCEDURE.</summary>
internal sealed record DropStatement(string Kind, string Name, string? Table, bool IfExists) : SqlStatement;

internal sealed record ExplainStatement(SelectStatement Query) : SqlStatement;

/// <summary>CALL of a procedure the application registered.</summary>
internal sealed record CallStatement(string Name, IReadOnlyList<SqlExpr> Arguments) : SqlStatement;
