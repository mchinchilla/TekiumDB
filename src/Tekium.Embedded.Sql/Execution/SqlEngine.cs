// Copyright (c) Tekium. Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Text;
using Tekium.Embedded.Catalog;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Sql.Parsing;
using Tekium.Embedded.Sql.Procedures;
using Tekium.Embedded.Sql.Storage;

namespace Tekium.Embedded.Sql.Execution;

/// <summary>
/// Runs SQL statements against a database, inside a transaction the caller gives. Parsed statements are
/// kept by their text, so a statement run again is not parsed again; how to reach the rows is decided
/// each time, because it depends on the values of the parameters and on the data.
/// </summary>
internal sealed class SqlEngine
{
    private const int MaxCachedStatements = 1024;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TekiumDb, SqlEngine> Engines = [];

    private readonly ConcurrentDictionary<string, SqlStatement> _statements = new(StringComparer.Ordinal);
    private const string StoredProceduresOff =
        "Stored procedures are off: a procedure kept in the file is program inside a data file. Open the database with DbOptions.AllowStoredProcedures = true to create and run them.";

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TkWriteTransaction, PlBudget> Budgets = [];

    private readonly ConcurrentDictionary<string, TkRoutine> _routines = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (byte[] Bytes, PlLoaded Loaded)> _procedures = new(StringComparer.Ordinal);

    /// <summary>Gets the functions and procedures the application registered, by name.</summary>
    public IReadOnlyDictionary<string, TkRoutine> Routines => _routines;

    /// <summary>The engine of a database: one for as long as the database lives, whatever connections use it.</summary>
    public static SqlEngine For(TekiumDb database) => Engines.GetValue(database, static _ => new SqlEngine());

    public void Register(IEnumerable<TkRoutine> routines)
    {
        foreach (var routine in routines)
        {
            if (!_routines.TryAdd(routine.Name, routine) && !ReferenceEquals(_routines[routine.Name], routine))
            {
                throw new ArgumentException($"A function or procedure named '{routine.Name}' is already registered.", nameof(routines));
            }
        }
    }

    /// <summary>Gets how many statement texts were parsed, as opposed to found already parsed.</summary>
    public int Parsed { get; private set; }

    public SqlStatement Parse(string sql)
    {
        if (_statements.TryGetValue(sql, out var known))
        {
            return known;
        }

        var statement = SqlParser.Parse(sql);
        Parsed++;
        if (_statements.Count < MaxCachedStatements)
        {
            _statements.TryAdd(sql, statement);
        }

        return statement;
    }

    /// <summary>Whether a statement only reads: it can run on a snapshot, with no write transaction.</summary>
    public static bool ReadsOnly(SqlStatement statement) => statement is SelectStatement or ExplainStatement;

    public static async ValueTask<SqlResult> ExecuteAsync(
        TkTransaction transaction, SqlStatement statement, StatementContext parameters, CancellationToken cancellationToken)
    {
        switch (statement)
        {
            case SelectStatement select:
                return await new SelectRunner(transaction, parameters, cancellationToken).RunAsync(select, null, []).ConfigureAwait(false);

            case ExplainStatement explain:
                var root = new PlanNode(string.Empty, null);
                await new SelectRunner(transaction, parameters, cancellationToken).RunAsync(explain.Query, root, []).ConfigureAwait(false);
                var lines = new List<object?[]>();
                Describe(root.Children[0], 0, lines);
                return new SqlResult { Names = ["plan", "estimated_rows", "actual_rows"], Types = [typeof(string), typeof(long), typeof(long)], Rows = lines };

            default:
                if (transaction is not TkWriteTransaction writer)
                {
                    throw new InvalidOperationException("A statement that changes the database needs a write transaction.");
                }

                return statement switch
                {
                    InsertStatement insert => await InsertAsync(writer, insert, parameters, cancellationToken).ConfigureAwait(false),
                    UpdateStatement update => await UpdateAsync(writer, update, parameters, cancellationToken).ConfigureAwait(false),
                    DeleteStatement delete => await DeleteAsync(writer, delete, parameters, cancellationToken).ConfigureAwait(false),
                    CreateTableStatement create => await CreateTableAsync(writer, create, cancellationToken).ConfigureAwait(false),
                    CreateIndexStatement create => await CreateIndexAsync(writer, create, cancellationToken).ConfigureAwait(false),
                    CreateViewStatement create => await CreateViewAsync(writer, create, parameters, cancellationToken).ConfigureAwait(false),
                    DropStatement drop => await DropAsync(writer, drop, cancellationToken).ConfigureAwait(false),
                    AlterTableStatement alter => await AlterTableAsync(writer, alter, cancellationToken).ConfigureAwait(false),
                    CallStatement call => await CallAsync(writer, call, parameters, cancellationToken).ConfigureAwait(false),
                    CreateProcedureStatement create => await For(writer.Database).CreateProcedureAsync(writer, create, cancellationToken).ConfigureAwait(false),
                    _ => throw new TkSqlException("Unsupported statement."),
                };
        }
    }

    private static async ValueTask<SqlResult> CallAsync(TkWriteTransaction writer, CallStatement call, StatementContext context, CancellationToken cancellationToken)
    {
        var arguments = call.Arguments.Select(argument => Binder.Constant(argument, context)).ToArray();
        var result = await For(writer.Database).InvokeAsync(writer, call.Name, arguments, cancellationToken).ConfigureAwait(false);
        return result is null
            ? new SqlResult()
            : new SqlResult { Names = ["result"], Types = [result.GetType()], Rows = [[result]] };
    }

