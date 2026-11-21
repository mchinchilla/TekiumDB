// Copyright (c) Tekium. Licensed under the MIT License.

using TekiumDB.Sql.Execution;
using TekiumDB.Sql.Parsing;
using TekiumDB.Sql.Storage;

namespace TekiumDB.Sql.Procedures;

/// <summary>How much a call to a stored procedure may still do: shared by the procedures it calls.</summary>
internal sealed class PlBudget(long instructions, int depth)
{
    public long Limit { get; } = instructions;

    public long Remaining { get; set; } = instructions;

    public int MaxDepth { get; } = depth;

    public int Depth { get; set; }
}

/// <summary>
/// A stored procedure ready to run: its program, with its SQL statements parsed (their variables turned
/// into parameters) and its expressions bound to the registers the first time each is used.
/// </summary>
internal sealed class PlLoaded
{
    private readonly Evaluator?[] _expressions;
    private readonly SqlStatement?[] _statements;
    private readonly Dictionary<string, int> _registers = new(StringComparer.OrdinalIgnoreCase);

    public PlLoaded(PlProgram program)
    {
        Program = program;
        _expressions = new Evaluator?[program.Constants.Length];
        _statements = new SqlStatement?[program.Constants.Length];
        for (var i = 0; i < program.RegisterNames.Length; i++)
        {
            if (program.RegisterNames[i].Length > 0)
            {
                _registers[program.RegisterNames[i]] = i;
            }
        }

        // Statements are parsed now: a program whose text is not a statement a procedure may run never starts.
        for (var i = 0; i < program.Constants.Length; i++)
        {
            if (program.Constants[i].Kind == PlConstant.Statement)
            {
                var statement = SqlParser.ParseEmbedded(program.Constants[i].Text);
                _statements[i] = SqlRewriter.Statement(statement, column =>
                    _registers.ContainsKey(PlCompiler.NameOf(column)) ? new Parameter(PlCompiler.NameOf(column)) : null);
            }
        }
    }

    public PlProgram Program { get; }

    public SqlStatement Statement(int constant) => _statements[constant]!;

    public Evaluator Expression(int constant, StatementContext context)
    {
        if (_expressions[constant] is { } bound)
        {
            return bound;
        }

        var text = Program.Constants[constant].Text;
        bound = Binder.Bind(
            SqlParser.ParseProcedural(text),
            column => _registers.TryGetValue(PlCompiler.NameOf(column), out var register)
                ? register
                : throw new TkSqlException($"The stored procedure '{Program.Name}' reads '{PlCompiler.NameOf(column)}', which it does not have."),
            context,
            null);
        _expressions[constant] = bound;
        return bound;
    }

    /// <summary>Binds every expression now: an unknown function is found when the procedure is created, not when it runs.</summary>
    public void BindAll(StatementContext context)
    {
        for (var i = 0; i < Program.Constants.Length; i++)
        {
            if (Program.Constants[i].Kind == PlConstant.Expression)
            {
                Expression(i, context);
            }
        }
    }

    /// <summary>The variables, by name, as the parameters of a SQL statement.</summary>
    public Dictionary<string, object?> Values(object?[] registers)
    {
        var values = new Dictionary<string, object?>(_registers.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, register) in _registers)
        {
            values[name] = registers[register] is Rows ? null : registers[register];
        }

        return values;
    }

    /// <summary>The rows of the query of a loop, and how far the loop has gone.</summary>
    internal sealed class Rows(List<object?[]> all)
    {
        public List<object?[]> All { get; } = all;

        public int Next { get; set; }
    }
}

