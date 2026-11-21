// Copyright (c) Tekium. Licensed under the MIT License.

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using Microsoft.Extensions.DependencyInjection;
using TekiumDB.Sql;

namespace TekiumDB.EntityFrameworkCore;

/// <summary>Registers the services Entity Framework Core needs to work over TekiumDB.</summary>
public static class TkServiceCollectionExtensions
{
    /// <summary>Adds the services of the TekiumDB provider. <c>UseTekium</c> calls it; an application seldom needs to.</summary>
    /// <param name="services">The services.</param>
    /// <returns>The same services.</returns>
    public static IServiceCollection AddEntityFrameworkTekium(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        new EntityFrameworkRelationalServicesBuilder(services)
            .TryAdd<LoggingDefinitions, TkLoggingDefinitions>()
            .TryAdd<IDatabaseProvider, DatabaseProvider<TkOptionsExtension>>()
            .TryAdd<IRelationalTypeMappingSource, TkTypeMappingSource>()
            .TryAdd<ISqlGenerationHelper, RelationalSqlGenerationHelper>()
            .TryAdd<IRelationalAnnotationProvider, RelationalAnnotationProvider>()
            .TryAdd<IModelValidator, RelationalModelValidator>()
            .TryAdd<IProviderConventionSetBuilder, TkConventionSetBuilder>()
            .TryAdd<IUpdateSqlGenerator, TkUpdateSqlGenerator>()
            .TryAdd<IModificationCommandBatchFactory, TkModificationCommandBatchFactory>()
            .TryAdd<IRelationalConnection, TkRelationalConnection>()
            .TryAdd<IRelationalTransactionFactory, TkRelationalTransactionFactory>()
            .TryAdd<IRelationalDatabaseCreator, TkDatabaseCreator>()
            .TryAdd<IHistoryRepository, TkHistoryRepository>()
            .TryAdd<IMigrationsSqlGenerator, TkMigrationsSqlGenerator>()
            .TryAdd<IQuerySqlGeneratorFactory, TkQuerySqlGeneratorFactory>()
            .TryAdd<IMethodCallTranslatorProvider, TkMethodCallTranslatorProvider>()
            .TryAdd<IMemberTranslatorProvider, TkMemberTranslatorProvider>()
            .TryAddCoreServices();
        return services;
    }
}

/// <summary>The log messages of the provider: those of any relational one.</summary>
public sealed class TkLoggingDefinitions : RelationalLoggingDefinitions;

/// <summary>The conventions a model is built with: those of any relational provider.</summary>
public sealed class TkConventionSetBuilder(ProviderConventionSetBuilderDependencies dependencies, RelationalConventionSetBuilderDependencies relationalDependencies)
    : RelationalConventionSetBuilder(dependencies, relationalDependencies);

/// <summary>The connection of a context: a <see cref="TkConnection"/>.</summary>
public sealed class TkRelationalConnection(RelationalConnectionDependencies dependencies) : RelationalConnection(dependencies)
{
    /// <inheritdoc/>
    protected override bool SupportsAmbientTransactions => false;

    /// <inheritdoc/>
    protected override DbConnection CreateDbConnection() => new TkConnection(GetValidatedConnectionString());
}

/// <summary>Creates the transactions of a context: without savepoints, which TekiumDB does not have.</summary>
public sealed class TkRelationalTransactionFactory(RelationalTransactionFactoryDependencies dependencies) : IRelationalTransactionFactory
{
    /// <inheritdoc/>
    public RelationalTransaction Create(
        IRelationalConnection connection,
        DbTransaction transaction,
        Guid transactionId,
        IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger,
        bool transactionOwned) => new Transaction(connection, transaction, transactionId, logger, transactionOwned, dependencies.SqlGenerationHelper);

