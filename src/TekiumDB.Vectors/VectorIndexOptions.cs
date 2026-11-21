// Copyright (c) Tekium. Licensed under the MIT License.

namespace TekiumDB.Vectors;

/// <summary>How a vector index is built. Everything but <see cref="EfConstruction"/> is fixed once the index holds vectors.</summary>
public sealed record VectorIndexOptions
{
    /// <summary>Gets the number of components of every vector.</summary>
    public required int Dimensions { get; init; }

    /// <summary>Gets how the distance between two vectors is measured.</summary>
    public VectorMetric Metric { get; init; } = VectorMetric.Cosine;

    /// <summary>Gets the number of neighbours a vector keeps in the upper layers of the graph; twice as many in the lowest.</summary>
    public int M { get; init; } = 16;

    /// <summary>Gets how many candidates are weighed when a vector is linked into the graph: higher gives a better graph, built more slowly.</summary>
    public int EfConstruction { get; init; } = 200;

    /// <summary>
    /// Gets a value indicating whether the graph keeps each component in one byte instead of four. The
    /// original vectors are kept aside and the final candidates of a search are ranked again with them.
    /// </summary>
    public bool Quantize { get; init; }

    /// <summary>Gets the lowest component value a quantized graph tells apart; lower ones count as this.</summary>
    public float QuantizationMin { get; init; } = -1f;

    /// <summary>Gets the highest component value a quantized graph tells apart; higher ones count as this.</summary>
    public float QuantizationMax { get; init; } = 1f;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Dimensions, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Dimensions, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(M, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(M, 100);
        ArgumentOutOfRangeException.ThrowIfLessThan(EfConstruction, 1);
        if (!Enum.IsDefined(Metric))
        {
            throw new ArgumentOutOfRangeException(nameof(Metric), Metric, "Unknown metric.");
        }

        if (!(QuantizationMin < QuantizationMax) || !float.IsFinite(QuantizationMin) || !float.IsFinite(QuantizationMax))
        {
            throw new ArgumentOutOfRangeException(nameof(QuantizationMin), QuantizationMin, "The quantization range must be finite and not empty.");
        }
    }
}
