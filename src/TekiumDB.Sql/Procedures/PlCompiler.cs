// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using TekiumDB.Sql.Execution;
using TekiumDB.Sql.Parsing;

namespace TekiumDB.Sql.Procedures;

/// <summary>
/// Turns the body of a procedure into a program: its control flow becomes jumps between instructions,
/// each variable a register, and each expression and SQL statement a constant the instructions name.
/// Everything that can be wrong without looking at the tables is an error here, when the procedure is
/// created.
/// </summary>
internal sealed class PlCompiler
{
    private readonly List<string> _names = [];
    private readonly List<byte> _types = [];
    private readonly Dictionary<string, int> _variables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Row> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(PlConstant Kind, string Text)> _constants = [];
    private readonly Dictionary<(PlConstant Kind, string Text), int> _known = [];
    private readonly List<byte> _code = [];
    private readonly bool _returns;

    private PlCompiler(bool returns) => _returns = returns;

    /// <summary>Gets the SQL statements of the body, each with the variables it reads without naming a table: what to check against the columns of its tables.</summary>
    public List<(SqlStatement Statement, List<string> Variables)> Embedded { get; } = [];

    public static (PlProgram Program, PlCompiler Compiler) Compile(CreateProcedureStatement create)
    {
        var body = SqlParser.ParseBody(create.Body);
        var compiler = new PlCompiler(create.Returns is not null);
        foreach (var (name, type) in create.Parameters)
        {
            compiler.Declare(name, type);
        }

        foreach (var variable in body.Variables)
        {
            var register = compiler.Declare(variable.Name, variable.Type);
            if (variable.Initial is { } initial)
            {
                compiler.Evaluate(register, initial);
            }
        }

        compiler.Block(body.Statements);
        compiler.Emit(PlOp.Return, PlProgram.None);
        return (new PlProgram
        {
            Name = create.Name,
            Parameters = create.Parameters.Count,
            Returns = create.Returns is { } returns ? (byte)returns : PlProgram.AnyType,
            RegisterNames = [.. compiler._names],
            RegisterTypes = [.. compiler._types],
            Constants = [.. compiler._constants],
            Code = [.. compiler._code],
        }, compiler);
    }

    /// <summary>The name a variable or a column of a loop row goes by: what a reference to it resolves through.</summary>
    public static string NameOf(ColumnRef column) => column.Table is null ? column.Name : $"{column.Table}.{column.Name}";

    private int Declare(string name, TkColumnType type)
    {
        if (_variables.ContainsKey(name))
        {
            throw new TkSqlException($"The procedure declares '{name}' more than once.");
        }

        var register = Register(name, (byte)type);
        _variables.Add(name, register);
        return register;
    }

    private int Register(string name, byte type)
    {
        if (_names.Count >= PlProgram.None)
        {
            throw new TkSqlException("The procedure is too large: it needs more registers than a program can have.");
        }

        _names.Add(name);
        _types.Add(type);
        return _names.Count - 1;
    }

    private int Temporary() => Register(string.Empty, PlProgram.AnyType);

    private void Block(IReadOnlyList<PlStatement> statements)
    {
        foreach (var statement in statements)
        {
            Statement(statement);
        }
    }

    private void Statement(PlStatement statement)
    {
        switch (statement)
        {
            case PlAssign assign:
                Evaluate(Variable(assign.Target), assign.Value);
                break;

            case PlIf conditional:
                var ends = new List<int>();
                foreach (var (condition, body) in conditional.Branches)
                {
                    var next = Unless(condition);
                    Block(body);
                    Emit(PlOp.Jump);
                    ends.Add(Hole());
                    Patch(next);
                }

                Block(conditional.Otherwise ?? []);
                ends.ForEach(Patch);
                break;

            case PlWhile loop:
                var top = Here;
                var exit = Unless(loop.Condition);
                Block(loop.Body);
                Emit(PlOp.Jump);
                Target(top);
                Patch(exit);
                break;

            case PlFor each:
                For(each);
                break;

            case PlReturn { Value: null }:
                Emit(PlOp.Return, PlProgram.None);
                break;

            case PlReturn { Value: { } value }:
                if (!_returns)
                {
                    throw new TkSqlException($"RETURN {value.Text} returns a value, but the procedure does not declare RETURNS.");
                }

                var result = Temporary();
                Evaluate(result, value);
                Emit(PlOp.Return, result);
                break;

            case PlRaise raise:
                var message = Temporary();
                Evaluate(message, raise.Message);
                Emit(PlOp.Raise, message);
                break;

            case PlCall call:
                var arguments = call.Arguments.Select(argument =>
                {
                    var register = Temporary();
                    Evaluate(register, argument);
                    return register;
                }).ToArray();
                Emit(PlOp.Call, Constant(PlConstant.Name, call.Name), call.Into is null ? PlProgram.None : Variable(call.Into), arguments.Length);
                Array.ForEach(arguments, Operand);
                break;

            case PlSql sql:
                var text = Embed(sql.Statement, sql.Text);
                if (sql.Statement is not SelectStatement select)
                {
                    Emit(PlOp.Execute, text);
                    break;
                }

                if (select.Items.Any(static item => item.Star) || select.Items.Count != sql.Into.Count)
                {
                    throw new TkSqlException($"SELECT … INTO needs as many columns as variables, and no *: {sql.Text}");
                }

                Emit(PlOp.Into, text, sql.Into.Count);
                foreach (var target in sql.Into)
                {
                    Operand(Variable(target));
                }

                break;

            default:
                throw new TkSqlException("Unsupported statement in a procedure.");
        }
    }

