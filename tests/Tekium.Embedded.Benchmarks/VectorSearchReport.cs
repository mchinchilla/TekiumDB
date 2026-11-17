// Copyright (c) Tekium. Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.Numerics.Tensors;
using Tekium.Embedded.Vectors;

namespace Tekium.Embedded.Benchmarks;

/// <summary>
/// Búsqueda vectorial: cuánto tarda construir el índice, cuánto ocupa, y el recall@10 contra fuerza
/// bruta y la latencia a distintos <c>efSearch</c>. Se corre con <c>-- vector [vectores] [dimensiones]</c>.
/// </summary>
internal static class VectorSearchReport
{
    private const int Queries = 1000;
    private const int Clusters = 1000;
    private const int PerCommit = 10_000;

    public static async Task RunAsync(int count, int dimensions, bool quantize, bool structured, int m)
    {
        var directory = Workload.NewDirectory();
        try
        {
            // Vectores agrupados alrededor de mil centros, y las consultas de la misma mezcla: lo que se busca se parece a lo guardado.
            var random = new Random(9);
            var centers = Enumerable.Range(0, Clusters).Select(_ => Gaussian(random, dimensions, 1f)).ToArray();
            var all = new float[count + Queries][];
            for (var i = 0; i < all.Length; i++)
            {
                // Sin estructura es el caso difícil: en 128 dimensiones todos los vectores quedan casi igual de lejos.
                all[i] = Gaussian(random, dimensions, structured ? 0.35f : 1f);
                if (structured)
                {
                    TensorPrimitives.Add(all[i], centers[random.Next(Clusters)], all[i]);
                }
            }

            var options = new VectorIndexOptions { Dimensions = dimensions, Metric = VectorMetric.Cosine, Quantize = quantize, M = m };
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{count:N0} vectores de {dimensions} dimensiones ({(structured ? "agrupados en mil grupos" : "sin estructura")}), coseno, M {options.M}, efConstruction {options.EfConstruction}{(quantize ? ", cuantizado a int8" : string.Empty)}; {Queries:N0} consultas; sobre archivo, commits de {PerCommit:N0}."));

            var path = Path.Combine(directory, "vectors.tkdb");
            var database = await TekiumDb.OpenAsync(new DbOptions { Path = path, CacheSizeMb = 4096 });
            await using (database)
            {
                var index = database.VectorIndex<int>("embeddings", options);
                var watch = Stopwatch.StartNew();
                for (var start = 0; start < count; start += PerCommit)
                {
                    await using var writer = await database.BeginWriteAsync();
                    for (var i = start; i < Math.Min(start + PerCommit, count); i++)
                    {
                        await index.UpsertAsync(writer, i, all[i]);
                    }

                    await writer.CommitAsync();
                    if ((start / PerCommit) % 10 == 9)
                    {
                        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {start + PerCommit:N0} en {watch.Elapsed.TotalSeconds:F0} s"));
                    }
                }

                var build = watch.Elapsed;
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Construcción: {build.TotalSeconds:F0} s ({count / build.TotalSeconds:F0} vectores/s). En uso: {(database.Pager.Current.PageCount - (long)database.Pager.FreePageCount) * database.Pager.PageSize.Bytes / 1048576.0:F0} MiB de un archivo de {new FileInfo(path).Length / 1048576.0:F0} MiB."));

                // La respuesta exacta, comparando cada consulta contra todos. Coseno: se normaliza una vez.
                watch.Restart();
                foreach (var vector in all)
                {
                    TensorPrimitives.Divide(vector, TensorPrimitives.Norm(vector), vector);
                }

                var truth = new int[Queries][];
                Parallel.For(0, Queries, q =>
                {
                    var query = all[count + q];
                    var best = new PriorityQueue<int, float>();
                    for (var i = 0; i < count; i++)
                    {
                        var similarity = TensorPrimitives.Dot(query, all[i]);
                        if (best.Count < 10)
                        {
                            best.Enqueue(i, similarity);
                        }
                        else if (best.TryPeek(out _, out var worst) && similarity > worst)
                        {
                            best.DequeueEnqueue(i, similarity);
                        }
                    }

                    truth[q] = [.. best.UnorderedItems.Select(static item => item.Element)];
                });
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Fuerza bruta: {watch.Elapsed.TotalMilliseconds / Queries:F1} ms por consulta (en paralelo)."));

                Console.WriteLine("| efSearch | Recall@10 | Latencia media | p99 |");
                Console.WriteLine("| ---: | ---: | ---: | ---: |");
                foreach (var ef in new[] { 16, 32, 64, 128, 256 })
                {
                    // Una pasada antes, sin medir: código compilado y caché caliente.
                    for (var q = 0; q < Queries; q += 10)
                    {
                        await index.SearchAsync(all[count + q], 10, ef);
                    }

                    var found = 0;
                    var times = new double[Queries];
                    for (var q = 0; q < Queries; q++)
                    {
                        var started = Stopwatch.GetTimestamp();
                        var hits = await index.SearchAsync(all[count + q], 10, ef);
                        times[q] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        found += hits.Select(static hit => hit.Key).Intersect(truth[q]).Count();
                    }

                    Array.Sort(times);
                    Console.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"| {ef} | {found / (10.0 * Queries):F4} | {times.Average():F3} ms | {times[(int)(Queries * 0.99)]:F3} ms |"));
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static float[] Gaussian(Random random, int dimensions, float sigma)
    {
        var vector = new float[dimensions];
        for (var i = 0; i < dimensions; i++)
        {
            var u = 1.0 - random.NextDouble();
            var v = random.NextDouble();
            vector[i] = (float)(Math.Sqrt(-2.0 * Math.Log(u)) * Math.Cos(2.0 * Math.PI * v)) * sigma;
        }

        return vector;
    }
}
