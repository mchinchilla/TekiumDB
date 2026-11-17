// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Sql.Parsing;

/// <summary>A statement of the body of a stored procedure, as parsed. Expressions and SQL statements keep their text: it is what the compiled program stores.</summary>
internal abstract record PlStatement;

internal sealed record PlExpression(SqlExpr Tree, string Text);

internal sealed record PlAssign(string Target, PlExpression Value) : PlStatement;

internal sealed record PlIf(IReadOnlyList<(PlExpression Condition, IReadOnlyList<PlStatement> Body)> Branches, IReadOnlyList<PlStatement>? Otherwise) : PlStatement;

internal sealed record PlWhile(PlExpression Condition, IReadOnlyList<PlStatement> Body) : PlStatement;

/// <summary>FOR record IN query LOOP: the body runs once per row, with the columns of the row as record.column.</summary>
internal sealed record PlFor(string Record, SelectStatement Query, string Text, IReadOnlyList<PlStatement> Body) : PlStatement;

internal sealed record PlReturn(PlExpression? Value) : PlStatement;

internal sealed record PlRaise(PlExpression Message) : PlStatement;

internal sealed record PlCall(string Name, IReadOnlyList<PlExpression> Arguments, string? Into) : PlStatement;

/// <summary>An INSERT, UPDATE or DELETE, or a SELECT … INTO variables.</summary>
internal sealed record PlSql(SqlStatement Statement, string Text, IReadOnlyList<string> Into) : PlStatement;

internal sealed record PlVariable(string Name, TkColumnType Type, PlExpression? Initial);

internal sealed record PlBody(IReadOnlyList<PlVariable> Variables, IReadOnlyList<PlStatement> Statements);

