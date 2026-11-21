// Copyright (c) Tekium. Licensed under the MIT License.

namespace TekiumDB.Vectors;

/// <summary>How far apart two vectors are. Fixed when the index is created.</summary>
public enum VectorMetric
{
    /// <summary>One minus the cosine of the angle between them. Vectors are normalized when they are stored.</summary>
    Cosine = 0,

    /// <summary>The straight-line distance between them.</summary>
    Euclidean = 1,

    /// <summary>The negated inner product: the larger the product, the closer.</summary>
    InnerProduct = 2,
}
