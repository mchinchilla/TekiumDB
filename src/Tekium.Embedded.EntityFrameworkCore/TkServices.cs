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
using Tekium.Embedded.Sql;

namespace Tekium.Embedded.EntityFrameworkCore;

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

    /// <summary>Drops the tables of the model. The file stays: it may hold what is not of this model.</summary>
    /// <inheritdoc/>
    public override async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        foreach (var table in Tables())
        {
            if (await ExistsAsync(table, cancellationToken).ConfigureAwait(false))
            {
                await RunAsync($"DROP TABLE {Dependencies.SqlGenerationHelper.DelimitIdentifier(table)}", cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private IEnumerable<string> Tables() =>
        Dependencies.CurrentContext.Context.GetService<IDesignTimeModel>().Model.GetRelationalModel().Tables.Select(static table => table.Name);

    private async Task<bool> ExistsAsync(string table, CancellationToken cancellationToken)
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

/// <summary>Writes CREATE TABLE and CREATE INDEX as TekiumDB reads them.</summary>
public sealed class TkMigrationsSqlGenerator(MigrationsSqlGeneratorDependencies dependencies) : MigrationsSqlGenerator(dependencies)
{
    /// <inheritdoc/>
    protected override void Generate(CreateTableOperation operation, IModel? model, MigrationCommandListBuilder builder, bool terminate = true)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);
        if (operation.PrimaryKey is not { Columns.Length: 1 } key)
        {
            throw new NotSupportedException($"Table '{operation.Name}' needs a primary key of exactly one column: TekiumDB has no keys of several columns, and no tables without one.");
        }

        var helper = Dependencies.SqlGenerationHelper;
        builder.Append("CREATE TABLE ").Append(helper.DelimitIdentifier(operation.Name)).AppendLine(" (");
        using (builder.Indent())
        {
            for (var i = 0; i < operation.Columns.Count; i++)
            {
                var column = operation.Columns[i];
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

        // An alternate key is a unique index. Foreign keys are not created: TekiumDB does not enforce them from SQL.
        foreach (var unique in operation.UniqueConstraints)
        {
            builder.Append("CREATE UNIQUE INDEX ").Append(helper.DelimitIdentifier(unique.Name)).Append(" ON ").Append(helper.DelimitIdentifier(operation.Name))
                .Append(" (").Append(string.Join(", ", unique.Columns.Select(helper.DelimitIdentifier))).Append(")").AppendLine(helper.StatementTerminator);
            EndStatement(builder);
        }
    }
}

/// <summary>Migrations are not supported: the tables of a model are created with <c>EnsureCreatedAsync</c>.</summary>
public sealed class TkHistoryRepository(HistoryRepositoryDependencies dependencies) : HistoryRepository(dependencies)
{
    private const string Unsupported = "TekiumDB has no migrations: create the tables of the model with Database.EnsureCreatedAsync().";

    /// <inheritdoc/>
    protected override string ExistsSql => throw new NotSupportedException(Unsupported);

    /// <inheritdoc/>
    protected override bool InterpretExistsResult(object? value) => throw new NotSupportedException(Unsupported);

    /// <inheritdoc/>
    public override string GetCreateIfNotExistsScript() => throw new NotSupportedException(Unsupported);

    /// <inheritdoc/>
    public override string GetBeginIfNotExistsScript(string migrationId) => throw new NotSupportedException(Unsupported);

    /// <inheritdoc/>
    public override string GetBeginIfExistsScript(string migrationId) => throw new NotSupportedException(Unsupported);

    /// <inheritdoc/>
    public override string GetEndIfScript() => throw new NotSupportedException(Unsupported);

    /// <inheritdoc/>
    public override LockReleaseBehavior LockReleaseBehavior => LockReleaseBehavior.Explicit;

    /// <inheritdoc/>
    public override IMigrationsDatabaseLock AcquireDatabaseLock() => throw new NotSupportedException(Unsupported);

    /// <inheritdoc/>
    public override Task<IMigrationsDatabaseLock> AcquireDatabaseLockAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException(Unsupported);
}
