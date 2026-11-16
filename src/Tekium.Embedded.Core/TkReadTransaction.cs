// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Storage;
using Tekium.Embedded.Transactions;

namespace Tekium.Embedded;

/// <summary>
/// Reads the database as it was at one commit, for as long as it lives. It never waits for the writer
/// and the writer never waits for it. Dispose it: while it lives the space of that commit cannot be
/// reused, so a forgotten one makes the file grow.
/// </summary>
public sealed class TkReadTransaction : TkTransaction
{
    private readonly ReadTransaction _inner;

    internal TkReadTransaction(TekiumDb database, ReadTransaction inner)
        : base(database)
    {
        _inner = inner;
        SnapshotId = inner.Id.Value;
    }

    /// <summary>Gets the id of the commit this transaction reads.</summary>
    internal ulong SnapshotId { get; }

    internal override IPageSource Source => _inner;

    internal override PageNumber CatalogRoot => _inner.Root;

    /// <summary>Releases the commit this transaction was reading. Safe to call more than once.</summary>
    /// <returns>A task that is already complete.</returns>
    public override ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return _inner.DisposeAsync();
    }
}
