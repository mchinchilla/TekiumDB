// Copyright (c) Tekium. Licensed under the MIT License.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using TekiumDB.Sql.Execution;

namespace TekiumDB.Sql;

/// <summary>
/// One SQL statement to run on a <see cref="TkConnection"/>. Run it with the <c>…Async</c> methods; the
/// synchronous ones throw. Outside a transaction, a statement that changes data commits by itself and
/// one that reads sees the last commit.
/// </summary>
public sealed class TkCommand : DbCommand
{
    private readonly TkParameterCollection _parameters = [];

    /// <inheritdoc/>
    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    /// <inheritdoc/>
    public override int CommandTimeout { get; set; }

    /// <inheritdoc/>
    public override CommandType CommandType
    {
        get => CommandType.Text;
        set
        {
            if (value != CommandType.Text)
            {
                throw new NotSupportedException("Only text commands are supported.");
            }
        }
    }

    /// <inheritdoc/>
    public override bool DesignTimeVisible { get; set; }

    /// <inheritdoc/>
    public override UpdateRowSource UpdatedRowSource { get; set; }

    /// <summary>Gets the parameters of the statement.</summary>
    public new TkParameterCollection Parameters => _parameters;

    /// <inheritdoc/>
    protected override DbConnection? DbConnection { get; set; }

    /// <inheritdoc/>
    protected override DbParameterCollection DbParameterCollection => _parameters;

    /// <inheritdoc/>
    protected override DbTransaction? DbTransaction { get; set; }

    /// <inheritdoc/>
    public override void Cancel()
    {
    }

    /// <inheritdoc/>
    public override int ExecuteNonQuery() => throw TkConnection.Synchronous("ExecuteNonQueryAsync");

    /// <inheritdoc/>
    public override object? ExecuteScalar() => throw TkConnection.Synchronous("ExecuteScalarAsync");

    /// <inheritdoc/>
    public override void Prepare() => Connected().Engine.Parse(CommandText);

    /// <inheritdoc/>
    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
        (await RunAsync(cancellationToken).ConfigureAwait(false)).RecordsAffected;

    /// <inheritdoc/>
    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(cancellationToken).ConfigureAwait(false);
        return result.Rows.Count > 0 && result.Names.Length > 0 ? result.Rows[0][0] : null;
    }

    /// <inheritdoc/>
    protected override DbParameter CreateDbParameter() => new TkParameter();

    /// <inheritdoc/>
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw TkConnection.Synchronous("ExecuteReaderAsync");

    /// <inheritdoc/>
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) =>
        new TkDataReader(await RunAsync(cancellationToken).ConfigureAwait(false));

    private TkConnection Connected() =>
        DbConnection as TkConnection ?? throw new InvalidOperationException("The command has no TekiumDB connection.");

    private async ValueTask<SqlResult> RunAsync(CancellationToken cancellationToken)
    {
        var connection = Connected();
        var database = connection.Db;
        var statement = connection.Engine.Parse(CommandText);
        var parameters = new StatementContext(_parameters.ToValues(), connection.Engine.Routines);

        // In the transaction given to the command, or else the one in progress on the connection.
        if ((DbTransaction as TkDbTransaction ?? connection.Current) is { } transaction)
        {
            return await SqlEngine.ExecuteAsync(transaction.Writer, statement, parameters, cancellationToken).ConfigureAwait(false);
        }

        if (SqlEngine.ReadsOnly(statement))
        {
            var read = database.BeginRead();
            await using (read.ConfigureAwait(false))
            {
                return await SqlEngine.ExecuteAsync(read, statement, parameters, cancellationToken).ConfigureAwait(false);
            }
        }

        var writer = await database.BeginWriteAsync(cancellationToken).ConfigureAwait(false);
        await using (writer.ConfigureAwait(false))
        {
            var result = await SqlEngine.ExecuteAsync(writer, statement, parameters, cancellationToken).ConfigureAwait(false);
            await writer.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
    }
}