    private sealed class Transaction(
        IRelationalConnection connection,
        DbTransaction transaction,
        Guid transactionId,
        IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger,
        bool transactionOwned,
        ISqlGenerationHelper sqlGenerationHelper) : RelationalTransaction(connection, transaction, transactionId, logger, transactionOwned, sqlGenerationHelper)
    {
        // Without one, a SaveChangesAsync that fails inside a transaction of the application cannot undo only itself: the transaction has to be rolled back.
        public override bool SupportsSavepoints => false;
    }
}

/// <summary>Creates the writer of queries.</summary>
public sealed class TkQuerySqlGeneratorFactory(QuerySqlGeneratorDependencies dependencies) : IQuerySqlGeneratorFactory
{
    /// <inheritdoc/>
    public QuerySqlGenerator Create() => new TkQuerySqlGenerator(dependencies);
}

/// <summary>Writes INSERT, UPDATE and DELETE; what the database computed comes back through RETURNING.</summary>
public sealed class TkUpdateSqlGenerator(UpdateSqlGeneratorDependencies dependencies) : UpdateSqlGenerator(dependencies);

/// <summary>One statement per command: TekiumDB runs one statement at a time.</summary>
public sealed class TkModificationCommandBatchFactory(ModificationCommandBatchFactoryDependencies dependencies) : IModificationCommandBatchFactory
{
    /// <inheritdoc/>
    public ModificationCommandBatch Create() => new SingularModificationCommandBatch(dependencies);
}

/// <summary>Creates the tables of a model. The file itself is created by opening it.</summary>
public sealed class TkDatabaseCreator(RelationalDatabaseCreatorDependencies dependencies, IRawSqlCommandBuilder rawSqlCommandBuilder) : RelationalDatabaseCreator(dependencies)
{
    private const string AsyncOnly = "TekiumDB does no synchronous I/O: use the Async method (EnsureCreatedAsync, EnsureDeletedAsync).";

    /// <inheritdoc/>
    public override bool Exists() => true;

    /// <inheritdoc/>
    public override bool HasTables() => throw new NotSupportedException(AsyncOnly);

    /// <inheritdoc/>
    public override void Create()
    {
    }

    /// <inheritdoc/>
    public override void Delete() => throw new NotSupportedException(AsyncOnly);