    private void For(PlFor each)
    {
        var fields = each.Query.Items.Select(item => item.Star || (item.Alias ?? (item.Expression as ColumnRef)?.Name) is not { } name
            ? throw new TkSqlException($"The query of FOR {each.Record} must name each of its columns: no *, and an expression needs AS a name.")
            : name).ToArray();
        if (fields.Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Length)
        {
            throw new TkSqlException($"The query of FOR {each.Record} has two columns with the same name.");
        }

        if (_variables.ContainsKey(each.Record))
        {
            throw new TkSqlException($"'{each.Record}' is a variable: the row of a FOR loop needs a name of its own.");
        }

        // The statement is embedded before the row exists: its query cannot read the row it is about to produce.
        var query = Embed(each.Query, each.Text);
        if (_rows.TryGetValue(each.Record, out var row))
        {
            if (row.Active || !row.Fields.SequenceEqual(fields, StringComparer.OrdinalIgnoreCase))
            {
                throw new TkSqlException($"The row name '{each.Record}' is used by another FOR loop of the procedure: give this one another name.");
            }
        }
        else
        {
            row = new Row(_names.Count, fields);
            foreach (var field in fields)
            {
                Register($"{each.Record}.{field}", PlProgram.AnyType);
            }

            _rows.Add(each.Record, row);
        }

        var rows = Temporary();
        Emit(PlOp.Open, rows, query);
        var top = Here;
        Emit(PlOp.Next, rows, row.First, fields.Length);
        var exit = Hole();
        row.Active = true;
        Block(each.Body);
        row.Active = false;
        Emit(PlOp.Jump);
        Target(top);
        Patch(exit);
    }

    private int Variable(string name) => _variables.TryGetValue(name, out var register)
        ? register
        : throw new TkSqlException($"There is no variable '{name}': declare it in DECLARE.");

    /// <summary>A reference, if it is to a variable or to a column of the row of a loop the statement is inside of.</summary>
    private int? Find(ColumnRef column)
    {
        if (column.Table is null)
        {
            return _variables.TryGetValue(column.Name, out var variable) ? variable : null;
        }

        if (_rows.TryGetValue(column.Table, out var row) && row.Active)
        {
            var field = Array.FindIndex(row.Fields, name => string.Equals(name, column.Name, StringComparison.OrdinalIgnoreCase));
            return field >= 0 ? row.First + field : throw new TkSqlException($"The row '{column.Table}' has no column '{column.Name}'.");
        }

        return null;
    }

    private void Evaluate(int register, PlExpression expression)
    {
        SqlRewriter.Expression(expression.Tree, column => Find(column) is null
            ? throw new TkSqlException($"There is no variable '{NameOf(column)}' in: {expression.Text}")
            : null);
        Emit(PlOp.Evaluate, register, Constant(PlConstant.Expression, expression.Text));
    }

    /// <summary>Evaluates a condition and jumps past what follows unless it is true. Returns the jump to complete.</summary>
    private int Unless(PlExpression condition)
    {
        var register = Temporary();
        Evaluate(register, condition);
        Emit(PlOp.JumpUnless, register);
        return Hole();
    }

    private int Embed(SqlStatement statement, string text)
    {
        var variables = new List<string>();
        SqlRewriter.Statement(statement, column =>
        {
            if (Find(column) is not null && column.Table is null)
            {
                variables.Add(column.Name);
            }

            return null;
        });
        Embedded.Add((statement, variables));
        return Constant(PlConstant.Statement, text);
    }

    private int Constant(PlConstant kind, string text)
    {
        if (!_known.TryGetValue((kind, text), out var index))
        {
            if (_constants.Count >= PlProgram.None)
            {
                throw new TkSqlException("The procedure is too large: it has more expressions and statements than a program can have.");
            }

            index = _constants.Count;
            _constants.Add((kind, text));
            _known.Add((kind, text), index);
        }

        return index;
    }

    private int Here => _code.Count;

    private void Emit(PlOp op, params ReadOnlySpan<int> operands)
    {
        _code.Add((byte)op);
        foreach (var operand in operands)
        {
            Operand(operand);
        }
    }

    private void Operand(int value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)value);
        _code.AddRange(bytes);
    }

    private void Target(int where)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, where);
        _code.AddRange(bytes);
    }

    /// <summary>Leaves room for a jump whose destination is not known yet.</summary>
    private int Hole()
    {
        Target(0);
        return Here - 4;
    }

    /// <summary>Makes a jump left open land here.</summary>
    private void Patch(int hole)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, Here);
        for (var i = 0; i < bytes.Length; i++)
        {
            _code[hole + i] = bytes[i];
        }
    }

    /// <summary>The row of a FOR loop: where its columns start among the registers, and whether the code being compiled is inside the loop.</summary>
    private sealed class Row(int first, string[] fields)
    {
        public int First { get; } = first;

        public string[] Fields { get; } = fields;

        public bool Active { get; set; }
    }
}
