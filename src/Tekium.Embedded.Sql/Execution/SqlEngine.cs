// Copyright (c) Tekium. Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Text;
using Tekium.Embedded.Catalog;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Sql.Parsing;
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

    private readonly ConcurrentDictionary<string, SqlStatement> _statements = new(StringComparer.Ordinal);

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
        TkTransaction transaction, SqlStatement statement, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
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
                    _ => throw new TkSqlException("Unsupported statement."),
                };
        }
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
        TkWriteTransaction writer, InsertStatement insert, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
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

        foreach (var row in rows)
        {
            await table.InsertAsync(row, cancellationToken).ConfigureAwait(false);
        }

        return new SqlResult { RecordsAffected = rows.Count };
    }

    private static async ValueTask<SqlResult> UpdateAsync(
        TkWriteTransaction writer, UpdateStatement update, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        var table = await TableAsync(writer, update.Table, cancellationToken).ConfigureAwait(false);
        var runner = new SelectRunner(writer, parameters, cancellationToken);

        // Every row to change is found before any is changed: the search never sees its own writes.
        var matching = await runner.MatchingAsync(table, update.Where).ConfigureAwait(false);
        var assignments = update.Set.Select(set => (
            Column: table.Column(set.Column) is >= 0 and var at ? at : throw new TkSqlException($"Table '{table.Name}' has no column '{set.Column}'."),
            Value: Binder.Bind(set.Value, runner.Resolve, parameters, null))).ToArray();
        foreach (var old in matching)
        {
            var row = (object?[])old.Clone();
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

        return new SqlResult { RecordsAffected = matching.Count };
    }

    private static async ValueTask<SqlResult> DeleteAsync(
        TkWriteTransaction writer, DeleteStatement delete, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        var table = await TableAsync(writer, delete.Table, cancellationToken).ConfigureAwait(false);
        var matching = await new SelectRunner(writer, parameters, cancellationToken).MatchingAsync(table, delete.Where).ConfigureAwait(false);
        foreach (var row in matching)
        {
            await table.DeleteAsync(row, cancellationToken).ConfigureAwait(false);
        }

        return new SqlResult { RecordsAffected = matching.Count };
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

        return new SqlResult();
    }

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
        TkWriteTransaction writer, CreateViewStatement create, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
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

                await RemoveIndexAsync(index).ConfigureAwait(false);
                return new SqlResult();
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
