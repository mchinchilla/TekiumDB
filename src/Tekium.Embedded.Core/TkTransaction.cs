// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Storage;

namespace Tekium.Embedded;

/// <summary>
/// A view of the database that does not change under whoever holds it: either a read transaction or
/// the write transaction, which also sees its own changes.
/// </summary>
public abstract class TkTransaction : IAsyncDisposable
{
    private protected TkTransaction(TekiumDb database) => Database = database;

    internal TekiumDb Database { get; }

    /// <summary>Gets where pages are read from.</summary>
    internal abstract IPageSource Source { get; }

    /// <summary>Gets the root of the catalog this transaction sees.</summary>
    internal abstract PageNumber CatalogRoot { get; }

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();
}