/// <content>The language of stored procedures: a deliberately small subset of PL/pgSQL.</content>
internal sealed partial class SqlParser
{
    private static readonly HashSet<string> ProceduralWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "DECLARE", "BEGIN", "END", "THEN", "ELSIF", "ELSE", "LOOP", "WHILE", "FOR", "RETURN", "RAISE", "CALL", "INTO",
    };

    private bool _procedural;
    private List<string>? _into;

    /// <summary>The body of a procedure: DECLARE … BEGIN … END.</summary>
    public static PlBody ParseBody(string text)
    {
        var parser = new SqlParser(text) { _procedural = true };
        var variables = new List<PlVariable>();
        if (parser.Accept("DECLARE"))
        {
            while (!parser.Is("BEGIN"))
            {
                var name = parser.Name("the name of a variable, or BEGIN");
                var type = parser.ColumnType();
                var initial = parser.Accept(":=") ? parser.Value() : null;
                parser.Expect(";");
                variables.Add(new PlVariable(name, type, initial));
            }
        }

        parser.Expect("BEGIN");
        var statements = parser.Statements("END");
        parser.Expect("END");
        parser.Accept(";");
        return parser.Current.Kind == Kind.End
            ? new PlBody(variables, statements)
            : throw parser.Error("the end of the procedure");
    }

    /// <summary>An expression of a procedure, from the text the compiled program keeps.</summary>
    public static SqlExpr ParseProcedural(string text)
    {
        var parser = new SqlParser(text) { _procedural = true };
        var expression = parser.Expression();
        return parser.Current.Kind == Kind.End ? expression : throw parser.Error("the end of the expression");
    }

    /// <summary>A SQL statement of a procedure, from the text the compiled program keeps.</summary>
    public static SqlStatement ParseEmbedded(string text)
    {
        var parser = new SqlParser(text) { _procedural = true };
        var statement = parser.Is("SELECT") ? parser.Select() : parser.Is("INSERT") || parser.Is("UPDATE") || parser.Is("DELETE")
            ? parser.Statement()
            : throw parser.Error("SELECT, INSERT, UPDATE or DELETE");
        return parser.Current.Kind == Kind.End ? statement : throw parser.Error("the end of the statement");
    }

    private CreateProcedureStatement CreateProcedure(bool replace)
    {
        var name = Name("the name of the procedure");
        var parameters = new List<(string Name, TkColumnType Type)>();
        Expect("(");
        if (!Accept(")"))
        {
            do
            {
                var parameter = Name("the name of a parameter");
                parameters.Add((parameter, ColumnType()));
            }
            while (Accept(","));
            Expect(")");
        }

        TkColumnType? returns = Accept("RETURNS") ? ColumnType() : null;
        Expect("AS");
        if (Current.Kind != Kind.Body)
        {
            throw Error("the body of the procedure between $$ and $$");
        }

        return new CreateProcedureStatement(name, parameters, returns, _tokens[_position++].Text, replace);
    }

    private PlExpression Value()
    {
        var start = Current.Position;
        var tree = Expression();
        return new PlExpression(tree, _text[start..Current.Position].Trim());
    }

    private List<PlStatement> Statements(params string[] ends)
    {
        var statements = new List<PlStatement>();
        while (!Array.Exists(ends, Is))
        {
            if (Current.Kind == Kind.End)
            {
                throw Error(string.Join(" or ", ends));
            }

            statements.Add(One());
        }

        return statements;
    }

    private PlStatement One()
    {
        if (Accept("IF"))
        {
            var branches = new List<(PlExpression Condition, IReadOnlyList<PlStatement> Body)>();
            do
            {
                var condition = Value();
                Expect("THEN");
                branches.Add((condition, Statements("ELSIF", "ELSE", "END")));
            }
            while (Accept("ELSIF"));
            var otherwise = Accept("ELSE") ? Statements("END") : null;
            Expect("END");
            Expect("IF");
            Expect(";");
            return new PlIf(branches, otherwise);
        }

        if (Accept("WHILE"))
        {
            var condition = Value();
            Expect("LOOP");
            var body = Statements("END");
            Expect("END");
            Expect("LOOP");
            Expect(";");
            return new PlWhile(condition, body);
        }

        if (Accept("FOR"))
        {
            var record = Name("a name for the row of the loop");
            Expect("IN");
            if (!Is("SELECT"))
            {
                throw Error("SELECT (a loop runs over the rows of a query)");
            }

            var start = Current.Position;
            _into = null;
            var query = Select();
            if (_into is not null)
            {
                throw new TkSqlException("The query of a FOR loop cannot have INTO: its columns are read as record.column.");
            }

            var text = _text[start..Current.Position].Trim();
            Expect("LOOP");
            var body = Statements("END");
            Expect("END");
            Expect("LOOP");
            Expect(";");
            return new PlFor(record, query, text, body);
        }

        if (Accept("RETURN"))
        {
            var value = Is(";") ? null : Value();
            Expect(";");
            return new PlReturn(value);
        }

        if (Accept("RAISE"))
        {
            Accept("EXCEPTION");
            var message = Value();
            Expect(";");
            return new PlRaise(message);
        }

        if (Accept("CALL"))
        {
            var name = Name("the name of a procedure");
            Expect("(");
            var arguments = new List<PlExpression>();
            if (!Accept(")"))
            {
                do
                {
                    arguments.Add(Value());
                }
                while (Accept(","));
                Expect(")");
            }

            var into = Accept("INTO") ? Name("a variable") : null;
            Expect(";");
            return new PlCall(name, arguments, into);
        }

        if (Is("SELECT") || Is("INSERT") || Is("UPDATE") || Is("DELETE"))
        {
            var start = Current.Position;
            _into = null;
            var statement = Is("SELECT") ? Select() : Statement();
            var text = _text[start..Current.Position].Trim();
            if (statement is SelectStatement && _into is null)
            {
                throw new TkSqlException(
                    $"The SELECT at position {start + 1} does nothing with its rows: use SELECT … INTO variables, or FOR row IN SELECT … LOOP.");
            }

            Expect(";");
            return new PlSql(statement, text, _into ?? []);
        }

        if (Is("CREATE") || Is("DROP") || Is("EXPLAIN"))
        {
            throw new TkSqlException($"'{Current.Text}' at position {Current.Position + 1} cannot be used in a procedure: only SELECT, INSERT, UPDATE, DELETE and CALL.");
        }

        if (!IsName())
        {
            throw Error("a statement");
        }

        var target = Name("a variable");
        Expect(":=");
        var assigned = Value();
        Expect(";");
        return new PlAssign(target, assigned);
    }
}