    /// <inheritdoc/>
    public override Task CreateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public override async Task<bool> HasTablesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var table in Tables())
        {
            if (await ExistsAsync(table, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Drops the tables of the model and the history of migrations. The file stays: it may hold what is not of this model.</summary>
    /// <inheritdoc/>
    public override async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        var helper = Dependencies.SqlGenerationHelper;
        var model = Dependencies.CurrentContext.Context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var existing = new List<ITable>();
        foreach (var table in model.Tables)
        {
            if (await ExistsAsync(table.Name, cancellationToken).ConfigureAwait(false))
            {
                existing.Add(table);
            }
        }

        // A table that others refer to cannot be dropped: the foreign keys go first, then the tables in any order.
        foreach (var table in existing)
        {
            foreach (var foreignKey in table.ForeignKeyConstraints)
            {
                await RunAsync($"ALTER TABLE {helper.DelimitIdentifier(table.Name)} DROP CONSTRAINT IF EXISTS {helper.DelimitIdentifier(foreignKey.Name)}", cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var table in existing)
        {
            await RunAsync($"DROP TABLE {helper.DelimitIdentifier(table.Name)}", cancellationToken).ConfigureAwait(false);
        }

        await RunAsync($"DROP TABLE IF EXISTS {helper.DelimitIdentifier(HistoryRepository.DefaultTableName)}", cancellationToken).ConfigureAwait(false);
    }

    private IEnumerable<string> Tables() =>
        Dependencies.CurrentContext.Context.GetService<IDesignTimeModel>().Model.GetRelationalModel().Tables.Select(static table => table.Name);

    /// <summary>Whether the database has a table: it is asked for a row of it.</summary>
    internal async Task<bool> ExistsAsync(string table, CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync($"SELECT 1 FROM {Dependencies.SqlGenerationHelper.DelimitIdentifier(table)} LIMIT 1", cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TkSqlException)
        {
            return false;
        }
    }

    private Task<int> RunAsync(string sql, CancellationToken cancellationToken) =>
        rawSqlCommandBuilder.Build(sql).ExecuteNonQueryAsync(
            new RelationalCommandParameterObject(
                Dependencies.Connection, null, null, Dependencies.CurrentContext.Context, Dependencies.CommandLogger, CommandSource.Migrations),
            cancellationToken);
}

/// <summary>
/// Writes the operations of a migration as TekiumDB reads them: tables, columns, indexes and foreign keys,
/// and their names. Dropping, renaming or changing a column writes every row of its table again inside the
/// migration's transaction. What the engine does not have is refused by name.
/// </summary>
public sealed class TkMigrationsSqlGenerator(MigrationsSqlGeneratorDependencies dependencies) : MigrationsSqlGenerator(dependencies)
{
    private const string Can = "A migration over TekiumDB can create, rename and drop tables; add, rename, change and drop columns; create, rename and drop indexes; and add and drop foreign keys.";

    /// <inheritdoc/>
    public override IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations, IModel? model = null, MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        // A foreign key is checked with an index that starts with its column, and makes one if there is none.
        // The ones declared with a table wait until the tables and the indexes of the migration are there:
        // they use the index EF creates for them, and may refer to a table created later.
        var ordered = new List<MigrationOperation>(operations);

        // EF drops the primary key of a table before renaming it and adds it again after. The key of a
        // table does not change: the pair is left out, and what is added is checked to be the key it has.
        var renamed = ordered.OfType<RenameTableOperation>().Where(static rename => rename.NewName is not null)
            .ToDictionary(static rename => rename.Name, static rename => rename.NewName!, StringComparer.Ordinal);
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i] is not DropPrimaryKeyOperation drop)
            {
                continue;
            }

            var table = renamed.GetValueOrDefault(drop.Table, drop.Table);
            if (!ordered.Skip(i + 1).Any(operation => operation is AddPrimaryKeyOperation add && (add.Table == drop.Table || add.Table == table)))
            {
                throw Unsupported($"drop the primary key of table '{drop.Table}': the key of a table does not change");
            }

            ordered.RemoveAt(i--);
        }

        var last = ordered.FindLastIndex(static operation => operation is CreateTableOperation or CreateIndexOperation);
        if (last >= 0)
        {
            ordered.InsertRange(last + 1, ordered.OfType<CreateTableOperation>().SelectMany(static table => table.ForeignKeys).ToList());
        }

        return base.Generate(ordered, model, options);
    }

    /// <inheritdoc/>
    protected override void Generate(CreateTableOperation operation, IModel? model, MigrationCommandListBuilder builder, bool terminate = true)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);
        if (operation.PrimaryKey is not { Columns.Length: 1 } key)
        {
            throw new NotSupportedException($"Table '{operation.Name}' needs a primary key of exactly one column: TekiumDB has no keys of several columns, and no tables without one.");
        }

        if (operation.CheckConstraints.Count > 0)
        {
            throw Unsupported($"create CHECK constraint '{operation.CheckConstraints[0].Name}' of table '{operation.Name}': SQL has no CHECK constraints");
        }

        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("CREATE TABLE ").Append(helper.DelimitIdentifier(operation.Name)).AppendLine(" (");
        using (builder.Indent())
        {
            for (var i = 0; i < operation.Columns.Count; i++)
            {
                var column = operation.Columns[i];
                if (column.DefaultValue is not null || column.DefaultValueSql is not null || column.ComputedColumnSql is not null)
                {
                    throw Unsupported($"give column '{column.Name}' of table '{operation.Name}' a default or computed value: a column keeps none, so set the value in the application");
                }

                builder.Append(helper.DelimitIdentifier(column.Name)).Append(" ").Append(column.ColumnType ?? GetColumnType(operation.Schema, operation.Name, column.Name, column, model)!);
                if (!column.IsNullable)
                {
                    builder.Append(" NOT NULL");
                }

                if (column.Name == key.Columns[0])
                {
                    builder.Append(" PRIMARY KEY");
                }

                builder.AppendLine(i < operation.Columns.Count - 1 ? "," : string.Empty);
            }
        }

        builder.Append(")");
        if (terminate)
        {
            builder.AppendLine(helper.StatementTerminator);
            EndStatement(builder);
        }

        // An alternate key is a unique index. The foreign keys come after every table and index of the migration.
        foreach (var unique in operation.UniqueConstraints)
        {
            Generate(unique, model, builder);
        }
    }

    /// <inheritdoc/>
    protected override void Generate(AddColumnOperation operation, IModel? model, MigrationCommandListBuilder builder, bool terminate = true)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);
        if (operation.DefaultValueSql is not null)
        {
            throw Unsupported($"add column '{operation.Name}' of table '{operation.Table}' with DefaultValueSql: the value of the rows there are must be written out (defaultValue)");
        }

        if (operation.ComputedColumnSql is not null)
        {
            throw Unsupported($"add computed column '{operation.Name}' of table '{operation.Table}'");
        }

        var helper = Dependencies.SqlGenerationHelper;
        var type = operation.ColumnType ?? GetColumnType(operation.Schema, operation.Table, operation.Name, operation, model)!;
        builder.Append("ALTER TABLE ").Append(helper.DelimitIdentifier(operation.Table)).Append(" ADD COLUMN ").Append(helper.DelimitIdentifier(operation.Name)).Append(" ").Append(type);
        if (!operation.IsNullable)
        {
            builder.Append(" NOT NULL");
        }

        // The rows there are take it. It is not kept for the rows to come: EF gives every column a value.
        if (operation.DefaultValue is { } value)
        {
            var mapping = Dependencies.TypeMappingSource.FindMapping(value.GetType(), type) ?? Dependencies.TypeMappingSource.GetMappingForValue(value);
            builder.Append(" DEFAULT ").Append(mapping.GenerateSqlLiteral(value));
        }

        End(builder, terminate);
    }

    /// <inheritdoc/>
    protected override void Generate(AddForeignKeyOperation operation, IModel? model, MigrationCommandListBuilder builder, bool terminate = true)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);
        if (operation.Columns.Length != 1 || operation.PrincipalColumns is { Length: not 1 })
        {
            throw new NotSupportedException(
                $"FOREIGN KEY '{operation.Name}' of table '{operation.Table}' is of {operation.Columns.Length} columns: in TekiumDB a foreign key is one column that refers to the primary key of a table.");
        }

        // What happens on delete is not written: here a foreign key only refuses. EF still deletes the dependents it has loaded.
        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("ALTER TABLE ").Append(helper.DelimitIdentifier(operation.Table)).Append(" ADD CONSTRAINT ").Append(helper.DelimitIdentifier(operation.Name))
            .Append(" FOREIGN KEY (").Append(helper.DelimitIdentifier(operation.Columns[0])).Append(") REFERENCES ").Append(helper.DelimitIdentifier(operation.PrincipalTable));
        if (operation.PrincipalColumns is { } principal)
        {
            builder.Append(" (").Append(helper.DelimitIdentifier(principal[0])).Append(")");
        }

        End(builder, terminate);
    }

    /// <inheritdoc/>
    protected override void Generate(DropForeignKeyOperation operation, IModel? model, MigrationCommandListBuilder builder, bool terminate = true)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);
        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("ALTER TABLE ").Append(helper.DelimitIdentifier(operation.Table)).Append(" DROP CONSTRAINT ").Append(helper.DelimitIdentifier(operation.Name));
        End(builder, terminate);
    }

    /// <inheritdoc/>
    protected override void Generate(CreateIndexOperation operation, IModel? model, MigrationCommandListBuilder builder, bool terminate = true)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);
        if (operation.Filter is not null)
        {
            throw Unsupported($"create index '{operation.Name}' of table '{operation.Table}' with a filter: SQL creates indexes over every row");
        }

        if (operation.IsDescending is { Length: > 0 })
        {
            throw Unsupported($"create index '{operation.Name}' of table '{operation.Table}' in descending order");
        }

        Index(builder, operation.Name, operation.Table, operation.Columns, operation.IsUnique, terminate);
    }

    /// <inheritdoc/>
    protected override void Generate(AddUniqueConstraintOperation operation, IModel? model, MigrationCommandListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);
        Index(builder, operation.Name, operation.Table, operation.Columns, true, true);
    }

    /// <inheritdoc/>
    protected override void Generate(DropUniqueConstraintOperation operation, IModel? model, MigrationCommandListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);
        DropIndex(builder, operation.Name, operation.Table, true);
    }

    /// <inheritdoc/>
    protected override void Generate(DropIndexOperation operation, IModel? model, MigrationCommandListBuilder builder, bool terminate = true)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);
        DropIndex(builder, operation.Name, operation.Table ?? throw Unsupported($"drop index '{operation.Name}' without the name of its table"), terminate);
    }

    /// <inheritdoc/>
    protected override void Generate(DropColumnOperation operation, IModel? model, MigrationCommandListBuilder builder, bool terminate = true)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);
        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("ALTER TABLE ").Append(helper.DelimitIdentifier(operation.Table)).Append(" DROP COLUMN ").Append(helper.DelimitIdentifier(operation.Name));
        End(builder, terminate);
    }

    /// <inheritdoc/>
    protected override void Generate(RenameColumnOperation operation, IModel? model, MigrationCommandListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);
        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("ALTER TABLE ").Append(helper.DelimitIdentifier(operation.Table)).Append(" RENAME COLUMN ").Append(helper.DelimitIdentifier(operation.Name))
            .Append(" TO ").Append(helper.DelimitIdentifier(operation.NewName));
        End(builder, true);
    }

    /// <summary>
    /// A new type or nullability for a column. Nothing is written when neither changes. A column that can no
    /// longer be NULL gives the rows that have NULL in it the default of the operation, or the zero of its
    /// type, which is what the SQLite provider does when it writes the table again.
    /// </summary>
    /// <inheritdoc/>
    protected override void Generate(AlterColumnOperation operation, IModel? model, MigrationCommandListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);
        if (operation.DefaultValueSql is not null || operation.ComputedColumnSql is not null)
        {
            throw Unsupported($"give column '{operation.Name}' of table '{operation.Table}' a default or computed value: a column keeps none, so set the value in the application");
        }

        var type = operation.ColumnType ?? GetColumnType(operation.Schema, operation.Table, operation.Name, operation, model)!;
        var old = operation.OldColumn.ColumnType ?? GetColumnType(operation.Schema, operation.Table, operation.Name, operation.OldColumn, model);
        if (string.Equals(type, old, StringComparison.OrdinalIgnoreCase) && operation.IsNullable == operation.OldColumn.IsNullable)
        {
            return;
        }

        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("ALTER TABLE ").Append(helper.DelimitIdentifier(operation.Table)).Append(" ALTER COLUMN ").Append(helper.DelimitIdentifier(operation.Name))
            .Append(" ").Append(type).Append(operation.IsNullable ? " NULL" : " NOT NULL");
        if (!operation.IsNullable && operation.OldColumn.IsNullable)
        {
            var value = operation.DefaultValue ?? Zero(Nullable.GetUnderlyingType(operation.ClrType) ?? operation.ClrType);
            var mapping = Dependencies.TypeMappingSource.FindMapping(value.GetType(), type) ?? Dependencies.TypeMappingSource.GetMappingForValue(value);
            builder.Append(" DEFAULT ").Append(mapping.GenerateSqlLiteral(value));
        }

        End(builder, true);

        static object Zero(Type type) =>
            type == typeof(string) ? string.Empty
            : type == typeof(byte[]) ? Array.Empty<byte>()
            : Activator.CreateInstance(type) ?? throw new NotSupportedException($"Column of type {type} has no value for the rows that had NULL.");
    }

    /// <inheritdoc/>
    protected override void Generate(RenameTableOperation operation, IModel? model, MigrationCommandListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);
        if (operation.NewSchema is not null && operation.NewSchema != operation.Schema)
        {
            throw Unsupported("use schemas");
        }

        if (operation.NewName is null || operation.NewName == operation.Name)
        {
            return;
        }

        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("ALTER TABLE ").Append(helper.DelimitIdentifier(operation.Name)).Append(" RENAME TO ").Append(helper.DelimitIdentifier(operation.NewName));
        End(builder, true);
    }

    /// <inheritdoc/>
    protected override void Generate(RenameIndexOperation operation, IModel? model, MigrationCommandListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);
        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("ALTER INDEX ").Append(helper.DelimitIdentifier(operation.Name)).Append(" ON ")
            .Append(helper.DelimitIdentifier(operation.Table ?? throw Unsupported($"rename index '{operation.Name}' without the name of its table")))
            .Append(" RENAME TO ").Append(helper.DelimitIdentifier(operation.NewName));
        End(builder, true);
    }

    /// <summary>The key of a table does not change: this is accepted only when the column already is the key.</summary>
    /// <inheritdoc/>
    protected override void Generate(AddPrimaryKeyOperation operation, IModel? model, MigrationCommandListBuilder builder, bool terminate = true)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);
        if (operation.Columns.Length != 1)
        {
            throw Unsupported($"give table '{operation.Table}' a primary key of {operation.Columns.Length} columns");
        }

        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("ALTER TABLE ").Append(helper.DelimitIdentifier(operation.Table)).Append(" ADD PRIMARY KEY (").Append(helper.DelimitIdentifier(operation.Columns[0])).Append(")");
        End(builder, terminate);
    }

    /// <inheritdoc/>
    protected override void Generate(DropPrimaryKeyOperation operation, IModel? model, MigrationCommandListBuilder builder, bool terminate = true) =>
        throw Unsupported($"drop the primary key of table '{operation?.Table}': the key of a table does not change");

    /// <inheritdoc/>
    protected override void Generate(AddCheckConstraintOperation operation, IModel? model, MigrationCommandListBuilder builder) =>
        throw Unsupported($"create CHECK constraint '{operation?.Name}' of table '{operation?.Table}': SQL has no CHECK constraints");

    /// <inheritdoc/>
    protected override void Generate(DropCheckConstraintOperation operation, IModel? model, MigrationCommandListBuilder builder) =>
        throw Unsupported($"drop CHECK constraint '{operation?.Name}' of table '{operation?.Table}': SQL has no CHECK constraints");

    /// <inheritdoc/>
    protected override void Generate(CreateSequenceOperation operation, IModel? model, MigrationCommandListBuilder builder) => throw Unsupported("use sequences");

    /// <inheritdoc/>
    protected override void Generate(AlterSequenceOperation operation, IModel? model, MigrationCommandListBuilder builder) => throw Unsupported("use sequences");

    /// <inheritdoc/>
    protected override void Generate(DropSequenceOperation operation, IModel? model, MigrationCommandListBuilder builder) => throw Unsupported("use sequences");

    /// <inheritdoc/>
    protected override void Generate(RenameSequenceOperation operation, IModel? model, MigrationCommandListBuilder builder) => throw Unsupported("use sequences");

    /// <inheritdoc/>
    protected override void Generate(RestartSequenceOperation operation, IModel? model, MigrationCommandListBuilder builder) => throw Unsupported("use sequences");

    /// <inheritdoc/>
    protected override void Generate(EnsureSchemaOperation operation, IModel? model, MigrationCommandListBuilder builder) => throw Unsupported("use schemas");

    /// <inheritdoc/>
    protected override void Generate(DropSchemaOperation operation, IModel? model, MigrationCommandListBuilder builder) => throw Unsupported("use schemas");

    private static NotSupportedException Unsupported(string what) => new($"TekiumDB cannot {what}. {Can}");

    private void Index(MigrationCommandListBuilder builder, string name, string table, string[] columns, bool unique, bool terminate)
    {
        var helper = Dependencies.SqlGenerationHelper;
        builder.Append(unique ? "CREATE UNIQUE INDEX " : "CREATE INDEX ").Append(helper.DelimitIdentifier(name)).Append(" ON ").Append(helper.DelimitIdentifier(table))
            .Append(" (").Append(string.Join(", ", columns.Select(helper.DelimitIdentifier))).Append(")");
        End(builder, terminate);
    }

    private void DropIndex(MigrationCommandListBuilder builder, string name, string table, bool terminate)
    {
        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("DROP INDEX ").Append(helper.DelimitIdentifier(name)).Append(" ON ").Append(helper.DelimitIdentifier(table));
        End(builder, terminate);
    }

    private void End(MigrationCommandListBuilder builder, bool terminate)
    {
        if (terminate)
        {
            builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
            EndStatement(builder);
        }
    }
}

