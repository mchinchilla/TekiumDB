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
    private readonly PageNumber _root;
    private readonly long _pin;
    private int _released;

    internal TkReadTransaction(TekiumDb database, ReadTransaction inner)
        : base(database)
    {
        _inner = inner;
        _root = inner.Root;
        SnapshotId = inner.Id.Value;
    }

    /// <summary>
    /// A transaction that reads a past state: the one <paramref name="transaction"/> left, whose catalog
    /// is at <paramref name="root"/>. <paramref name="pin"/> keeps its pages from being reused.
    /// </summary>
    internal TkReadTransaction(TekiumDb database, ReadTransaction inner, PageNumber root, ulong transaction, long pin)
        : base(database)
    {
        _inner = inner;
        _root = root;
        _pin = pin;
        SnapshotId = transaction;
    }

    /// <summary>Gets the transaction whose commit this one reads: everything up to it, and nothing after.</summary>
    public ulong TransactionId => SnapshotId;

    /// <summary>Gets the id of the commit this transaction reads.</summary>
    internal ulong SnapshotId { get; }

    internal override IPageSource Source => _inner;

    internal override PageNumber CatalogRoot => _root;

    /// <summary>Releases the commit this transaction was reading. Safe to call more than once.</summary>
    /// <returns>A task that is already complete.</returns>
    public override ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (_pin != 0 && Interlocked.Exchange(ref _released, 1) == 0)
        {
            Database.Pager.Release(_pin);
        }

        return _inner.DisposeAsync();
    }
}
