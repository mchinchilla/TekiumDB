// Copyright (c) Tekium. Licensed under the MIT License.

using System.Numerics.Tensors;

namespace Tekium.Embedded.Vectors.Tests;

/// <summary>Vectores de prueba y la respuesta exacta, por fuerza bruta, contra la que se mide el índice.</summary>
internal static class VectorData
{
    /// <summary>Vectores agrupados alrededor de centros al azar, como los embeddings reales: no están repartidos por igual.</summary>
    public static float[][] Clustered(int count, int dimensions, int seed, int clusters = 40)
    {
        var random = new Random(seed);
        var centers = Enumerable.Range(0, clusters).Select(_ => Gaussian(random, dimensions, 1f)).ToArray();
        var vectors = new float[count][];
        for (var i = 0; i < count; i++)
        {
            var vector = Gaussian(random, dimensions, 0.35f);
            TensorPrimitives.Add(vector, centers[random.Next(clusters)], vector);
            vectors[i] = vector;
        }

        return vectors;
    }

    public static float[] Gaussian(Random random, int dimensions, float sigma)
    {
        var vector = new float[dimensions];
        for (var i = 0; i < dimensions; i++)
        {
            // Box-Muller.
            var u = 1.0 - random.NextDouble();
            var v = random.NextDouble();
            vector[i] = (float)(Math.Sqrt(-2.0 * Math.Log(u)) * Math.Cos(2.0 * Math.PI * v)) * sigma;
        }

        return vector;
    }

    /// <summary>La distancia que el índice dice usar, calculada sin él.</summary>
    public static float Distance(VectorMetric metric, ReadOnlySpan<float> a, ReadOnlySpan<float> b) => metric switch
    {
        VectorMetric.Euclidean => TensorPrimitives.Distance(a, b),
        VectorMetric.InnerProduct => -TensorPrimitives.Dot(a, b),
        _ => 1f - TensorPrimitives.CosineSimilarity(a, b),
    };

    /// <summary>Los <paramref name="k"/> más cercanos de verdad, comparando contra todos.</summary>
    public static int[] Nearest(VectorMetric metric, IReadOnlyList<float[]?> vectors, float[] query, int k) =>
        [.. Enumerable.Range(0, vectors.Count)
            .Where(i => vectors[i] is not null)
            .Select(i => (Index: i, Distance: Distance(metric, vectors[i], query)))
            .OrderBy(static pair => pair.Distance)
            .ThenBy(static pair => pair.Index)
            .Take(k)
            .Select(static pair => pair.Index)];
}
