// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Vectors;

/// <summary>Vector search over a <see cref="TekiumDb"/>.</summary>
public static class VectorDatabaseExtensions
{
    /// <summary>
    /// Gets the vector index with this name. Costs nothing: the index is created by its first write, and
    /// from then on must be asked for with the same dimensions, metric, <c>M</c> and quantization.
    /// </summary>
    /// <typeparam name="TKey">The type of the keys: <see cref="Guid"/>, <see cref="int"/>, <see cref="long"/> or <see cref="string"/>.</typeparam>
    /// <param name="database">The database.</param>
    /// <param name="name">The name of the index.</param>
    /// <param name="options">How the index is built.</param>
    /// <returns>The index.</returns>
    public static HnswIndex<TKey> VectorIndex<TKey>(this TekiumDb database, string name, VectorIndexOptions options)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return VectorKey<TKey>.IsSupported
            ? new HnswIndex<TKey>(database, name, options)
            : throw new NotSupportedException($"A vector index cannot be keyed by {typeof(TKey).Name}: use Guid, int, long or string.");
    }
}