/// <summary>
/// Runs a stored procedure: a loop over its instructions, each a small step over registers. It can do
/// nothing but compute expressions, run SQL in the transaction it was called in, and call procedures:
/// stored ones, or those the application registered. Every instruction counts against the budget, so a
/// loop that never ends is stopped.
/// </summary>
internal static class PlMachine
{
    public static async ValueTask<object?> RunAsync(
        SqlEngine engine, TkWriteTransaction writer, PlLoaded loaded, object?[] arguments, PlBudget budget, CancellationToken cancellationToken)
    {
        var program = loaded.Program;
        if (arguments.Length != program.Parameters)
        {
            throw new TkSqlException($"Procedure {program.Name} takes {program.Parameters} arguments; {arguments.Length} were given.");
        }

        if (++budget.Depth > budget.MaxDepth)
        {
            throw new TkSqlException(
                $"Procedure {program.Name} was stopped: procedures called one another more than {budget.MaxDepth} deep (DbOptions.ProcedureCallDepth).");
        }

        try
        {
            var code = program.Code;
            var registers = new object?[program.RegisterNames.Length];
            var context = new StatementContext(new Dictionary<string, object?>(), engine.Routines);
            for (var i = 0; i < arguments.Length; i++)
            {
                Assign(i, arguments[i]);
            }

            var at = 0;
            while (at < code.Length)
            {
                if (--budget.Remaining < 0)
                {
                    throw new TkSqlException(
                        $"Procedure {program.Name} was stopped after {budget.Limit} instructions (DbOptions.ProcedureInstructionLimit): it may be in a loop that never ends.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                var op = (PlOp)code[at++];
                switch (op)
                {
                    case PlOp.Evaluate:
                        var target = Operand();
                        Assign(target, Compute(Operand()));
                        break;

                    case PlOp.Jump:
                        at = PlProgram.Int32(code, at);
                        break;

                    case PlOp.JumpUnless:
                        var condition = registers[Operand()];
                        at = Binder.Truth(condition) == true ? at + 4 : PlProgram.Int32(code, at);
                        break;

                    case PlOp.Execute:
                        if (loaded.Statement(Operand()) is not (InsertStatement or UpdateStatement or DeleteStatement) and var refused)
                        {
                            throw Damaged($"it runs a {refused.GetType().Name} where only INSERT, UPDATE or DELETE can go");
                        }

                        await SqlAsync(loaded.Statement(PlProgram.UInt16(code, at - 2))).ConfigureAwait(false);
                        break;

                    case PlOp.Open:
                        var cursor = Operand();
                        registers[cursor] = new PlLoaded.Rows((await QueryAsync(Operand()).ConfigureAwait(false)).Rows);
                        break;

                    case PlOp.Next:
                        if (registers[Operand()] is not PlLoaded.Rows rows)
                        {
                            throw Damaged("a loop reads rows it never asked for");
                        }

                        var first = Operand();
                        var count = Operand();
                        if (rows.Next >= rows.All.Count)
                        {
                            at = PlProgram.Int32(code, at);
                            break;
                        }

                        at += 4;
                        var row = rows.All[rows.Next++];
                        if (row.Length != count)
                        {
                            throw Damaged($"a loop expects {count} columns and its query gives {row.Length}");
                        }

                        row.CopyTo(registers, first);
                        break;

                    case PlOp.Into:
                        var found = await QueryAsync(Operand()).ConfigureAwait(false);
                        var targets = Operand();
                        if (found.Names.Length != targets)
                        {
                            throw Damaged($"SELECT INTO has {found.Names.Length} columns for {targets} variables");
                        }

                        for (var i = 0; i < targets; i++)
                        {
                            Assign(Operand(), found.Rows.Count > 0 ? found.Rows[0][i] : null);
                        }

                        break;

                    case PlOp.Call:
                        var name = program.Constants[Operand()].Text;
                        var result = Operand();
                        var values = new object?[Operand()];
                        for (var i = 0; i < values.Length; i++)
                        {
                            values[i] = registers[Operand()];
                        }

                        var returned = await engine.InvokeAsync(writer, name, values, cancellationToken).ConfigureAwait(false);
                        if (result != PlProgram.None)
                        {
                            Assign(result, returned);
                        }

                        break;

                    case PlOp.Return:
                        var value = Operand();
                        return value == PlProgram.None || registers[value] is not { } answer
                            ? null
                            : program.Returns == PlProgram.AnyType ? answer : Convert(answer, program.Returns, "the result");

                    case PlOp.Raise:
                        throw new TkSqlException(registers[Operand()] is { } message ? TkArgument.ToText(message) : $"Procedure {program.Name} raised an error.");

                    default:
                        throw Damaged("it has an instruction this build does not know");
                }
            }

            return null;

            int Operand()
            {
                at += 2;
                return PlProgram.UInt16(code, at - 2);
            }

            void Assign(int register, object? value) =>
                registers[register] = value is null || program.RegisterTypes[register] == PlProgram.AnyType
                    ? value
                    : Convert(value, program.RegisterTypes[register], program.RegisterNames[register]);

            object Convert(object value, byte type, string what)
            {
                try
                {
                    return SqlValues.Convert(value, (TkColumnType)type, what);
                }
                catch (Exception exception) when (exception is ArithmeticException or FormatException or InvalidCastException)
                {
                    throw new TkSqlException($"Procedure {program.Name}: {SqlValues.Show(value)} cannot be {what}: {exception.Message}", exception);
                }
            }

            object? Compute(int constant)
            {
                try
                {
                    return loaded.Expression(constant, context)(registers);
                }
                catch (Exception exception) when (exception is ArithmeticException or FormatException or InvalidCastException)
                {
                    throw new TkSqlException($"Procedure {program.Name}: {program.Constants[constant].Text} failed: {exception.Message}", exception);
                }
            }

            ValueTask<SqlResult> SqlAsync(SqlStatement statement) =>
                SqlEngine.ExecuteAsync(writer, statement, new StatementContext(loaded.Values(registers), engine.Routines), cancellationToken);

            ValueTask<SqlResult> QueryAsync(int constant) => loaded.Statement(constant) is SelectStatement query
                ? SqlAsync(query)
                : throw Damaged("it reads rows from a statement that is not a SELECT");

            TkSqlException Damaged(string why) => new($"The stored procedure '{program.Name}' cannot be run: {why}.");
        }
        finally
        {
            budget.Depth--;
        }
    }
}
