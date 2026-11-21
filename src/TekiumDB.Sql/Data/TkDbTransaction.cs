// Copyright (c) Tekium. Licensed under the MIT License.

using System.Data;
using System.Data.Common;

namespace TekiumDB.Sql;

/// <summary>
/// A write transaction of the database, as ADO.NET sees it. Commit and roll back with
/// <see cref="DbTransaction.CommitAsync(CancellationToken)"/> and <see cref="DbTransaction.RollbackAsync(CancellationToken)"/>;
/// disposing it without committing rolls it back.
/// </summary>
public sealed class TkDbTransaction : DbTransaction
{
    private readonly TkConnection _connection;
    private bool _ended;

    internal TkDbTransaction(TkConnection connection, TkWriteTransaction writer)
    {
        _connection = connection;
        Writer = writer;
    }

    /// <summary>Gets the engine's transaction, to use the typed API in the same transaction as SQL.</summary>
    public TkWriteTransaction Writer { get; }

    /// <inheritdoc/>
    public override IsolationLevel IsolationLevel => IsolationLevel.Snapshot;

    /// <inheritdoc/>
    protected override DbConnection DbConnection => _connection;

    /// <inheritdoc/>
    public override void Commit() => throw TkConnection.Synchronous("CommitAsync");

    /// <inheritdoc/>
    public override void Rollback() => throw TkConnection.Synchronous("RollbackAsync");

    /// <inheritdoc/>
    public override async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_ended, this);
        try
        {
            await Writer.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await EndAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public override Task RollbackAsync(CancellationToken cancellationToken = default) => EndAsync().AsTask();

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        await EndAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Letting go of a write transaction waits for nothing.
            var pending = EndAsync();
            if (!pending.IsCompleted)
            {
                _ = pending.AsTask().ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }

        base.Dispose(disposing);
    }

    private async ValueTask EndAsync()
    {
        if (_ended)
        {
            return;
        }

        _ended = true;
        if (ReferenceEquals(_connection.Current, this))
        {
            _connection.Current = null;
        }

        await Writer.DisposeAsync().ConfigureAwait(false);
    }
}