    /// <summary>Calls a procedure by name: one the application registered, or else one stored in the file.</summary>
    public async ValueTask<object?> InvokeAsync(TkWriteTransaction writer, string name, object?[] arguments, CancellationToken cancellationToken)
    {
        if (_routines.TryGetValue(name, out var routine))
        {
            if (routine.Procedure is not { } procedure)
            {
                throw new TkSqlException($"{name} is a function, not a procedure: use it in an expression.");
            }

            return routine.ParameterCount == arguments.Length
                ? await procedure(new TkContext(writer.Database, writer), arguments, cancellationToken).ConfigureAwait(false)
                : throw new TkSqlException($"Procedure {routine.Name} takes {routine.ParameterCount} arguments; {arguments.Length} were given.");
        }

        if (await StoredAsync(writer, name, cancellationToken).ConfigureAwait(false) is not var (actual, bytes))
        {
            throw new TkSqlException($"There is no procedure '{name}': none is stored in the database and the application has not registered one.");
        }

        var options = writer.Database.Options;
        if (!options.AllowStoredProcedures)
        {
            throw new TkSqlException(StoredProceduresOff);
        }

        // The loaded form is kept while the stored bytes are the same: another transaction may see another version.
        if (!_procedures.TryGetValue(actual, out var cached) || !cached.Bytes.AsSpan().SequenceEqual(bytes))
        {
            cached = (bytes, new PlLoaded(PlProgram.Decode(bytes, actual)));
            _procedures[actual] = cached;
        }

        // One budget for the whole call, however procedures call one another from here on.
        if (Budgets.TryGetValue(writer, out var budget))
        {
            return await PlMachine.RunAsync(this, writer, cached.Loaded, arguments, budget, cancellationToken).ConfigureAwait(false);
        }

        budget = new PlBudget(options.ProcedureInstructionLimit, options.ProcedureCallDepth);
        Budgets.Add(writer, budget);
        try
        {
            return await PlMachine.RunAsync(this, writer, cached.Loaded, arguments, budget, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A procedure is called as one thing. Stopped halfway, what it had written is not what anyone
            // asked for: the transaction can only be rolled back.
            writer.Fail();
            throw;
        }
        finally
        {
            Budgets.Remove(writer);
        }
    }

    /// <summary>The program of a stored procedure, by name, exactly or ignoring case. Null if there is none.</summary>
    private static async ValueTask<(string Name, byte[] Bytes)?> StoredAsync(TkTransaction transaction, string name, CancellationToken cancellationToken)
    {
        string? actual = null;
        await foreach (var existing in SqlTable.NamesAsync(transaction, 9, cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(existing, name, StringComparison.Ordinal))
            {
                actual = existing;
                break;
            }

            actual = string.Equals(existing, name, StringComparison.OrdinalIgnoreCase) ? existing : actual;
        }

        return actual is null
            ? null
            : (actual, await CatalogStore.ReadAsync(transaction.Source, transaction.CatalogRoot, CatalogStore.ProcedureKey(actual), cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<SqlResult> CreateProcedureAsync(TkWriteTransaction writer, CreateProcedureStatement create, CancellationToken cancellationToken)
    {
        if (!writer.Database.Options.AllowStoredProcedures)
        {
            throw new TkSqlException(StoredProceduresOff);
        }

        if (_routines.ContainsKey(create.Name))
        {
            throw new TkSqlException($"The application has registered a routine named '{create.Name}': a stored procedure with that name would never be called.");
        }

        var existing = await StoredAsync(writer, create.Name, cancellationToken).ConfigureAwait(false);
        if (existing is not null && !create.OrReplace)
        {
            throw new TkSqlException($"There is already a procedure '{create.Name}'. Use CREATE OR REPLACE PROCEDURE to change it.");
        }

        byte[] key;
        try
        {
            key = CatalogStore.ProcedureKey(existing?.Name ?? create.Name);
        }
        catch (ArgumentException invalid)
        {
            throw new TkSqlException($"A procedure cannot be named '{create.Name}': {invalid.Message}", invalid);
        }

        var (program, compiler) = PlCompiler.Compile(create);

        // A variable with the name of a column of a table its statement reads: which of the two was meant cannot be known.
        foreach (var (statement, variables) in compiler.Embedded)
        {
            foreach (var table in SqlRewriter.Tables(statement))
            {
                if (variables.Count > 0 && await SqlTable.OpenAsync(writer, table, cancellationToken).ConfigureAwait(false) is { } opened
                    && variables.Find(variable => opened.Columns.Any(column => string.Equals(column.Name, variable, StringComparison.OrdinalIgnoreCase))) is { } clash)
                {
                    throw new TkSqlException(
                        $"'{clash}' is a variable of the procedure and a column of table '{table}': rename the variable, or write the column as {table}.{clash}.");
                }
            }
        }

        // Decoding what was just encoded runs the same checks a call will.
        var bytes = program.Encode();
        new PlLoaded(PlProgram.Decode(bytes, create.Name)).BindAll(new StatementContext(new Dictionary<string, object?>(), _routines));
        await writer.Database.Tree.PutAsync(writer.Inner, key, bytes, cancellationToken).ConfigureAwait(false);
        return new SqlResult();
    }

    private static void Describe(PlanNode node, int depth, List<object?[]> lines)
    {
        lines.Add([new string(' ', depth * 2) + node.Text, node.Estimated, node.Actual]);
        foreach (var child in node.Children)
        {
            Describe(child, depth + 1, lines);
        }
    }

    private static async ValueTask<SqlTable> TableAsync(TkWriteTransaction writer, string name, CancellationToken cancellationToken) =>
        await SqlTable.OpenAsync(writer, name, cancellationToken).ConfigureAwait(false)
        ?? throw new TkSqlException(
            await SelectRunner.ViewAsync(writer, name, cancellationToken).ConfigureAwait(false) is null
                ? $"There is no table '{name}'."
                : $"'{name}' is a view: views cannot be written to.");

    private static async ValueTask<SqlResult> InsertAsync(
        TkWriteTransaction writer, InsertStatement insert, StatementContext parameters, CancellationToken cancellationToken)
    {
        var table = await TableAsync(writer, insert.Table, cancellationToken).ConfigureAwait(false);
        var targets = insert.Columns is null
            ? [.. Enumerable.Range(0, table.Columns.Length)]
            : insert.Columns.Select(name => table.Column(name) is >= 0 and var at ? at : throw new TkSqlException($"Table '{table.Name}' has no column '{name}'.")).ToArray();
        if (targets.Distinct().Count() != targets.Length)
        {
            throw new TkSqlException("INSERT names a column twice.");
        }

        var rows = new List<object?[]>();
        foreach (var values in insert.Rows)
        {
            if (values.Count != targets.Length)
            {
                throw new TkSqlException($"INSERT gives {values.Count} values for {targets.Length} columns.");
            }

            var row = new object?[table.Columns.Length];
            for (var i = 0; i < targets.Length; i++)
            {
                var column = table.Columns[targets[i]];
                row[targets[i]] = Binder.Constant(values[i], parameters) is { } value ? SqlValues.Convert(value, column.Type, column.Name) : null;
            }

            rows.Add(row);
        }

        // A whole-number key that was not given is the next after the highest in the table.
        var keyColumn = table.Columns[table.KeyColumn];
        if (keyColumn.Type is TkColumnType.I32 or TkColumnType.I64 && rows.Exists(row => row[table.KeyColumn] is null))
        {
            var next = await table.NextKeyAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in rows.Where(row => row[table.KeyColumn] is null))
            {
                row[table.KeyColumn] = SqlValues.Convert(next++, keyColumn.Type, keyColumn.Name);
            }
        }

        foreach (var row in rows)
        {
            await table.InsertAsync(row, cancellationToken).ConfigureAwait(false);
        }

        return Returned(table, rows, insert.Returning, parameters);
    }

    private static async ValueTask<SqlResult> UpdateAsync(
        TkWriteTransaction writer, UpdateStatement update, StatementContext parameters, CancellationToken cancellationToken)
    {
        var table = await TableAsync(writer, update.Table, cancellationToken).ConfigureAwait(false);
        var runner = new SelectRunner(writer, parameters, cancellationToken);

        // Every row to change is found before any is changed: the search never sees its own writes.
        var matching = await runner.MatchingAsync(table, update.Where, update.Alias).ConfigureAwait(false);
        var assignments = update.Set.Select(set => (
            Column: table.Column(set.Column) is >= 0 and var at ? at : throw new TkSqlException($"Table '{table.Name}' has no column '{set.Column}'."),
            Value: Binder.Bind(set.Value, runner.Resolve, parameters, null))).ToArray();
        var written = new List<object?[]>(matching.Count);
        foreach (var old in matching)
        {
            var row = (object?[])old.Clone();
            written.Add(row);
            foreach (var (column, value) in assignments)
            {
                row[column] = value(old) is { } computed ? SqlValues.Convert(computed, table.Columns[column].Type, table.Columns[column].Name) : null;
            }

            if (row[table.KeyColumn] is { } key && old[table.KeyColumn] is { } before && Binder.Compare(key, before) == 0)
            {
                await table.UpdateAsync(old, row, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // The key changed: it is another row.
                await table.DeleteAsync(old, cancellationToken).ConfigureAwait(false);
                await table.InsertAsync(row, cancellationToken).ConfigureAwait(false);
            }
        }

        return Returned(table, written, update.Returning, parameters);
    }

    /// <summary>What a statement that writes gives back: how many rows, and what its RETURNING asks of each.</summary>
    private static SqlResult Returned(SqlTable table, List<object?[]> rows, IReadOnlyList<SelectItem>? returning, StatementContext parameters)
    {
        if (returning is null)
        {
            return new SqlResult { RecordsAffected = rows.Count };
        }

        var items = returning.SelectMany(item => item.Star
            ? table.Columns.Select(static column => new SelectItem(new ColumnRef(null, column.Name), column.Name, false, null))
            : [item]).ToList();
        var values = items.Select(item => Binder.Bind(
            item.Expression!,
            column => table.Column(column.Name) is >= 0 and var at ? at : throw new TkSqlException($"Table '{table.Name}' has no column '{column.Name}'."),
            parameters,
            null)).ToArray();
        var returned = rows.Select(row => Array.ConvertAll(values, value => value(row))).ToList();
        return new SqlResult
        {
            Names = [.. items.Select(static (item, i) => item.Alias ?? (item.Expression as ColumnRef)?.Name ?? $"column{i + 1}")],
            Types = [.. items.Select((item, i) => returned.Select(row => row[i]).FirstOrDefault(static value => value is not null)?.GetType()
                ?? (item.Expression is ColumnRef column ? SqlValues.ClrType(table.Columns[table.Column(column.Name)].Type) : typeof(object)))],
            Rows = returned,
            RecordsAffected = rows.Count,
        };
    }

    private static async ValueTask<SqlResult> DeleteAsync(
        TkWriteTransaction writer, DeleteStatement delete, StatementContext parameters, CancellationToken cancellationToken)
    {
        var table = await TableAsync(writer, delete.Table, cancellationToken).ConfigureAwait(false);
        var matching = await new SelectRunner(writer, parameters, cancellationToken).MatchingAsync(table, delete.Where, delete.Alias).ConfigureAwait(false);
        foreach (var row in matching)
        {
            await table.DeleteAsync(row, cancellationToken).ConfigureAwait(false);
        }

        return Returned(table, matching, delete.Returning, parameters);
    }

    private static async ValueTask<SqlResult> CreateTableAsync(TkWriteTransaction writer, CreateTableStatement create, CancellationToken cancellationToken)
    {
        if (await SqlTable.ResolveAsync(writer, create.Name, cancellationToken).ConfigureAwait(false) is not null)
        {
            return create.IfNotExists ? new SqlResult() : throw new TkSqlException($"There is already a table '{create.Name}'.");
        }

        if (await SelectRunner.ViewAsync(writer, create.Name, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new TkSqlException($"There is already a view '{create.Name}'.");
        }

        var keys = create.Columns.Where(static column => column.PrimaryKey).ToList();
        if (keys.Count != 1)
        {
            throw new TkSqlException($"Table '{create.Name}' needs exactly one PRIMARY KEY column; it declares {keys.Count}.");
        }

        if (!TkKey.IsSupported(keys[0].Type))
        {
            throw new TkSqlException($"Column '{keys[0].Name}' cannot be the primary key: a key is an INT, a BIGINT, a UUID or a TEXT.");
        }

        if (create.Columns.Select(static column => column.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != create.Columns.Count)
        {
            throw new TkSqlException($"Table '{create.Name}' declares a column twice.");
        }

        TkColumn[] columns;
        byte[] tableKey;
        try
        {
            columns = [.. create.Columns.Select(static column => new TkColumn(column.Name, column.Type, !column.NotNull))];
            tableKey = CatalogStore.TableKey(create.Name);
        }
        catch (ArgumentException invalid)
        {
            throw new TkSqlException($"Table '{create.Name}' cannot be created: {invalid.Message}", invalid);
        }

        var schema = new TableSchema(1, columns, create.Columns.ToList().FindIndex(static column => column.PrimaryKey));
        var encoded = new byte[schema.EncodedSize()];
        schema.Encode(encoded);
        var state = await writer.TableAsync(create.Name, tableKey, cancellationToken).ConfigureAwait(false);
        await writer.Database.Tree.PutAsync(writer.Inner, CatalogStore.SchemaKey(tableKey, 1), encoded, cancellationToken).ConfigureAwait(false);
        // The table is in the catalog from now on, for the rest of this transaction to find it by name.
        var info = new byte[TableInfo.EncodedSize];
        new TableInfo(default, 1).Encode(info);
        await writer.Database.Tree.PutAsync(writer.Inner, tableKey, info, cancellationToken).ConfigureAwait(false);
        state.Schema = schema;
        state.UncommittedVersion = 1;
        state.Root = default;
        state.Changed = true;

        // UNIQUE on a column is a unique index on it.
        foreach (var column in create.Columns.Where(static column => column.Unique && !column.PrimaryKey))
        {
            await CreateIndexAsync(writer, new CreateIndexStatement($"{column.Name}_unique", create.Name, [column.Name], true, false), cancellationToken)
                .ConfigureAwait(false);
        }

        // A FOREIGN KEY is declared once the table is there: it may refer to the table itself.
        foreach (var foreignKey in create.ForeignKeys)
        {
            await AddForeignKeyAsync(writer, await TableAsync(writer, create.Name, cancellationToken).ConfigureAwait(false), foreignKey, cancellationToken).ConfigureAwait(false);
        }

        return new SqlResult();
    }

    private static async ValueTask<SqlResult> AlterTableAsync(TkWriteTransaction writer, AlterTableStatement alter, CancellationToken cancellationToken)
    {
        var table = await TableAsync(writer, alter.Table, cancellationToken).ConfigureAwait(false);
        if (table.ReadOnlyReason is { } reason)
        {
            throw new TkSqlException($"Table '{table.Name}' cannot be changed through SQL: {reason}.");
        }

        if (alter.AddForeignKey is { } foreignKey)
        {
            await AddForeignKeyAsync(writer, table, foreignKey, cancellationToken).ConfigureAwait(false);
        }
        else if (alter.DropConstraint is { } constraint)
        {
            await DropConstraintAsync(writer, table, constraint, alter.IfExists, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await AddColumnAsync(writer, table, alter.AddColumn!, alter.HasDefault, alter.Default, cancellationToken).ConfigureAwait(false);
        }

        return new SqlResult();
    }

    /// <summary>
    /// Declares a FOREIGN KEY exactly as a row type would: the same entry in the catalog of the table, the
    /// same note in the table referred to, and an index that starts with the column, made if there is none.
    /// The rows there are must already respect it.
    /// </summary>
    private static async ValueTask AddForeignKeyAsync(TkWriteTransaction writer, SqlTable table, ForeignKeyDefinition definition, CancellationToken cancellationToken)
    {
        var column = table.Column(definition.Column) is >= 0 and var at ? at : throw new TkSqlException($"Table '{table.Name}' has no column '{definition.Column}'.");
        var name = definition.Name ?? $"fk_{table.Columns[column].Name}";
        if (name.Length == 0 || Encoding.UTF8.GetByteCount(name) > ConstraintSet.MaxNameBytes)
        {
            throw new TkSqlException($"Table '{table.Name}': the name of constraint '{name}' must take from 1 to {ConstraintSet.MaxNameBytes} UTF-8 bytes.");
        }

        var parent = string.Equals(definition.Table, table.Name, StringComparison.OrdinalIgnoreCase)
            ? table
            : await SqlTable.OpenAsync(writer, definition.Table, cancellationToken).ConfigureAwait(false)
                ?? throw new TkSqlException($"FOREIGN KEY '{name}' of table '{table.Name}' refers to '{definition.Table}', and there is no table '{definition.Table}'.");
        var parentKey = parent.Columns[parent.KeyColumn];
        if (definition.TargetColumn is { } target && parent.Column(target) != parent.KeyColumn)
        {
            throw new TkSqlException(
                $"FOREIGN KEY '{name}' of table '{table.Name}' cannot refer to '{target}' of '{parent.Name}': a foreign key refers only to its primary key, '{parentKey.Name}'.");
        }

        if (table.Columns[column].Type != parentKey.Type)
        {
            throw new TkSqlException(
                $"FOREIGN KEY '{name}' of table '{table.Name}' cannot be declared: column '{table.Columns[column].Name}' is {TypeName(table.Columns[column].Type)} and the key of '{parent.Name}' is {TypeName(parentKey.Type)}.");
        }

        var state = await writer.TableAsync(table.Name, table.TableKey, cancellationToken).ConfigureAwait(false);
        var constraints = ConstraintSet.Decode(await writer.ConstraintsAsync(state, cancellationToken).ConfigureAwait(false));
        if (constraints.ForeignKeys.Any(known => string.Equals(known.Name, name, StringComparison.OrdinalIgnoreCase))
            || constraints.Checks.Any(known => string.Equals(known.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new TkSqlException($"Table '{table.Name}' already has a constraint '{name}'.");
        }

        // The rows there are: nothing is declared if one of them refers to a row that is not there.
        var declared = new ForeignKeyInfo(name, parent.Name, name, table.Columns[column].IsNullable);
        var referring = new List<(object Key, object Value)>();
        await foreach (var row in table.ScanAsync(default, default, cancellationToken).ConfigureAwait(false))
        {
            if (row[column] is { } value)
            {
                referring.Add((row[table.KeyColumn]!, value));
            }
        }

        foreach (var (key, value) in referring)
        {
            if (!await table.RefersToARowAsync(writer, declared, value, parentKey.Type, null, cancellationToken).ConfigureAwait(false))
            {
                throw new TkSqlException(
                    $"FOREIGN KEY '{name}' cannot be added to table '{table.Name}': the row with key {SqlValues.Show(key)} refers to {SqlValues.Show(value)}, and no row of '{parent.Name}' has that key.");
            }
        }

        // Deleting a row asks who refers to it: that needs an index that starts with the column.
        var index = table.Indexes.Find(index => SqlTable.CanCheck(index) && index.Columns[0] == column)?.Name;
        if (index is null)
        {
            index = name;
            while (table.Indexes.Exists(existing => string.Equals(existing.Name, index, StringComparison.OrdinalIgnoreCase)))
            {
                index += "_";
            }

            await CreateIndexAsync(writer, new CreateIndexStatement(index, table.Name, [table.Columns[column].Name], false, false), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            ForeignKeyInfo[] foreignKeys = [.. constraints.ForeignKeys, declared with { Index = index }];
            await writer.SetConstraintsAsync(state, ConstraintSet.Create(constraints.Checks, foreignKeys).Encoded, cancellationToken).ConfigureAwait(false);
            await SetReferrersAsync(writer, table.Name, parent.Name, foreignKeys, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            writer.Fail();
            throw;
        }
    }

    /// <summary>Tells a table referred to which foreign keys of a table refer to it now: who to ask before deleting a row.</summary>
    private static ValueTask SetReferrersAsync(TkWriteTransaction writer, string table, string parent, ForeignKeyInfo[] foreignKeys, CancellationToken cancellationToken) =>
        writer.SetReferrersAsync(
            parent,
            table,
            foreignKeys.Where(key => string.Equals(key.Table, parent, StringComparison.Ordinal)).Select(key => new Referrer(table, key.Name, key.Index, key.IsNullable)),
            cancellationToken);

    private static async ValueTask DropConstraintAsync(TkWriteTransaction writer, SqlTable table, string name, bool ifExists, CancellationToken cancellationToken)
    {
        var state = await writer.TableAsync(table.Name, table.TableKey, cancellationToken).ConfigureAwait(false);
        var constraints = ConstraintSet.Decode(await writer.ConstraintsAsync(state, cancellationToken).ConfigureAwait(false));
        var at = Array.FindIndex(constraints.ForeignKeys, known => string.Equals(known.Name, name, StringComparison.Ordinal));
        at = at >= 0 ? at : Array.FindIndex(constraints.ForeignKeys, known => string.Equals(known.Name, name, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
        {
            if (ifExists)
            {
                return;
            }

            throw new TkSqlException($"Table '{table.Name}' has no constraint '{name}'.");
        }

        // The index the constraint was checked with stays: it may be one that was there before.
        var dropped = constraints.ForeignKeys[at];
        try
        {
            ForeignKeyInfo[] foreignKeys = [.. constraints.ForeignKeys.Where((_, i) => i != at)];
            await writer.SetConstraintsAsync(state, ConstraintSet.Create(constraints.Checks, foreignKeys).Encoded, cancellationToken).ConfigureAwait(false);
            await SetReferrersAsync(writer, table.Name, dropped.Table, foreignKeys, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            writer.Fail();
            throw;
        }
    }

    /// <summary>
    /// Adds a column as a new version of the schema: no row is rewritten, and the rows there are read the
    /// column as the typed API reads it (NULL, or the zero of its type if it cannot be NULL). Only a
    /// DEFAULT that is something else writes the rows again, with it.
    /// </summary>
    private static async ValueTask AddColumnAsync(
        TkWriteTransaction writer, SqlTable table, ColumnDefinition column, bool hasDefault, object? byDefault, CancellationToken cancellationToken)
    {
        if (column.PrimaryKey)
        {
            throw new TkSqlException($"Column '{column.Name}' cannot be added as the primary key of table '{table.Name}': the primary key of a table does not change.");
        }

        if (table.Column(column.Name) >= 0)
        {
            throw new TkSqlException($"Table '{table.Name}' already has a column '{column.Name}'.");
        }

        if (table.Version == byte.MaxValue)
        {
            throw new TkSqlException($"Table '{table.Name}' has used all {byte.MaxValue} schema versions a table may have.");
        }

        var value = byDefault is null ? null : SqlValues.Convert(byDefault, column.Type, column.Name);
        if (column.NotNull && hasDefault && value is null)
        {
            throw new TkSqlException($"Column '{column.Name}' of table '{table.Name}' cannot be NULL, and its DEFAULT is NULL.");
        }

        var rows = new List<object?[]>();
        await foreach (var row in table.ScanAsync(default, default, cancellationToken).ConfigureAwait(false))
        {
            rows.Add(row);
            if (!column.Unique && value is null)
            {
                // Nobody needs the rows: only whether there is more than one, or what to write them with.
                break;
            }
        }

        // What every row there is will read: with more than one, it cannot be unique.
        if (column.Unique && rows.Count > 1 && (value is not null || column.NotNull))
        {
            throw new TkSqlException(
                $"Column '{column.Name}' cannot be unique: the {rows.Count} rows of table '{table.Name}' would all have {SqlValues.Show(value ?? SqlValues.Zero(column.Type))} in it.");
        }

        TableSchema schema;
        try
        {
            schema = new TableSchema((byte)(table.Version + 1), [.. table.Columns, new TkColumn(column.Name, column.Type, !column.NotNull)], table.KeyColumn);
            if (schema.Columns.Length > TableSchema.MaxColumns)
            {
                throw new TkSqlException($"Table '{table.Name}' already has {TableSchema.MaxColumns} columns, the most a table may.");
            }
        }
        catch (ArgumentException invalid)
        {
            throw new TkSqlException($"Column '{column.Name}' cannot be added to table '{table.Name}': {invalid.Message}", invalid);
        }

        var state = await writer.TableAsync(table.Name, table.TableKey, cancellationToken).ConfigureAwait(false);
        try
        {
            var encoded = new byte[schema.EncodedSize()];
            schema.Encode(encoded);
            await writer.Database.Tree.PutAsync(writer.Inner, CatalogStore.SchemaKey(table.TableKey, schema.Version), encoded, cancellationToken).ConfigureAwait(false);
            state.Schema = schema;
            state.UncommittedVersion = schema.Version;
            state.Changed = true;

            // Whatever row type wrote the table in this transaction must look at its columns again.
            state.WritesAs = null;

            var position = schema.Columns.Length - 1;
            if (value is not null && !(column.NotNull && Same(value, SqlValues.Zero(column.Type))))
            {
                var changed = await TableAsync(writer, table.Name, cancellationToken).ConfigureAwait(false);
                var old = new List<object?[]>();
                await foreach (var row in changed.ScanAsync(default, default, cancellationToken).ConfigureAwait(false))
                {
                    old.Add(row);
                }

                foreach (var row in old)
                {
                    var written = (object?[])row.Clone();
                    written[position] = value;
                    await changed.UpdateAsync(row, written, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is not TkSqlException)
        {
            writer.Fail();
            throw;
        }

        if (column.Unique)
        {
            await CreateIndexAsync(writer, new CreateIndexStatement($"{column.Name}_unique", table.Name, [column.Name], true, false), cancellationToken).ConfigureAwait(false);
        }

        static bool Same(object left, object right) => left is byte[] bytes ? right is byte[] other && bytes.AsSpan().SequenceEqual(other) : left.Equals(right);
    }

    /// <summary>The type of a column as SQL names it, with its article.</summary>
    private static string TypeName(TkColumnType type) => type switch
    {
        TkColumnType.I32 => "an INT",
        TkColumnType.I64 => "a BIGINT",
        TkColumnType.I16 => "a SMALLINT",
        TkColumnType.U8 => "a TINYINT",
        TkColumnType.Boolean => "a BOOLEAN",
        TkColumnType.Text => "a TEXT",
        TkColumnType.Uuid => "a UUID",
        TkColumnType.F32 => "a REAL",
        TkColumnType.F64 => "a DOUBLE",
        TkColumnType.Numeric => "a DECIMAL",
        TkColumnType.DateTimeOffset => "a TIMESTAMP",
        TkColumnType.DateTime => "a DATETIME",
        TkColumnType.DateOnly => "a DATE",
        TkColumnType.TimeOnly => "a TIME",
        TkColumnType.TimeSpan => "an INTERVAL",
        TkColumnType.Bytes => "a BLOB",
        _ => $"of type {type}",
    };

    private static async ValueTask<SqlResult> CreateIndexAsync(TkWriteTransaction writer, CreateIndexStatement create, CancellationToken cancellationToken)
    {
        var table = await TableAsync(writer, create.Table, cancellationToken).ConfigureAwait(false);
        if (table.Indexes.Exists(index => string.Equals(index.Name, create.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return create.IfNotExists ? new SqlResult() : throw new TkSqlException($"Table '{table.Name}' already has an index '{create.Name}'.");
        }

        if (table.ReadOnlyReason is { } reason)
        {
            throw new TkSqlException($"An index cannot be added to table '{table.Name}' through SQL: {reason}.");
        }

        var fields = create.Columns.Select(name => table.Column(name) is >= 0 and var at ? at : throw new TkSqlException($"Table '{table.Name}' has no column '{name}'.")).ToArray();
        if (fields.Distinct().Count() != fields.Length)
        {
            throw new TkSqlException($"Index '{create.Name}' names a column twice.");
        }

        byte[] catalogKey;
        try
        {
            catalogKey = CatalogStore.IndexKey(table.TableKey, create.Name);
        }
        catch (ArgumentException invalid)
        {
            throw new TkSqlException($"Index '{create.Name}' cannot have that name: {invalid.Message}", invalid);
        }

        var index = StoredIndex.Create(create.Name, table.Columns, fields, create.Unique);
        index.Entry = new TkWriteTransaction.IndexEntry(catalogKey, index.Definition) { Dirty = true };
        var state = await writer.TableAsync(table.Name, table.TableKey, cancellationToken).ConfigureAwait(false);
        (await writer.IndexesAsync(state, cancellationToken).ConfigureAwait(false)).Add(create.Name, index.Entry);
        table.Indexes.Add(index);
        try
        {
            // Built from the rows there are: each is written again, which gives it its entry.
            var rows = new List<object?[]>();
            await foreach (var row in table.ScanAsync(default, default, cancellationToken).ConfigureAwait(false))
            {
                rows.Add(row);
            }

            foreach (var row in rows)
            {
                await table.IndexRowAsync(index, row, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (TkConstraintException duplicate)
        {
            // Nothing of the index is left behind: the transaction goes on as if it had not been asked for.
            await BTree.DropAsync(writer.Inner, index.CurrentRoot, cancellationToken).ConfigureAwait(false);
            (await writer.IndexesAsync(state, cancellationToken).ConfigureAwait(false)).Remove(create.Name);
            table.Indexes.Remove(index);
            throw new TkSqlException($"Index '{create.Name}' cannot be unique: {duplicate.Message}", duplicate);
        }

        return new SqlResult();
    }

    private static async ValueTask<SqlResult> CreateViewAsync(
        TkWriteTransaction writer, CreateViewStatement create, StatementContext parameters, CancellationToken cancellationToken)
    {
        if (await SqlTable.ResolveAsync(writer, create.Name, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new TkSqlException($"There is already a table '{create.Name}'.");
        }

        var existing = await SelectRunner.ViewAsync(writer, create.Name, cancellationToken).ConfigureAwait(false);
        if (existing is not null && !create.OrReplace)
        {
            throw new TkSqlException($"There is already a view '{create.Name}'. Use CREATE OR REPLACE VIEW to change it.");
        }

        byte[] key;
        try
        {
            key = CatalogStore.ViewKey(existing?.Name ?? create.Name);
        }
        catch (ArgumentException invalid)
        {
            throw new TkSqlException($"A view cannot be named '{create.Name}': {invalid.Message}", invalid);
        }

        // The view is written and then run once: an unknown table or column, or a view that ends up
        // referring to itself through others, is found now and not by whoever queries it later.
        await writer.Database.Tree.PutAsync(writer.Inner, key, Encoding.UTF8.GetBytes(create.Text), cancellationToken).ConfigureAwait(false);
        try
        {
            await new SelectRunner(writer, parameters, cancellationToken)
                .RunAsync(new SelectStatement([new SelectItem(null, null, true, null)], new TableRef(create.Name, create.Name), [], null, [], null, [], new Literal(0L), null, false), null, [])
                .ConfigureAwait(false);
        }
        catch (TkSqlException invalid)
        {
            // Put back as it was: the transaction stays usable.
            if (existing is { } previous)
            {
                await writer.Database.Tree.PutAsync(writer.Inner, key, Encoding.UTF8.GetBytes(previous.Text), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await writer.Database.Tree.DeleteAsync(writer.Inner, key, cancellationToken).ConfigureAwait(false);
            }

            throw new TkSqlException($"View '{create.Name}' cannot be created: {invalid.Message}", invalid);
        }

        return new SqlResult();
    }

    private static async ValueTask<SqlResult> DropAsync(TkWriteTransaction writer, DropStatement drop, CancellationToken cancellationToken)
    {
        var tree = writer.Database.Tree;
        if (drop.Kind == "PROCEDURE")
        {
            if (await StoredAsync(writer, drop.Name, cancellationToken).ConfigureAwait(false) is not var (procedure, _))
            {
                return drop.IfExists ? new SqlResult() : throw new TkSqlException($"There is no procedure '{drop.Name}'.");
            }

            await tree.DeleteAsync(writer.Inner, CatalogStore.ProcedureKey(procedure), cancellationToken).ConfigureAwait(false);
            return new SqlResult();
        }

        if (drop.Kind == "VIEW")
        {
            if (await SelectRunner.ViewAsync(writer, drop.Name, cancellationToken).ConfigureAwait(false) is not { } view)
            {
                return drop.IfExists ? new SqlResult() : throw new TkSqlException($"There is no view '{drop.Name}'.");
            }

            await tree.DeleteAsync(writer.Inner, CatalogStore.ViewKey(view.Name), cancellationToken).ConfigureAwait(false);
            return new SqlResult();
        }

        var name = drop.Kind == "INDEX" ? drop.Table! : drop.Name;
        if (await SqlTable.OpenAsync(writer, name, cancellationToken).ConfigureAwait(false) is not { } table)
        {
            return drop.IfExists ? new SqlResult() : throw new TkSqlException($"There is no table '{name}'.");
        }

        if (table.ReadOnlyReason is { } reason)
        {
            throw new TkSqlException($"Table '{table.Name}' cannot be changed through SQL: {reason}.");
        }

        var state = await writer.TableAsync(table.Name, table.TableKey, cancellationToken).ConfigureAwait(false);
        var indexes = await writer.IndexesAsync(state, cancellationToken).ConfigureAwait(false);
        try
        {
            if (drop.Kind == "INDEX")
            {
                if (table.Indexes.Find(index => string.Equals(index.Name, drop.Name, StringComparison.OrdinalIgnoreCase)) is not { } index)
                {
                    return drop.IfExists ? new SqlResult() : throw new TkSqlException($"Table '{table.Name}' has no index '{drop.Name}'.");
                }

                if (table.ForeignKeys.Find(key => string.Equals(key.Info.Index, index.Name, StringComparison.Ordinal)) is { Info.Name: { } user })
                {
                    throw new TkSqlException($"Index '{index.Name}' of table '{table.Name}' is what FOREIGN KEY '{user}' is checked with: drop the constraint first.");
                }

                await RemoveIndexAsync(index).ConfigureAwait(false);
                return new SqlResult();
            }

            if ((await writer.ReferrersAsync(state, cancellationToken).ConfigureAwait(false)).Find(referrer => !string.Equals(referrer.Table, table.Name, StringComparison.Ordinal)) is { Table: { } other } referrer)
            {
                throw new TkSqlException(
                    $"Table '{table.Name}' cannot be dropped: rows of '{other}' refer to it through FOREIGN KEY '{referrer.Name}'. Drop that constraint or that table first.");
            }

            // The tables it referred to have nobody of this one to ask any more.
            foreach (var parent in table.ForeignKeys.Select(static key => key.Info.Table).Distinct(StringComparer.Ordinal))
            {
                if (!string.Equals(parent, table.Name, StringComparison.Ordinal))
                {
                    await writer.SetReferrersAsync(parent, table.Name, [], cancellationToken).ConfigureAwait(false);
                }
            }

            foreach (var index in table.Indexes)
            {
                await RemoveIndexAsync(index).ConfigureAwait(false);
            }

            await BTree.DropAsync(writer.Inner, state.Root, cancellationToken).ConfigureAwait(false);
            for (var version = 1; version <= table.Version; version++)
            {
                await tree.DeleteAsync(writer.Inner, CatalogStore.SchemaKey(table.TableKey, (byte)version), cancellationToken).ConfigureAwait(false);
            }

            await tree.DeleteAsync(writer.Inner, table.TableKey, cancellationToken).ConfigureAwait(false);
            await tree.DeleteAsync(writer.Inner, CatalogStore.ConstraintsKey(table.TableKey), cancellationToken).ConfigureAwait(false);
            await tree.DeleteAsync(writer.Inner, CatalogStore.ReferrersKey(table.TableKey), cancellationToken).ConfigureAwait(false);

            // The transaction forgets the table: nothing of it is written at commit.
            state.Root = default;
            state.Schema = null;
            state.Changed = false;
            state.UncommittedVersion = 0;
            state.WritesAs = null;
            state.IndexedAs = null;
            state.Bound = [];
            state.Constraints = null;
            state.Referrers = null;
        }
        catch (Exception exception) when (exception is not TkSqlException)
        {
            writer.Fail();
            throw;
        }

        return new SqlResult();

        async ValueTask RemoveIndexAsync(StoredIndex index)
        {
            await BTree.DropAsync(writer.Inner, index.CurrentRoot, cancellationToken).ConfigureAwait(false);
            indexes.Remove(index.Name);
            state.DroppedIndexes.Add(index.Entry!.CatalogKey);
        }
    }
}
