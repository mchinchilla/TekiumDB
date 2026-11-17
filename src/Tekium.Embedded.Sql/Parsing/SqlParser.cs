// Copyright (c) Tekium. Licensed under the MIT License.

using System.Globalization;
using System.Text;

namespace Tekium.Embedded.Sql.Parsing;

/// <summary>
/// A hand-written recursive descent parser for the SQL this layer runs. One statement per text. An error
/// says what was expected, what was found and where.
/// </summary>
internal sealed partial class SqlParser
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "GROUP", "BY", "HAVING", "ORDER", "LIMIT", "OFFSET", "INNER", "JOIN", "ON", "AS", "AND", "OR", "NOT",
        "IS", "NULL", "IN", "BETWEEN", "LIKE", "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE", "CREATE", "DROP", "TABLE", "INDEX",
        "VIEW", "UNIQUE", "PRIMARY", "KEY", "EXPLAIN", "DISTINCT", "ASC", "DESC", "TRUE", "FALSE", "IF", "EXISTS", "REPLACE", "LEFT", "RIGHT",
        "OUTER", "CROSS", "UNION", "RETURNING", "CASE", "WHEN", "THEN", "ELSE", "END", "ALTER", "CONSTRAINT", "FOREIGN", "REFERENCES",
    };

    private readonly string _text;
    private readonly List<Token> _tokens;
    private int _position;

    private SqlParser(string text)
    {
        _text = text;
        _tokens = Tokenize(text);
    }

    private enum Kind
    {
        Word,
        Quoted,
        Number,
        Text,
        Blob,
        Parameter,
        Body,
        Symbol,
        End,
    }

    private Token Current => _tokens[_position];

    public static SqlStatement Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parser = new SqlParser(text);
        var statement = parser.Statement();
        parser.Accept(";");
        return parser.Current.Kind == Kind.End
            ? statement
            : throw parser.Error("the end of the statement");
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
            }
            else if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }

                // x'…' is a blob, not the word x.
                if (i - start == 1 && c is 'x' or 'X' && i < text.Length && text[i] == '\'')
                {
                    var end = text.IndexOf('\'', i + 1);
                    if (end < 0)
                    {
                        throw new TkSqlException($"The blob that starts at position {start + 1} has no closing quote.");
                    }

                    tokens.Add(new Token(Kind.Blob, text[(i + 1)..end], start));
                    i = end + 1;
                }
                else
                {
                    tokens.Add(new Token(Kind.Word, text[start..i], start));
                }
            }
            else if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                var start = i;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
                {
                    i++;
                }

                if (i < text.Length && text[i] is 'e' or 'E')
                {
                    i++;
                    if (i < text.Length && text[i] is '+' or '-')
                    {
                        i++;
                    }

                    while (i < text.Length && char.IsDigit(text[i]))
                    {
                        i++;
                    }
                }

                tokens.Add(new Token(Kind.Number, text[start..i], start));
            }
            else if (c == '\'')
            {
                var start = i++;
                var value = new StringBuilder();
                while (true)
                {
                    if (i >= text.Length)
                    {
                        throw new TkSqlException($"The text that starts at position {start + 1} has no closing quote.");
                    }

                    if (text[i] == '\'')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '\'')
                        {
                            value.Append('\'');
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    value.Append(text[i++]);
                }

                tokens.Add(new Token(Kind.Text, value.ToString(), start));
            }
            else if (c is '"' or '`' or '[')
            {
                var close = c == '[' ? ']' : c;
                var end = text.IndexOf(close, i + 1);
                if (end < 0)
                {
                    throw new TkSqlException($"The quoted name that starts at position {i + 1} has no closing {close}.");
                }

                tokens.Add(new Token(Kind.Quoted, text[(i + 1)..end], i));
                i = end + 1;
            }
            else if (c == '$' && i + 1 < text.Length && text[i + 1] == '$')
            {
                // $$ … $$: the body of a procedure, kept as it is.
                var end = text.IndexOf("$$", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    throw new TkSqlException($"The body that starts at position {i + 1} has no closing $$.");
                }

                tokens.Add(new Token(Kind.Body, text[(i + 2)..end], i));
                i = end + 2;
            }
            else if (c == ':' && i + 1 < text.Length && text[i + 1] == '=')
            {
                tokens.Add(new Token(Kind.Symbol, ":=", i));
                i += 2;
            }
            else if (c is '@' or ':' or '$')
            {
                var start = i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }

                if (i == start + 1)
                {
                    throw new TkSqlException($"A parameter needs a name after '{c}', at position {start + 1}.");
                }

                tokens.Add(new Token(Kind.Parameter, text[(start + 1)..i], start));
            }
            else
            {
                var two = i + 1 < text.Length ? text.Substring(i, 2) : string.Empty;
                if (two == "->")
                {
                    var arrow = i + 2 < text.Length && text[i + 2] == '>' ? "->>" : "->";
                    tokens.Add(new Token(Kind.Symbol, arrow, i));
                    i += arrow.Length;
                }
                else if (two is "<>" or "!=" or "<=" or ">=" or "||")
                {
                    tokens.Add(new Token(Kind.Symbol, two == "!=" ? "<>" : two, i));
                    i += 2;
                }
                else if ("=<>+-*/%(),.;".Contains(c, StringComparison.Ordinal))
                {
                    tokens.Add(new Token(Kind.Symbol, c.ToString(), i));
                    i++;
                }
                else
                {
                    throw new TkSqlException($"Unexpected character '{c}' at position {i + 1}.");
                }
            }
        }

        tokens.Add(new Token(Kind.End, string.Empty, text.Length));
        return tokens;
    }

    private SqlStatement Statement()
    {
        if (Accept("EXPLAIN"))
        {
            return new ExplainStatement(Select());
        }

        if (Is("SELECT"))
        {
            return Select();
        }

        if (Accept("INSERT"))
        {
            return Insert();
        }

        if (Accept("UPDATE"))
        {
            return Update();
        }

        if (Accept("DELETE"))
        {
            Expect("FROM");
            var table = Name("a table name");
            var alias = Accept("AS") ? Name("an alias") : IsName() ? Name("an alias") : null;
            return new DeleteStatement(table, Accept("WHERE") ? Expression() : null, Returning(), alias);
        }

        if (Accept("CREATE"))
        {
            return Create();
        }

        if (Accept("DROP"))
        {
            return Drop();
        }

        if (Accept("ALTER"))
        {
            return Alter();
        }

        if (Accept("CALL"))
        {
            var name = Name("the name of a procedure");
            Expect("(");
            var arguments = new List<SqlExpr>();
            if (!Accept(")"))
            {
                do
                {
                    arguments.Add(Expression());
                }
                while (Accept(","));
                Expect(")");
            }

            return new CallStatement(name, arguments);
        }

        throw Error("SELECT, INSERT, UPDATE, DELETE, CREATE, DROP, ALTER, CALL or EXPLAIN");
    }

    private SelectStatement Select()
    {
        Expect("SELECT");
        var distinct = Accept("DISTINCT");
        var items = new List<SelectItem>();
        do
        {
            if (Accept("*"))
            {
                items.Add(new SelectItem(null, null, true, null));
            }
            else if (Current.Kind is Kind.Word or Kind.Quoted && Peek(1) == "." && Peek(2) == "*")
            {
                var of = Name("a table name");
                _position += 2;
                items.Add(new SelectItem(null, null, true, of));
            }
            else
            {
                var expression = Expression();
                var alias = Accept("AS") ? Name("a name for the column") : IsName() ? Name("a name") : null;
                items.Add(new SelectItem(expression, alias, false, null));
            }
        }
        while (Accept(","));

        // SELECT … INTO variables: only inside a procedure.
        if (_procedural && Accept("INTO"))
        {
            var into = new List<string>();
            do
            {
                into.Add(Name("a variable"));
            }
            while (Accept(","));
            _into = into;
        }

        TableRef? from = null;
        var joins = new List<JoinClause>();
        if (Accept("FROM"))
        {
            from = Table();
            while (true)
            {
                if (Is("RIGHT") || Is("CROSS") || Is("OUTER"))
                {
                    throw Error("INNER JOIN or LEFT JOIN (right, full and cross joins are not supported)");
                }

                var left = Accept("LEFT");
                if (left)
                {
                    Accept("OUTER");
                    Expect("JOIN");
                }
                else if (Accept("INNER"))
                {
                    Expect("JOIN");
                }
                else if (!Accept("JOIN"))
                {
                    break;
                }

                var joined = Table();
                Expect("ON");
                joins.Add(new JoinClause(joined, Expression(), left));
            }

            if (Is(","))
            {
                throw Error("JOIN … ON (tables separated by commas are not supported)");
            }
        }

        var where = Accept("WHERE") ? Expression() : null;
        var groupBy = new List<SqlExpr>();
        if (Accept("GROUP"))
        {
            Expect("BY");
            do
            {
                groupBy.Add(Expression());
            }
            while (Accept(","));
        }

        var having = Accept("HAVING") ? Expression() : null;
        var orderBy = new List<OrderItem>();
        if (Accept("ORDER"))
        {
            Expect("BY");
            do
            {
                var expression = Expression();
                var descending = Accept("DESC");
                if (!descending)
                {
                    Accept("ASC");
                }

                orderBy.Add(new OrderItem(expression, descending));
            }
            while (Accept(","));
        }

        var limit = Accept("LIMIT") ? Expression() : null;
        var offset = Accept("OFFSET") ? Expression() : null;
        if (Is("UNION"))
        {
            throw Error("the end of the query (UNION is not supported)");
        }

        return new SelectStatement(items, from, joins, where, groupBy, having, orderBy, limit, offset, distinct);
    }

    private TableRef Table()
    {
        if (Accept("("))
        {
            // A query in the place of a table: it needs a name to be referred to by.
            var query = Select();
            Expect(")");
            Accept("AS");
            var named = Name("an alias for the query in parentheses");
            return new TableRef(named, named, query);
        }

        var name = Name("a table or view name");
        var alias = Accept("AS") ? Name("an alias") : IsName() ? Name("an alias") : name;
        return new TableRef(name, alias);
    }

    private InsertStatement Insert()
    {
        Expect("INTO");
        var table = Name("a table name");
        List<string>? columns = null;
        if (Accept("("))
        {
            columns = [];
            do
            {
                columns.Add(Name("a column name"));
            }
            while (Accept(","));
            Expect(")");
        }

        Expect("VALUES");
        var rows = new List<IReadOnlyList<SqlExpr>>();
        do
        {
            Expect("(");
            var row = new List<SqlExpr>();
            do
            {
                row.Add(Expression());
            }
            while (Accept(","));
            Expect(")");
            rows.Add(row);
        }
        while (Accept(","));
        return new InsertStatement(table, columns, rows, Returning());
    }

    private UpdateStatement Update()
    {
        var table = Name("a table name");
        var alias = Accept("AS") ? Name("an alias") : IsName() ? Name("an alias") : null;
        Expect("SET");
        var set = new List<(string, SqlExpr)>();
        do
        {
            var column = Name("a column name");
            Expect("=");
            set.Add((column, Expression()));
        }
        while (Accept(","));
        return new UpdateStatement(table, set, Accept("WHERE") ? Expression() : null, Returning(), alias);
    }

    /// <summary>RETURNING: what to give back of each row an INSERT, UPDATE or DELETE writes.</summary>
    private List<SelectItem>? Returning()
    {
        if (!Accept("RETURNING"))
        {
            return null;
        }

        var items = new List<SelectItem>();
        do
        {
            if (Accept("*"))
            {
                items.Add(new SelectItem(null, null, true, null));
            }
            else
            {
                var expression = Expression();
                items.Add(new SelectItem(expression, Accept("AS") ? Name("a name for the column") : IsName() ? Name("a name") : null, false, null));
            }
        }
        while (Accept(","));
        return items;
    }

    private SqlStatement Create()
    {
        var replace = false;
        if (Accept("OR"))
        {
            Expect("REPLACE");
            replace = true;
        }

        if (Accept("VIEW"))
        {
            var name = Name("a view name");
            Expect("AS");
            var start = Current.Position;
            var query = Select();
            return new CreateViewStatement(name, query, _text[start..Current.Position].Trim(), replace);
        }

        if (Accept("PROCEDURE"))
        {
            return CreateProcedure(replace);
        }

        if (replace)
        {
            throw Error("VIEW or PROCEDURE (only a view or a procedure can be replaced)");
        }

        var unique = Accept("UNIQUE");
        if (Accept("INDEX"))
        {
            var ifNotExists = IfNotExists();
            var name = Name("an index name");
            Expect("ON");
            var table = Name("a table name");
            Expect("(");
            var columns = new List<string>();
            var paths = new List<(string Path, TkColumnType Type)?>();
            do
            {
                // A column, or the value at a path of a JSON column: JSON_VALUE(column, 'path') or column ->> 'path', AS TEXT or DOUBLE.
                (string Path, TkColumnType Type)? path = null;
                string column;
                if (Is("JSON_VALUE") && Peek(1) == "(")
                {
                    _position += 2;
                    column = Name("a column name");
                    Expect(",");
                    path = (PathText(), TkColumnType.Text);
                    Expect(")");
                }
                else if (IsName() && Peek(1) is "->>")
                {
                    column = Name("a column name");
                    _position++;
                    path = (PathText(), TkColumnType.Text);
                }
                else if (IsName() && Peek(1) is "," or ")")
                {
                    column = Name("a column name");
                }
                else
                {
                    throw Error("a column or JSON_VALUE(column, 'path')");
                }

                if (path is { } found && Accept("AS"))
                {
                    path = (found.Path, ColumnType());
                }

                columns.Add(column);
                paths.Add(path);
            }
            while (Accept(","));
            Expect(")");
            return new CreateIndexStatement(name, table, columns, unique, ifNotExists) { Paths = paths.Exists(static path => path is not null) ? paths : null };
        }

        if (unique)
        {
            throw Error("INDEX");
        }

        Expect("TABLE");
        var exists = IfNotExists();
        var tableName = Name("a table name");
        Expect("(");
        var definitions = new List<ColumnDefinition>();
        var foreignKeys = new List<ForeignKeyDefinition>();
        string? keyColumn = null;
        do
        {
            if (Is("CONSTRAINT") || Is("FOREIGN"))
            {
                foreignKeys.Add(ForeignKey());
                continue;
            }

            if (Accept("PRIMARY"))
            {
                Expect("KEY");
                Expect("(");
                keyColumn = Name("a column name");
                if (Is(","))
                {
                    throw Error(") (a primary key is one column)");
                }

                Expect(")");
                continue;
            }

            var (definition, reference, _) = Column(false);
            definitions.Add(definition);
            if (reference is not null)
            {
                foreignKeys.Add(reference);
            }
        }
        while (Accept(","));
        Expect(")");
        if (keyColumn is not null)
        {
            var at = definitions.FindIndex(definition => string.Equals(definition.Name, keyColumn, StringComparison.OrdinalIgnoreCase));
            if (at < 0)
            {
                throw new TkSqlException($"PRIMARY KEY names column '{keyColumn}', which table '{tableName}' does not declare.");
            }

            definitions[at] = definitions[at] with { PrimaryKey = true, NotNull = true };
        }

        return new CreateTableStatement(tableName, definitions, exists, foreignKeys);
    }

    /// <summary>A column as CREATE TABLE and ALTER TABLE declare it: name, type, and what follows in any order.</summary>
    private (ColumnDefinition Column, ForeignKeyDefinition? Reference, (bool Given, object? Value) Default) Column(bool allowDefault)
    {
        var column = Name("a column name");
        var type = ColumnType();
        var notNull = false;
        var primary = false;
        var isUnique = false;
        ForeignKeyDefinition? reference = null;
        (bool Given, object? Value) byDefault = default;
        while (true)
        {
            if (Accept("NOT"))
            {
                Expect("NULL");
                notNull = true;
            }
            else if (Accept("NULL"))
            {
                notNull = false;
            }
            else if (Accept("PRIMARY"))
            {
                Expect("KEY");
                primary = true;
            }
            else if (Accept("UNIQUE"))
            {
                isUnique = true;
            }
            else if (Accept("REFERENCES"))
            {
                reference = References(null, column);
            }
            else if (allowDefault && Is("DEFAULT"))
            {
                _position++;
                byDefault = (true, WrittenValue());
            }
            else
            {
                break;
            }
        }

        return (new ColumnDefinition(column, type, notNull || primary, primary, isUnique), reference, byDefault);
    }

    /// <summary>A value written out: a number with its sign, a text, TRUE, FALSE or NULL.</summary>
    private object? WrittenValue()
    {
        var negative = Accept("-");
        if (Primary() is Literal literal && !(negative && literal.Value is not (long or decimal or double)))
        {
            return negative ? Negate(literal.Value!) : literal.Value;
        }

        throw new TkSqlException("Expected a value written out (a number, a text, TRUE, FALSE or NULL) as the DEFAULT of a column: it cannot be worked out from anything else.");
    }

    /// <summary><c>[CONSTRAINT name] FOREIGN KEY (column) REFERENCES table [(column)]</c>.</summary>
    private ForeignKeyDefinition ForeignKey()
    {
        var name = Accept("CONSTRAINT") ? Name("a constraint name") : null;
        Expect("FOREIGN");
        Expect("KEY");
        Expect("(");
        var column = Name("a column name");
        if (Is(","))
        {
            throw Error(") (a foreign key is one column)");
        }

        Expect(")");
        Expect("REFERENCES");
        return References(name, column);
    }

    private ForeignKeyDefinition References(string? name, string column)
    {
        var table = Name("a table name");
        string? target = null;
        if (Accept("("))
        {
            target = Name("a column name");
            Expect(")");
        }

        // A foreign key here does one thing: it refuses. Saying so is accepted; asking for anything else is not.
        while (Accept("ON"))
        {
            var when = Accept("DELETE") ? "DELETE" : Accept("UPDATE") ? "UPDATE" : throw Error("DELETE or UPDATE");
            if (Accept("RESTRICT"))
            {
                continue;
            }

            if (Accept("NO"))
            {
                if (!Accept("ACTION"))
                {
                    throw Error("ACTION");
                }

                continue;
            }

            var action = Accept("CASCADE") ? "CASCADE" : Accept("SET") ? (Accept("NULL") ? "SET NULL" : Accept("DEFAULT") ? "SET DEFAULT" : throw Error("NULL or DEFAULT"))
                : throw Error("RESTRICT, NO ACTION, CASCADE or SET");
            throw new TkSqlException(
                $"ON {when} {action} is not supported: a FOREIGN KEY only refuses to leave a row referring to one that is not there. Delete or change the referring rows first.");
        }

        return new ForeignKeyDefinition(name, column, table, target);
    }

    private AlterTableStatement Alter()
    {
        if (Accept("INDEX"))
        {
            var index = Name("an index name");
            Expect("ON");
            var on = Name("the table of the index");
            ExpectWord("RENAME");
            ExpectWord("TO");
            return new AlterTableStatement(on, new RenameIndexAction(index, Name("a new name for the index")));
        }

        Expect("TABLE");
        var table = Name("a table name");
        if (Accept("ADD"))
        {
            if (Is("CONSTRAINT") || Is("FOREIGN"))
            {
                return new AlterTableStatement(table, new AddForeignKeyAction(ForeignKey()));
            }

            if (Accept("PRIMARY"))
            {
                Expect("KEY");
                Expect("(");
                var key = Name("a column name");
                if (Is(","))
                {
                    throw Error(") (a primary key is one column)");
                }

                Expect(")");
                return new AlterTableStatement(table, new AddPrimaryKeyAction(key));
            }

            SkipColumnWord();
            var (column, reference, byDefault) = Column(true);
            if (reference is not null)
            {
                throw new TkSqlException($"Column '{column.Name}' cannot be added with REFERENCES: add the column, then the constraint with ALTER TABLE {table} ADD FOREIGN KEY.");
            }

            return new AlterTableStatement(table, new AddColumnAction(column, byDefault.Given, byDefault.Value));
        }

        if (Accept("DROP"))
        {
            if (Accept("CONSTRAINT"))
            {
                var ifExists = false;
                if (Accept("IF"))
                {
                    Expect("EXISTS");
                    ifExists = true;
                }

                return new AlterTableStatement(table, new DropConstraintAction(Name("a constraint name"), ifExists));
            }

            SkipColumnWord();
            return new AlterTableStatement(table, new DropColumnAction(Name("a column name")));
        }

        if (AcceptWord("RENAME"))
        {
            if (AcceptWord("TO"))
            {
                return new AlterTableStatement(table, new RenameTableAction(Name("a new name for the table")));
            }

            SkipColumnWord();
            var column = Name("a column name");
            ExpectWord("TO");
            return new AlterTableStatement(table, new RenameColumnAction(column, Name("a new name for the column")));
        }

        if (AcceptWord("ALTER"))
        {
            SkipColumnWord();
            var name = Name("a column name");

            // TYPE is optional, and may also be the name of a type no column has.
            if (Is("TYPE") && _position + 1 < _tokens.Count && _tokens[_position + 1].Kind is Kind.Word or Kind.Quoted)
            {
                _position++;
            }

            var type = ColumnType();
            bool? notNull = null;
            (bool Given, object? Value) byDefault = default;
            while (true)
            {
                if (Accept("NOT"))
                {
                    Expect("NULL");
                    notNull = true;
                }
                else if (Accept("NULL"))
                {
                    notNull = false;
                }
                else if (Is("DEFAULT"))
                {
                    _position++;
                    byDefault = (true, WrittenValue());
                }
                else
                {
                    break;
                }
            }

            return new AlterTableStatement(table, new AlterColumnAction(name, type, notNull, byDefault.Given, byDefault.Value));
        }

        throw Error("ADD, DROP, RENAME or ALTER");
    }

    /// <summary>A path written as text in a statement: it must start with $, and is kept as written for the engine to read.</summary>
    private string PathText()
    {
        var token = Current;
        if (token.Kind != Kind.Text)
        {
            throw Error("a JSON path in quotes");
        }

        _position++;
        return token.Text;
    }

    /// <summary>COLUMN is optional before a column name, and may also be the name of the column.</summary>
    private void SkipColumnWord()
    {
        if (Is("COLUMN") && _position + 1 < _tokens.Count && _tokens[_position + 1].Kind is Kind.Word or Kind.Quoted
            && !(_tokens[_position + 1].Kind == Kind.Word && Reserved.Contains(_tokens[_position + 1].Text)))
        {
            _position++;
        }
    }

    /// <summary>A word that is not reserved, so that it can also be a name elsewhere.</summary>
    private bool AcceptWord(string word)
    {
        if (Current.Kind == Kind.Word && string.Equals(Current.Text, word, StringComparison.OrdinalIgnoreCase))
        {
            _position++;
            return true;
        }

        return false;
    }

    private void ExpectWord(string word)
    {
        if (!AcceptWord(word))
        {
            throw Error(word);
        }
    }

    /// <summary>The names a text of SQL uses: every word and quoted name, in order. Used to find what a view or procedure refers to.</summary>
    public static IEnumerable<string> Names(string text)
    {
        List<Token> tokens;
        try
        {
            tokens = Tokenize(text);
        }
        catch (TkSqlException)
        {
            yield break;
        }

        foreach (var token in tokens)
        {
            if (token.Kind is Kind.Word or Kind.Quoted)
            {
                yield return token.Text;
            }
        }
    }

    private DropStatement Drop()
    {
        var kind = Accept("TABLE") ? "TABLE" : Accept("INDEX") ? "INDEX" : Accept("VIEW") ? "VIEW" : Accept("PROCEDURE") ? "PROCEDURE"
            : throw Error("TABLE, INDEX, VIEW or PROCEDURE");
        var ifExists = false;
        if (Accept("IF"))
        {
            Expect("EXISTS");
            ifExists = true;
        }

        var name = Name("a name");
        string? table = null;
        if (kind == "INDEX")
        {
            Expect("ON");
            table = Name("the table of the index");
        }

        return new DropStatement(kind, name, table, ifExists);
    }

    private bool IfNotExists()
    {
        if (!Accept("IF"))
        {
            return false;
        }

        Expect("NOT");
        Expect("EXISTS");
        return true;
    }

    private TkColumnType ColumnType()
    {
        var token = Current;
        var name = Name("a column type").ToUpperInvariant();
        var type = name switch
        {
            "BOOLEAN" or "BOOL" or "BIT" => TkColumnType.Boolean,
            "TINYINT" => TkColumnType.U8,
            "SMALLINT" => TkColumnType.I16,
            "INT" or "INTEGER" => TkColumnType.I32,
            "BIGINT" => TkColumnType.I64,
            "REAL" => TkColumnType.F32,
            "DOUBLE" or "FLOAT" => TkColumnType.F64,
            "DECIMAL" or "NUMERIC" or "MONEY" => TkColumnType.Numeric,
            "TEXT" or "VARCHAR" or "NVARCHAR" or "CHAR" or "NCHAR" or "STRING" => TkColumnType.Text,
            "UUID" or "GUID" or "UNIQUEIDENTIFIER" => TkColumnType.Uuid,
            "TIMESTAMP" or "TIMESTAMPTZ" or "DATETIMEOFFSET" => TkColumnType.DateTimeOffset,
            "DATETIME" => TkColumnType.DateTime,
            "DATE" => TkColumnType.DateOnly,
            "TIME" => TkColumnType.TimeOnly,
            "INTERVAL" => TkColumnType.TimeSpan,
            "BLOB" or "BYTEA" or "VARBINARY" or "BINARY" => TkColumnType.Bytes,
            "JSON" => TkColumnType.Json,
            "JSONB" => TkColumnType.Jsonb,
            _ => throw new TkSqlException($"Unknown column type '{token.Text}' at position {token.Position + 1}."),
        };
        if (name == "DOUBLE" && Is("PRECISION"))
        {
            _position++;
        }

        // A length or precision is accepted and ignored: VARCHAR(50), DECIMAL(10, 2).
        if (Accept("("))
        {
            while (!Accept(")"))
            {
                if (Current.Kind == Kind.End)
                {
                    throw Error(")");
                }

                _position++;
            }
        }

        return type;
    }

    private SqlExpr Expression() => Or();

    private SqlExpr Or()
    {
        var left = And();
        while (Accept("OR"))
        {
            left = new Binary("OR", left, And());
        }

        return left;
    }

    private SqlExpr And()
    {
        var left = Not();
        while (Accept("AND"))
        {
            left = new Binary("AND", left, Not());
        }

        return left;
    }

    private SqlExpr Not() => Accept("NOT") ? new Unary("NOT", Not()) : Comparison();

    private SqlExpr Comparison()
    {
        var left = Additive();
        while (true)
        {
            if (Current.Kind == Kind.Symbol && Current.Text is "=" or "<>" or "<" or "<=" or ">" or ">=")
            {
                var op = Current.Text;
                _position++;
                left = new Binary(op, left, Additive());
            }
            else if (Accept("IS"))
            {
                var negated = Accept("NOT");
                Expect("NULL");
                left = new IsNull(left, negated);
            }
            else if (Is("NOT") && Peek(1).ToUpperInvariant() is "IN" or "BETWEEN" or "LIKE")
            {
                _position++;
                left = Postfix(left, true);
            }
            else if (Is("IN") || Is("BETWEEN") || Is("LIKE"))
            {
                left = Postfix(left, false);
            }
            else
            {
                return left;
            }
        }
    }

    private SqlExpr Postfix(SqlExpr left, bool negated)
    {
        if (Accept("IN"))
        {
            Expect("(");
            if (Is("SELECT"))
            {
                var query = Select();
                Expect(")");
                return new InQuery(left, query, negated);
            }

            var items = new List<SqlExpr>();
            do
            {
                items.Add(Expression());
            }
            while (Accept(","));
            Expect(")");
            return new InList(left, items, negated);
        }

        if (Accept("BETWEEN"))
        {
            var low = Additive();
            Expect("AND");
            return new Between(left, low, Additive(), negated);
        }

        Expect("LIKE");
        return new Like(left, Additive(), negated);
    }

    private SqlExpr Additive()
    {
        var left = Multiplicative();
        while (Current.Kind == Kind.Symbol && Current.Text is "+" or "-" or "||")
        {
            var op = Current.Text;
            _position++;
            left = new Binary(op, left, Multiplicative());
        }

        return left;
    }

    private SqlExpr Multiplicative()
    {
        var left = JsonAccess();
        while (Current.Kind == Kind.Symbol && Current.Text is "*" or "/" or "%")
        {
            var op = Current.Text;
            _position++;
            left = new Binary(op, left, JsonAccess());
        }

        return left;
    }

    /// <summary><c>doc -&gt; path</c> and <c>doc -&gt;&gt; path</c>, which bind tighter than arithmetic and read left to right.</summary>
    private SqlExpr JsonAccess()
    {
        var left = Primary();
        while (Current.Kind == Kind.Symbol && Current.Text is "->" or "->>")
        {
            var op = Current.Text;
            _position++;
            left = new Binary(op, left, Primary());
        }

        return left;
    }

    private SqlExpr Primary()
    {
        var token = Current;
        switch (token.Kind)
        {
            case Kind.Number:
                _position++;
                return new Literal(Number(token));

            case Kind.Text:
                _position++;
                return new Literal(token.Text);

            case Kind.Blob:
                _position++;
                try
                {
                    return new Literal(Convert.FromHexString(token.Text));
                }
                catch (FormatException)
                {
                    throw new TkSqlException($"The blob at position {token.Position + 1} is not hexadecimal.");
                }

            case Kind.Parameter when _procedural:
                throw new TkSqlException($"A procedure has no parameters like @{token.Text}: it reads its own variables by name (position {token.Position + 1}).");

            case Kind.Parameter:
                _position++;
                return new Parameter(token.Text);

            case Kind.Symbol when token.Text == "(":
                _position++;
                if (Is("SELECT"))
                {
                    var scalar = Select();
                    Expect(")");
                    return new Subquery(scalar, false);
                }

                var inner = Expression();
                Expect(")");
                return inner;

            case Kind.Symbol when token.Text == "-":
                _position++;
                var operand = Primary();
                return operand is Literal { Value: { } number } && Storage.SqlValues.IsNumber(number) ? new Literal(Negate(number)) : new Unary("-", operand);

            case Kind.Word when Is("EXISTS"):
                _position++;
                Expect("(");
                var exists = Select();
                Expect(")");
                return new Subquery(exists, true);

            case Kind.Word when Is("CASE"):
                _position++;
                var subject = Is("WHEN") ? null : Expression();
                var branches = new List<(SqlExpr When, SqlExpr Then)>();
                while (Accept("WHEN"))
                {
                    var when = Expression();
                    Expect("THEN");
                    branches.Add((subject is null ? when : new Binary("=", subject, when), Expression()));
                }

                if (branches.Count == 0)
                {
                    throw Error("WHEN");
                }

                var otherwise = Accept("ELSE") ? Expression() : null;
                Expect("END");
                return new Case(branches, otherwise);

            case Kind.Word when Is("NULL"):
                _position++;
                return new Literal(null);

            case Kind.Word when Is("TRUE") || Is("FALSE"):
                _position++;
                return new Literal(token.Text.Equals("TRUE", StringComparison.OrdinalIgnoreCase));

            case Kind.Word or Kind.Quoted:
                if (token.Kind == Kind.Word && Reserved.Contains(token.Text))
                {
                    throw Error("an expression");
                }

                _position++;

                // A function may be named in quotes, as Entity Framework writes the functions an application maps.
                if (Accept("("))
                {
                    if (token.Text.Equals("CAST", StringComparison.OrdinalIgnoreCase))
                    {
                        // CAST(value AS type): a function of the value and the name of the type.
                        var value = Expression();
                        Expect("AS");
                        var type = ColumnType();
                        Expect(")");
                        return new Call("CAST", [value, new Literal(type.ToString())], false, false);
                    }

                    return FunctionCall(token.Text);
                }

                return Accept(".") ? new ColumnRef(token.Text, Name("a column name")) : new ColumnRef(null, token.Text);

            default:
                throw Error("an expression");
        }
    }

    private Call FunctionCall(string name)
    {
        var upper = name.ToUpperInvariant();
        if (Accept("*"))
        {
            Expect(")");
            return new Call(upper, [], true, false);
        }

        var distinct = Accept("DISTINCT");
        var arguments = new List<SqlExpr>();
        if (!Accept(")"))
        {
            do
            {
                arguments.Add(Expression());
            }
            while (Accept(","));
            Expect(")");
        }

        return new Call(upper, arguments, false, distinct);
    }

    private static object Number(Token token)
    {
        var text = token.Text;
        if (text.Contains('e', StringComparison.OrdinalIgnoreCase)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating))
        {
            return floating;
        }

        if (!text.Contains('.', StringComparison.Ordinal) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var whole))
        {
            return whole;
        }

        return decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var exact)
            ? exact
            : throw new TkSqlException($"'{text}' at position {token.Position + 1} is not a number.");
    }

    private static object Negate(object number) => number switch
    {
        long whole => -whole,
        decimal exact => -exact,
        double floating => -floating,
        _ => number,
    };

    private bool Is(string word) =>
        (Current.Kind == Kind.Word || Current.Kind == Kind.Symbol) && string.Equals(Current.Text, word, StringComparison.OrdinalIgnoreCase);

    private bool IsName() => Current.Kind == Kind.Quoted
        || (Current.Kind == Kind.Word && !Reserved.Contains(Current.Text) && !(_procedural && ProceduralWords.Contains(Current.Text)));

    private string Peek(int ahead) =>
        _position + ahead < _tokens.Count && _tokens[_position + ahead].Kind is Kind.Word or Kind.Symbol ? _tokens[_position + ahead].Text : string.Empty;

    private bool Accept(string word)
    {
        if (!Is(word))
        {
            return false;
        }

        _position++;
        return true;
    }

    private void Expect(string word)
    {
        if (!Accept(word))
        {
            throw Error(word);
        }
    }

    private string Name(string what)
    {
        if (!IsName())
        {
            throw Error(what);
        }

        return _tokens[_position++].Text;
    }

    private TkSqlException Error(string expected)
    {
        var found = Current.Kind == Kind.End ? "the end of the statement" : $"'{Current.Text}'";
        return new TkSqlException($"Expected {expected} but found {found}, at position {Current.Position + 1}.");
    }

    private readonly record struct Token(Kind Kind, string Text, int Position);
}
