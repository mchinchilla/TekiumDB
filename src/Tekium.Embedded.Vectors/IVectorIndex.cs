// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Vectors;

/// <summary>
/// Vectors stored under a key, searchable by nearness. The index lives in the database file and changes
/// in the same transaction as the rows: what a transaction writes to both commits, or not, together.
/// </summary>
/// <typeparam name="TKey">The type of the keys: <see cref="Guid"/>, <see cref="int"/>, <see cref="long"/> or <see cref="string"/>.</typeparam>
public interface IVectorIndex<TKey>
    where TKey : notnull
{
    /// <summary>Stores a vector under a key, in place of the one the key had, if any.</summary>
    /// <param name="transaction">The transaction the change is part of.</param>
    /// <param name="key">The key.</param>
    /// <param name="vector">The vector: as many components as the index declares.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the vector is in the transaction.</returns>
    ValueTask UpsertAsync(
        TkWriteTransaction transaction, TKey key, ReadOnlyMemory<float> vector, CancellationToken cancellationToken = default);

    /// <summary>Removes the vector of a key.</summary>
    /// <param name="transaction">The transaction the change is part of.</param>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether the key had a vector.</returns>
    ValueTask<bool> RemoveAsync(TkWriteTransaction transaction, TKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the index and everything in it, and gives its pages back to the database. The same name can
    /// be used again afterwards, with other options if wanted.
    /// </summary>
    /// <param name="transaction">The transaction the change is part of.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the index is gone from the transaction.</returns>
    ValueTask DropAsync(TkWriteTransaction transaction, CancellationToken cancellationToken = default);

    /// <summary>Finds the vectors nearest to a query among those committed.</summary>
    /// <param name="query">The vector to look near.</param>
    /// <param name="topK">How many to return, at most.</param>
    /// <param name="efSearch">How many candidates the search keeps: higher finds more of the true nearest, more slowly.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The nearest vectors found, nearest first.</returns>
    ValueTask<IReadOnlyList<VectorHit<TKey>>> SearchAsync(
        ReadOnlyMemory<float> query, int topK, int efSearch = 64, CancellationToken cancellationToken = default);

    /// <summary>Finds the vectors nearest to a query as a transaction sees them.</summary>
    /// <param name="transaction">The transaction whose view is searched.</param>
    /// <param name="query">The vector to look near.</param>
    /// <param name="topK">How many to return, at most.</param>
    /// <param name="efSearch">How many candidates the search keeps: higher finds more of the true nearest, more slowly.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The nearest vectors found, nearest first.</returns>
    ValueTask<IReadOnlyList<VectorHit<TKey>>> SearchAsync(
        TkTransaction transaction, ReadOnlyMemory<float> query, int topK, int efSearch = 64, CancellationToken cancellationToken = default);

    /// <summary>Counts the vectors a transaction sees.</summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The number of keys with a vector.</returns>
    ValueTask<long> CountAsync(TkTransaction transaction, CancellationToken cancellationToken = default);
}
