// Copyright (c) Tekium. Licensed under the MIT License.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Tekium.Embedded.Sql.Execution;

namespace Tekium.Embedded.Sql;

/// <summary>
/// An ADO.NET connection to a TekiumDB database, for SQL. It opens the file named by its connection
/// string, or uses a database the application already has open.
/// </summary>
/// <remarks>
/// Everything that touches the file is asynchronous: <see cref="DbConnection.OpenAsync(CancellationToken)"/>,
/// <see cref="DbConnection.BeginTransactionAsync(CancellationToken)"/> and the <c>…Async</c> methods of
/// its commands. The synchronous ones throw <see cref="NotSupportedException"/>, because the engine has no
/// synchronous I/O to do them with. A statement run outside a transaction commits by itself.
/// Connection string: <c>Data Source=app.tkdb;Password=…;Cache Size=256;Change Retention=0</c>.
/// </remarks>
public sealed class TkConnection : DbConnection
{
    private readonly bool _owns;
    private TekiumDb? _database;
    private string _connectionString;

    /// <summary>Initializes a new instance of the <see cref="TkConnection"/> class that opens a file.</summary>
    /// <param name="connectionString">Where the file is and how to open it.</param>
    public TkConnection(string connectionString)
    {
        _connectionString = connectionString ?? string.Empty;
        _owns = true;
    }

    /// <summary>Initializes a new instance of the <see cref="TkConnection"/> class over a database that is already open. It is not closed with the connection.</summary>
    /// <param name="database">The database.</param>
    public TkConnection(TekiumDb database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _connectionString = string.Empty;
    }

    /// <inheritdoc/>
    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set => _connectionString = _database is null || !_owns
            ? value ?? string.Empty
            : throw new InvalidOperationException("The connection string cannot change while the connection is open.");
    }

    /// <inheritdoc/>
    public override string Database => "main";

    /// <inheritdoc/>
    public override string DataSource => Setting("Data Source") ?? string.Empty;

    /// <inheritdoc/>
    public override string ServerVersion => typeof(TekiumDb).Assembly.GetName().Version?.ToString() ?? "0";

    /// <inheritdoc/>
    public override ConnectionState State => _database is null ? ConnectionState.Closed : ConnectionState.Open;

    /// <summary>Gets the database the connection runs on.</summary>
    /// <exception cref="InvalidOperationException">The connection is not open.</exception>
    public TekiumDb Db => _database ?? throw new InvalidOperationException("The connection is not open: call OpenAsync first.");

    internal SqlEngine Engine => SqlEngine.For(Db);

    /// <summary>Gets the transaction begun on this connection that has not ended, if any: commands run in it.</summary>
    internal TkDbTransaction? Current { get; set; }

    /// <inheritdoc/>
    public override void Open() => throw Synchronous("OpenAsync");

    /// <inheritdoc/>
    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (_database is not null)
        {
            return;
        }

        var path = Setting("Data Source");
        if (string.IsNullOrEmpty(path))
        {
            throw new InvalidOperationException("The connection string needs a Data Source: the path of the database file, or :memory:.");
        }

        var memory = string.Equals(path, ":memory:", StringComparison.OrdinalIgnoreCase);
        _database = await TekiumDb.OpenAsync(
            new DbOptions
            {
                Path = memory ? null : path,
                InMemory = memory,
                Password = (Setting("Password") ?? string.Empty).AsMemory(),
                CacheSizeMb = Number("Cache Size", 256),
                ChangeRetention = Number("Change Retention", 0),
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override void Close()
    {
        // Closing a file takes no waiting; if it ever did, the asynchronous close is the one to use.
        var pending = CloseAsync();
        if (!pending.IsCompleted)
        {
            _ = pending.ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    /// <inheritdoc/>
    public override async Task CloseAsync()
    {
        if (Current is { } transaction)
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }

        var database = _database;
        if (_owns && database is not null)
        {
            _database = null;
            await database.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException("A TekiumDB file is one database.");

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    internal static NotSupportedException Synchronous(string instead) =>
        new($"TekiumDB does all its I/O asynchronously: use {instead}.");

    /// <inheritdoc/>
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw Synchronous("BeginTransactionAsync");

    /// <inheritdoc/>
    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken)
    {
        if (Current is not null)
        {
            throw new InvalidOperationException("The connection already has a transaction in progress: there is one writer at a time.");
        }

        var writer = await Db.BeginWriteAsync(cancellationToken).ConfigureAwait(false);
        return Current = new TkDbTransaction(this, writer);
    }

    /// <inheritdoc/>
    protected override DbCommand CreateDbCommand() => new TkCommand { Connection = this };

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
        }

        base.Dispose(disposing);
    }

    private string? Setting(string name)
    {
        foreach (var part in _connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && string.Equals(part[..equals].Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return part[(equals + 1)..].Trim();
            }
        }

        return null;
    }

    private int Number(string name, int otherwise) =>
        Setting(name) is not { } text ? otherwise
        : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number
        : throw new InvalidOperationException($"'{name}' in the connection string must be a whole number.");
}
