// Copyright (c) Tekium. Licensed under the MIT License.

namespace TekiumDB.Vectors;

/// <summary>A vector found by a search.</summary>
/// <typeparam name="TKey">The type of the keys of the index.</typeparam>
/// <param name="Key">The key the vector was stored under.</param>
/// <param name="Distance">How far it is from the query, by the metric of the index: the lower, the closer.</param>
public readonly record struct VectorHit<TKey>(TKey Key, float Distance);