/// <summary>
/// The history of migrations: a table like any other. One process uses a database, so there is nobody
/// else to keep out while migrating, and the lock is nothing.
/// </summary>
public sealed class TkHistoryRepository(HistoryRepositoryDependencies dependencies) : HistoryRepository(dependencies)
{
    private const string AsyncOnly = "TekiumDB does no synchronous I/O: apply migrations with Database.MigrateAsync().";
    private const string NoScripts = "TekiumDB cannot run an idempotent script: its SQL has nothing to decide with. Apply migrations with Database.MigrateAsync().";

    /// <inheritdoc/>
    public override LockReleaseBehavior LockReleaseBehavior => LockReleaseBehavior.Explicit;

    /// <inheritdoc/>
    protected override string ExistsSql => $"SELECT 1 FROM {SqlGenerationHelper.DelimitIdentifier(TableName)} LIMIT 1";

    /// <inheritdoc/>
    public override bool Exists() => throw new NotSupportedException(AsyncOnly);

    /// <inheritdoc/>
    public override Task<bool> ExistsAsync(CancellationToken cancellationToken = default) =>
        ((TkDatabaseCreator)Dependencies.DatabaseCreator).ExistsAsync(TableName, cancellationToken);

    /// <inheritdoc/>
    public override string GetCreateIfNotExistsScript()
    {
        var script = GetCreateScript();
        const string create = "CREATE TABLE";
        return script.Insert(script.IndexOf(create, StringComparison.Ordinal) + create.Length, " IF NOT EXISTS");
    }

    /// <inheritdoc/>
    public override string GetBeginIfNotExistsScript(string migrationId) => throw new NotSupportedException(NoScripts);

    /// <inheritdoc/>
    public override string GetBeginIfExistsScript(string migrationId) => throw new NotSupportedException(NoScripts);

    /// <inheritdoc/>
    public override string GetEndIfScript() => throw new NotSupportedException(NoScripts);

    /// <inheritdoc/>
    public override IMigrationsDatabaseLock AcquireDatabaseLock() => throw new NotSupportedException(AsyncOnly);

    /// <inheritdoc/>
    public override Task<IMigrationsDatabaseLock> AcquireDatabaseLockAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IMigrationsDatabaseLock>(new NoLock(this));

    /// <inheritdoc/>
    protected override bool InterpretExistsResult(object? value) => value is not null;

    private sealed class NoLock(IHistoryRepository historyRepository) : IMigrationsDatabaseLock
    {
        public IHistoryRepository HistoryRepository => historyRepository;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => default;
    }
}
