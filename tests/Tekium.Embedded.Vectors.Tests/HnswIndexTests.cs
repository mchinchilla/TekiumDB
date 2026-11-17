// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Catalog;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Security;
using Tekium.Embedded.Storage;
using Tekium.Embedded.Tests.Indexing;
using Tekium.Embedded.Tests.Storage;
using Tekium.Embedded.Vectors.Hnsw;

namespace Tekium.Embedded.Vectors.Tests;

/// <summary>
/// El índice vectorial contra la respuesta exacta: lo que encuentra se compara con buscar por fuerza
/// bruta, y lo que guarda sigue las reglas de cualquier otra cosa de la base (transacciones, snapshots,
/// reapertura, ninguna página perdida).
/// </summary>
public abstract class HnswIndexTests : IAsyncLifetime
{
    private readonly List<TestDevice> _devices = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => default;

    public async ValueTask DisposeAsync()
    {
        foreach (var device in _devices)
        {
            await device.DestroyAsync();
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task An_index_with_nothing_in_it_finds_nothing()
    {
        await using var database = await OpenAsync(NewDevice());
        var index = database.VectorIndex<int>("empty", new VectorIndexOptions { Dimensions = 4 });

        Assert.Empty(await index.SearchAsync(new float[] { 1, 0, 0, 0 }, topK: 5, cancellationToken: Ct));
        await using var read = database.BeginRead();
        Assert.Equal(0, await index.CountAsync(read, Ct));
    }

    [Theory]
    [InlineData(VectorMetric.Cosine)]
    [InlineData(VectorMetric.Euclidean)]
    [InlineData(VectorMetric.InnerProduct)]
    public async Task With_a_wide_enough_search_a_small_index_gives_the_exact_answer(VectorMetric metric)
    {
        await using var database = await OpenAsync(NewDevice());
        var options = new VectorIndexOptions { Dimensions = 8, Metric = metric };
        var index = database.VectorIndex<int>("small", options);
        var vectors = VectorData.Clustered(300, 8, seed: 3, clusters: 5);
        await InsertAsync(database, index, vectors);

        var queries = VectorData.Clustered(40, 8, seed: 4, clusters: 5);
        foreach (var query in queries)
        {
            var hits = await index.SearchAsync(query, topK: 10, efSearch: 300, Ct);
            var expected = VectorData.Nearest(metric, vectors, query, 10);
            Assert.Equal(expected, hits.Select(static hit => hit.Key));

            // Y las distancias son las de la métrica, de menor a mayor.
            for (var i = 0; i < hits.Count; i++)
            {
                Assert.Equal(VectorData.Distance(metric, vectors[hits[i].Key], query), hits[i].Distance, 1e-3f);
            }
        }
    }

    [Theory]
    [InlineData(VectorMetric.Cosine, false)]
    [InlineData(VectorMetric.Euclidean, false)]
    [InlineData(VectorMetric.InnerProduct, false)]
    [InlineData(VectorMetric.Cosine, true)]
    [InlineData(VectorMetric.Euclidean, true)]
    public async Task Recall_at_ten_is_above_ninety_five_percent(VectorMetric metric, bool quantize)
    {
        await using var database = await OpenAsync(NewDevice(), cacheMb: 32);
        var options = new VectorIndexOptions { Dimensions = 32, Metric = metric, Quantize = quantize };
        var index = database.VectorIndex<int>(
            "recall", metric == VectorMetric.Cosine ? options : options with { QuantizationMin = -4, QuantizationMax = 4 });

        // Las consultas salen de donde salen los datos, como al buscar algo parecido a lo guardado.
        var all = VectorData.Clustered(6100, 32, seed: 11);
        var vectors = all[..6000];
        await InsertAsync(database, index, vectors);

        var recall = await RecallAsync(index, metric, vectors, all[6000..], efSearch: 64);
        Assert.True(recall > 0.95, $"Recall@10 is {recall:F3}.");
    }

    /// <summary>
    /// El caso difícil: consultas que no se parecen a nada de lo guardado, con producto interno, que no es
    /// una distancia de verdad. Ensanchando la búsqueda se recupera.
    /// </summary>
    [Fact]
    public async Task Queries_unlike_anything_stored_are_answered_by_widening_the_search()
    {
        await using var database = await OpenAsync(NewDevice(), cacheMb: 32);
        var metric = VectorMetric.InnerProduct;
        var index = database.VectorIndex<int>("far", new VectorIndexOptions { Dimensions = 32, Metric = metric });
        var vectors = VectorData.Clustered(6000, 32, seed: 11);
        await InsertAsync(database, index, vectors);
        var strangers = VectorData.Clustered(100, 32, seed: 12);

        var narrow = await RecallAsync(index, metric, vectors, strangers, efSearch: 16);
        var wide = await RecallAsync(index, metric, vectors, strangers, efSearch: 200);
        Assert.True(wide > 0.97, $"Recall@10 is {wide:F3} with efSearch 200.");
        Assert.True(wide > narrow, $"Recall@10 is {narrow:F3} with efSearch 16 and {wide:F3} with 200.");
    }

    [Fact]
    public async Task What_a_transaction_wrote_is_found_inside_it_and_by_nobody_else_until_it_commits()
    {
        await using var database = await OpenAsync(NewDevice());
        var index = database.VectorIndex<string>("docs", new VectorIndexOptions { Dimensions = 3, Metric = VectorMetric.Euclidean });
        await using (var first = await database.BeginWriteAsync(Ct))
        {
            await index.UpsertAsync(first, "north", new float[] { 0, 1, 0 }, Ct);
            await first.CommitAsync(Ct);
        }

        await using var before = database.BeginRead();
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await index.UpsertAsync(writer, "east", new float[] { 1, 0, 0 }, Ct);

            Assert.Equal(["east", "north"], (await index.SearchAsync(writer, new float[] { 1, 0.1f, 0 }, 5, cancellationToken: Ct)).Select(static hit => hit.Key));
            Assert.Equal(2, await index.CountAsync(writer, Ct));
            Assert.Equal(["north"], (await index.SearchAsync(new float[] { 1, 0.1f, 0 }, 5, cancellationToken: Ct)).Select(static hit => hit.Key));
            await writer.CommitAsync(Ct);
        }

        // Quien leía desde antes sigue viendo lo de antes.
        Assert.Equal(["north"], (await index.SearchAsync(before, new float[] { 1, 0.1f, 0 }, 5, cancellationToken: Ct)).Select(static hit => hit.Key));
        Assert.Equal(1, await index.CountAsync(before, Ct));
        Assert.Equal(["east", "north"], (await index.SearchAsync(new float[] { 1, 0.1f, 0 }, 5, cancellationToken: Ct)).Select(static hit => hit.Key));
    }

    [Fact]
    public async Task A_transaction_that_does_not_commit_leaves_no_trace()
    {
        await using var database = await OpenAsync(NewDevice());
        var index = database.VectorIndex<int>("rolled", new VectorIndexOptions { Dimensions = 8 });
        var vectors = VectorData.Clustered(400, 8, seed: 5);
        await InsertAsync(database, index, vectors[..200]);
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            for (var i = 200; i < 400; i++)
            {
                await index.UpsertAsync(writer, i, vectors[i], Ct);
            }
        }

        var hits = await index.SearchAsync(vectors[300], topK: 400, efSearch: 400, Ct);
        Assert.Equal(200, hits.Count);
        Assert.All(hits, static hit => Assert.InRange(hit.Key, 0, 199));
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task The_index_is_the_same_after_closing_and_opening_the_database()
    {
        var device = NewDevice();
        var options = new VectorIndexOptions { Dimensions = 16, Metric = VectorMetric.Euclidean };
        var vectors = VectorData.Clustered(1500, 16, seed: 6);
        var queries = VectorData.Clustered(20, 16, seed: 7);
        var before = new List<int[]>();
        await using (var database = await OpenAsync(device))
        {
            var index = database.VectorIndex<int>("kept", options);
            await InsertAsync(database, index, vectors);
            foreach (var query in queries)
            {
                before.Add([.. (await index.SearchAsync(query, 10, cancellationToken: Ct)).Select(static hit => hit.Key)]);
            }
        }

        await using (var database = await OpenAsync(device))
        {
            var index = database.VectorIndex<int>("kept", options);
            for (var i = 0; i < queries.Length; i++)
            {
                Assert.Equal(before[i], (await index.SearchAsync(queries[i], 10, cancellationToken: Ct)).Select(static hit => hit.Key));
            }

            await using var read = database.BeginRead();
            Assert.Equal(1500, await index.CountAsync(read, Ct));
            await AssertNoLeaksAsync(database);
        }
    }

    [Fact]
    public async Task Replacing_and_removing_vectors_matches_brute_force_over_what_is_left()
    {
        await using var database = await OpenAsync(NewDevice(), cacheMb: 16);
        var metric = VectorMetric.Euclidean;
        var index = database.VectorIndex<int>("changing", new VectorIndexOptions { Dimensions = 12, Metric = metric });
        var random = new Random(21);
        var model = new float[]?[2500];
        var fresh = VectorData.Clustered(8000, 12, seed: 22);
        var next = 0;
        for (var round = 0; round < 8; round++)
        {
            await using var writer = await database.BeginWriteAsync(Ct);
            for (var i = 0; i < 600; i++)
            {
                var key = random.Next(model.Length);
                if (random.Next(4) == 0)
                {
                    Assert.Equal(model[key] is not null, await index.RemoveAsync(writer, key, Ct));
                    model[key] = null;
                }
                else
                {
                    model[key] = fresh[next++];
                    await index.UpsertAsync(writer, key, model[key], Ct);
                }
            }

            await writer.CommitAsync(Ct);
            await using var read = database.BeginRead();
            Assert.Equal(model.Count(static vector => vector is not null), await index.CountAsync(read, Ct));
        }

        // Ninguna clave borrada aparece, ninguna sale dos veces, y el recall sobre lo que queda se mantiene.
        var live = model.Count(static vector => vector is not null);
        var everything = await index.SearchAsync(fresh[0], topK: model.Length, efSearch: model.Length, Ct);
        Assert.Equal(everything.Count, everything.Select(static hit => hit.Key).Distinct().Count());
        Assert.All(everything, hit => Assert.NotNull(model[hit.Key]));
        Assert.True(everything.Count > live * 0.98, $"{everything.Count} of {live} live vectors are reachable.");
        var recall = await RecallAsync(index, metric, model, VectorData.Clustered(60, 12, seed: 23), efSearch: 64);
        Assert.True(recall > 0.95, $"Recall@10 is {recall:F3}.");
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task Keys_of_every_supported_type_come_back_as_they_went_in()
    {
        await using var database = await OpenAsync(NewDevice());
        var options = new VectorIndexOptions { Dimensions = 2, Metric = VectorMetric.Euclidean };
        var guid = Guid.CreateVersion7();
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await database.VectorIndex<Guid>("by_guid", options).UpsertAsync(writer, guid, new float[] { 1, 1 }, Ct);
            await database.VectorIndex<int>("by_int", options).UpsertAsync(writer, -7, new float[] { 1, 1 }, Ct);
            await database.VectorIndex<long>("by_long", options).UpsertAsync(writer, long.MinValue, new float[] { 1, 1 }, Ct);
            await database.VectorIndex<string>("by_text", options).UpsertAsync(writer, "añil", new float[] { 1, 1 }, Ct);
            await writer.CommitAsync(Ct);
        }

        var query = new float[] { 1, 0 };
        Assert.Equal(new VectorHit<Guid>(guid, 1f), Assert.Single(await database.VectorIndex<Guid>("by_guid", options).SearchAsync(query, 3, cancellationToken: Ct)));
        Assert.Equal(-7, Assert.Single(await database.VectorIndex<int>("by_int", options).SearchAsync(query, 3, cancellationToken: Ct)).Key);
        Assert.Equal(long.MinValue, Assert.Single(await database.VectorIndex<long>("by_long", options).SearchAsync(query, 3, cancellationToken: Ct)).Key);
        Assert.Equal("añil", Assert.Single(await database.VectorIndex<string>("by_text", options).SearchAsync(query, 3, cancellationToken: Ct)).Key);
        Assert.Throws<NotSupportedException>(() => database.VectorIndex<double>("by_double", options));
    }

    [Fact]
    public async Task What_does_not_fit_the_index_is_refused_before_anything_changes()
    {
        await using var database = await OpenAsync(NewDevice());
        var options = new VectorIndexOptions { Dimensions = 3 };
        var index = database.VectorIndex<int>("strict", options);
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await index.UpsertAsync(writer, 1, new float[] { 1, 2, 3 }, Ct);
            await Assert.ThrowsAsync<ArgumentException>(async () => await index.UpsertAsync(writer, 2, new float[] { 1, 2 }, Ct));
            await Assert.ThrowsAsync<ArgumentException>(async () => await index.UpsertAsync(writer, 2, new float[] { 1, float.NaN, 3 }, Ct));
            await Assert.ThrowsAsync<ArgumentException>(async () => await index.UpsertAsync(writer, 2, new float[] { 0, 0, 0 }, Ct));
            await Assert.ThrowsAsync<ArgumentException>(async () => await index.SearchAsync(writer, new float[] { 1, 2 }, 3, cancellationToken: Ct));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await index.SearchAsync(writer, new float[] { 1, 2, 3 }, 0, cancellationToken: Ct));

            // La transacción sigue sirviendo: nada de lo rechazado tocó el índice.
            await writer.CommitAsync(Ct);
        }

        Assert.Equal(1, Assert.Single(await index.SearchAsync(new float[] { 1, 2, 3 }, 5, cancellationToken: Ct)).Key);

        // Otra declaración sobre el mismo nombre: ni se lee ni se escribe.
        var other = database.VectorIndex<int>("strict", options with { Dimensions = 4 });
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await other.SearchAsync(new float[] { 1, 2, 3, 4 }, 5, cancellationToken: Ct));
        var cosine = database.VectorIndex<int>("strict", options with { Metric = VectorMetric.Euclidean });
        await using var second = await database.BeginWriteAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await cosine.UpsertAsync(second, 9, new float[] { 1, 2, 3 }, Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => database.VectorIndex<int>("bad", new VectorIndexOptions { Dimensions = 0 }));
    }

    [Fact]
    public async Task A_quantized_graph_takes_a_quarter_of_the_space_and_ranks_with_the_original_vectors()
    {
        await using var database = await OpenAsync(NewDevice(), cacheMb: 32);
        var plain = database.VectorIndex<int>("plain", new VectorIndexOptions { Dimensions = 64, Metric = VectorMetric.Euclidean });
        var small = database.VectorIndex<int>(
            "small", new VectorIndexOptions { Dimensions = 64, Metric = VectorMetric.Euclidean, Quantize = true, QuantizationMin = -4, QuantizationMax = 4 });
        var vectors = VectorData.Clustered(1500, 64, seed: 31);
        await InsertAsync(database, plain, vectors);
        await InsertAsync(database, small, vectors);

        var plainPages = await NodePagesAsync(database, "plain");
        var smallPages = await NodePagesAsync(database, "small");
        Assert.True(smallPages * 3 < plainPages, $"The quantized graph takes {smallPages} pages of nodes; the plain one, {plainPages}.");

        // La distancia que devuelve es la exacta, no la de los bytes.
        var query = VectorData.Clustered(1, 64, seed: 32)[0];
        foreach (var hit in await small.SearchAsync(query, 10, cancellationToken: Ct))
        {
            Assert.Equal(VectorData.Distance(VectorMetric.Euclidean, vectors[hit.Key], query), hit.Distance, 1e-3f);
        }

        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task Vectors_larger_than_a_page_are_stored_and_found()
    {
        await using var database = await OpenAsync(NewDevice(), cacheMb: 32);
        var index = database.VectorIndex<int>("wide", new VectorIndexOptions { Dimensions = 1536 });
        var vectors = VectorData.Clustered(120, 1536, seed: 41, clusters: 4);
        await InsertAsync(database, index, vectors);

        for (var i = 0; i < vectors.Length; i += 7)
        {
            Assert.Equal(i, (await index.SearchAsync(vectors[i], 1, cancellationToken: Ct))[0].Key);
        }

        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task Dropping_an_index_gives_its_pages_back_and_frees_its_name()
    {
        await using var database = await OpenAsync(NewDevice(), cacheMb: 16);
        var options = new VectorIndexOptions { Dimensions = 16, Quantize = true };
        var index = database.VectorIndex<int>("gone", options);
        var vectors = VectorData.Clustered(900, 16, seed: 51);
        await InsertAsync(database, index, vectors);
        var other = database.VectorIndex<int>("kept", options);
        await InsertAsync(database, other, vectors[..100]);
        var used = database.Pager.Current.PageCount - (uint)database.Pager.FreePageCount;

        await using (var abandoned = await database.BeginWriteAsync(Ct))
        {
            await index.DropAsync(abandoned, Ct);
            Assert.Empty(await index.SearchAsync(abandoned, vectors[0], 5, cancellationToken: Ct));
        }

        Assert.Equal(900, (await index.SearchAsync(vectors[0], 900, 900, Ct)).Count);

        // Con otra declaración se puede borrar, y el nombre queda libre para ella.
        var redeclared = database.VectorIndex<string>("gone", new VectorIndexOptions { Dimensions = 3, Metric = VectorMetric.Euclidean });
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await redeclared.DropAsync(writer, Ct);
            Assert.Equal(0, await redeclared.CountAsync(writer, Ct));
            await redeclared.UpsertAsync(writer, "again", new float[] { 1, 2, 3 }, Ct);
            await writer.CommitAsync(Ct);
        }

        Assert.Equal("again", Assert.Single(await redeclared.SearchAsync(new float[] { 1, 2, 3 }, 5, cancellationToken: Ct)).Key);
        Assert.Equal(100, (await other.SearchAsync(vectors[0], 100, 100, Ct)).Count);
        Assert.True(database.Pager.Current.PageCount - (uint)database.Pager.FreePageCount < used / 2, "Dropping the index did not free its pages.");
        await AssertNoLeaksAsync(database);

        // Borrar lo que queda no deja ni la entrada del catálogo.
        await using (var last = await database.BeginWriteAsync(Ct))
        {
            await redeclared.DropAsync(last, Ct);
            await last.CommitAsync(Ct);
        }

        Assert.Empty(await CatalogStore.ReadAsync(database.Pager, database.Pager.Current.Root, CatalogStore.VectorIndexKey("gone"), Ct));
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task An_operation_that_fails_halfway_leaves_a_transaction_that_can_only_be_rolled_back()
    {
        var device = NewDevice();
        await using var database = await OpenAsync(device, cacheMb: 1);
        var index = database.VectorIndex<int>("fragile", new VectorIndexOptions { Dimensions = 48 });
        var vectors = VectorData.Clustered(3001, 48, seed: 61);
        await InsertAsync(database, index, vectors[..3000]);

        // La caché no alcanza para el grafo: insertar lee del dispositivo, y una de esas lecturas falla.
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            device.FailOnReadNumber = device.ReadCount + 20;
            await Assert.ThrowsAsync<IOException>(async () => await index.UpsertAsync(writer, 3000, vectors[3000], Ct));
            device.FailOnReadNumber = null;
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await writer.CommitAsync(Ct));
        }

        Assert.Equal(3000, (await index.SearchAsync(vectors[0], 3000, 3000, Ct)).Count);
        await AssertNoLeaksAsync(database);
    }

    /// <summary>
    /// El índice se confirma con todo lo demás: si la energía se va en cualquier operación de un commit,
    /// al abrir está entero como antes de ese commit o entero como después, nunca a medias.
    /// </summary>
    [Fact]
    public async Task Losing_power_at_any_point_of_a_commit_leaves_the_index_as_it_was_or_as_it_became()
    {
        var vectors = VectorData.Clustered(600, 12, seed: 71);
        var complete = NewDevice();
        var counts = await RunAsync(complete, null);
        var first = NewDevice();
        await RunAsync(first, 0);
        var operations = complete.OperationCount - first.OperationCount;
        Assert.True(operations > 12, "The workload is too small to say anything about the commit.");

        for (var at = 1; at <= operations; at++)
        {
            var device = NewDevice();
            await RunAsync(device, at);
            Assert.True(device.PowerLost);
            foreach (var seed in new[] { 0, 1, 12345 + at })
            {
                var image = await device.PowerLossImageAsync(
                    (write, sector) => seed == 1 || (seed != 0 && HashCode.Combine(seed, write, sector) % 2 == 0));
                _devices.Add(image);
                await using var recovered = await OpenAsync(image);
                var index = recovered.VectorIndex<int>("crash", new VectorIndexOptions { Dimensions = 12 });
                await using var read = recovered.BeginRead();
                var count = (int)await index.CountAsync(read, Ct);
                Assert.Contains(count, counts);
                var hits = await index.SearchAsync(read, vectors[0], 600, 600, Ct);
                Assert.Equal(hits.Count, hits.Select(static hit => hit.Key).Distinct().Count());
                Assert.InRange(hits.Count, count * 0.97, count);
                await AssertNoLeaksAsync(recovered);
            }
        }

        // Cuatro commits: 300 vectores, y después tres veces 60 más y 20 menos. Devuelve cuántos hay tras cada uno.
        async Task<List<int>> RunAsync(TestDevice device, int? lossAfterFirstCommit)
        {
            var after = new List<int>();
            try
            {
                await using var database = await OpenAsync(device);
                var index = database.VectorIndex<int>("crash", new VectorIndexOptions { Dimensions = 12 });
                for (var round = 0; round < 4; round++)
                {
                    await using var writer = await database.BeginWriteAsync(Ct);
                    var from = round == 0 ? 0 : 240 + (round * 60);
                    for (var i = from; i < (round == 0 ? 300 : from + 60); i++)
                    {
                        await index.UpsertAsync(writer, i, vectors[i], Ct);
                    }

                    for (var i = 0; round > 0 && i < 20; i++)
                    {
                        await index.RemoveAsync(writer, (round * 20) + i, Ct);
                    }

                    await writer.CommitAsync(Ct);
                    after.Add((int)await index.CountAsync(writer, Ct));
                    if (round == 0 && lossAfterFirstCommit is { } at)
                    {
                        if (at == 0)
                        {
                            return after;
                        }

                        device.TrackUnflushedWrites = true;
                        device.PowerLossAtOperation = device.OperationCount + at;
                    }
                }
            }
            catch (IOException) when (device.PowerLost)
            {
            }

            return after;
        }
    }

    /// <summary>La forma del grafo, no sólo lo que encuentra: un grafo mal formado puede acertar en conjuntos chicos.</summary>
    [Fact]
    public async Task The_graph_keeps_its_shape()
    {
        await using var database = await OpenAsync(NewDevice(), cacheMb: 16);
        const int M = 6;
        var index = database.VectorIndex<int>("shape", new VectorIndexOptions { Dimensions = 10, M = M, Metric = VectorMetric.Euclidean });
        await InsertAsync(database, index, VectorData.Clustered(3000, 10, seed: 81));

        var pager = database.Pager;
        using var read = pager.BeginRead();
        var header = HnswHeader.Decode(await CatalogStore.ReadAsync(pager, read.Root, CatalogStore.VectorIndexKey("shape"), Ct));
        var levels = new Dictionary<uint, int>();
        await foreach (var node in BTree.RangeAsync(pager, header.Nodes, default, default, Ct))
        {
            levels.Add(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(node.Key.Span), node.Value.Span[1]);
        }

        Assert.Equal(3000, levels.Count);
        Assert.Equal(3000u, header.NextId);

        // Cada capa tiene más o menos una de cada M de la de abajo, y se entra por la más alta.
        Assert.InRange(levels.Values.Count(static level => level >= 1), 3000 / (M * 2), 3000 * 2 / M);
        Assert.InRange(levels.Values.Count(static level => level >= 2), 1, 3000 / M);
        Assert.Equal(levels.Values.Max(), header.MaxLevel);
        Assert.Equal(header.MaxLevel, levels[header.Entry]);

        var longest = new int[header.MaxLevel + 1];
        var linked = new HashSet<uint>();
        await foreach (var list in BTree.RangeAsync(pager, header.Links, default, default, Ct))
        {
            var owner = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(list.Key.Span);
            var layer = list.Key.Span[4];
            var neighbours = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(list.Value.Span).ToArray();
            Assert.True(levels[owner] >= layer, $"Node {owner} has neighbours on layer {layer}, above its own.");
            Assert.True(neighbours.Length <= (layer == 0 ? 2 * M : M), $"Node {owner} keeps {neighbours.Length} neighbours on layer {layer}.");
            Assert.Equal(neighbours.Length, neighbours.Distinct().Count());
            Assert.DoesNotContain(owner, neighbours);
            Assert.All(neighbours, neighbour => Assert.True(levels[neighbour] >= layer, $"Node {neighbour} is linked on layer {layer}, above its own."));
            longest[layer] = Math.Max(longest[layer], neighbours.Length);
            if (layer == 0 && neighbours.Length > 0)
            {
                linked.Add(owner);
            }
        }

        // La capa de abajo admite el doble de vecinos y los usa; nadie queda suelto.
        Assert.Equal(2 * M, longest[0]);
        Assert.Equal(M, longest[1]);
        Assert.Equal(3000, linked.Count);
    }

    /// <summary>
    /// Cuantización con un rango que no está centrado en cero y con componentes que se salen de él: los que
    /// se salen cuentan como el extremo, y el resto se compara donde de verdad está.
    /// </summary>
    [Fact]
    public async Task A_quantized_graph_works_with_a_lopsided_range_and_values_outside_it()
    {
        await using var database = await OpenAsync(NewDevice(), cacheMb: 32);
        var metric = VectorMetric.Euclidean;
        var index = database.VectorIndex<int>(
            "lopsided", new VectorIndexOptions { Dimensions = 24, Metric = metric, Quantize = true, QuantizationMin = 2, QuantizationMax = 10 });
        var all = VectorData.Clustered(4100, 24, seed: 91);
        var random = new Random(92);
        foreach (var vector in all)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                // Todo desplazado a positivo; una componente de cada cuarenta, muy fuera del rango.
                vector[i] = (vector[i] * 1.2f) + 6f + (random.Next(40) == 0 ? (random.Next(2) == 0 ? 30f : -30f) : 0f);
            }
        }

        var vectors = all[..4000];
        await InsertAsync(database, index, vectors);
        for (var i = 0; i < 4000; i += 4)
        {
            await using var writer = await database.BeginWriteAsync(Ct);
            Assert.True(await index.RemoveAsync(writer, i, Ct));
            await writer.CommitAsync(Ct);
            i += 396;
        }

        // Con poco margen de búsqueda: si el grafo comparara mal, los candidatos no incluirían a los vecinos.
        var model = vectors.Select(static (vector, i) => i % 400 == 0 ? null : vector).ToArray();
        var recall = await RecallAsync(index, metric, model, all[4000..], efSearch: 24);
        Assert.True(recall > 0.9, $"Recall@10 is {recall:F3}.");

        // Los vectores originales de lo borrado no se quedan.
        var pager = database.Pager;
        using var read = pager.BeginRead();
        var header = HnswHeader.Decode(await CatalogStore.ReadAsync(pager, read.Root, CatalogStore.VectorIndexKey("lopsided"), Ct));
        var originals = 0;
        await foreach (var _ in BTree.RangeAsync(pager, header.Exact, default, default, Ct))
        {
            originals++;
        }

        Assert.Equal(model.Count(static vector => vector is not null), originals);
        Assert.Equal((uint)originals, header.Count);
    }

    /// <summary>Lo borrado va saliendo del grafo: cuando una lista de vecinos se rehace, no vuelve a entrar.</summary>
    [Fact]
    public async Task Links_to_removed_vectors_fade_as_the_graph_keeps_growing()
    {
        await using var database = await OpenAsync(NewDevice(), cacheMb: 16);
        var index = database.VectorIndex<int>("fading", new VectorIndexOptions { Dimensions = 8, M = 6, Metric = VectorMetric.Euclidean });
        var vectors = VectorData.Clustered(4000, 8, seed: 95, clusters: 6);
        await InsertAsync(database, index, vectors[..1000]);
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            for (var i = 0; i < 1000; i += 2)
            {
                await index.RemoveAsync(writer, i, Ct);
            }

            await writer.CommitAsync(Ct);
        }

        var before = await LinksToRemovedAsync();
        for (var start = 1000; start < 4000; start += 1000)
        {
            await using var writer = await database.BeginWriteAsync(Ct);
            for (var i = start; i < start + 1000; i++)
            {
                await index.UpsertAsync(writer, i, vectors[i], Ct);
            }

            await writer.CommitAsync(Ct);
        }

        var after = await LinksToRemovedAsync();
        Assert.True(before > 2000, $"Only {before} links pointed to removed vectors to begin with.");
        Assert.True(after * 4 < before, $"{after} of {before} links to removed vectors are left.");

        // Enlaces de la capa de abajo, desde nodos vivos, hacia nodos borrados.
        async Task<int> LinksToRemovedAsync()
        {
            var pager = database.Pager;
            using var read = pager.BeginRead();
            var header = HnswHeader.Decode(await CatalogStore.ReadAsync(pager, read.Root, CatalogStore.VectorIndexKey("fading"), Ct));
            var removed = new HashSet<uint>();
            await foreach (var node in BTree.RangeAsync(pager, header.Nodes, default, default, Ct))
            {
                if ((node.Value.Span[0] & HnswGraph.RemovedFlag) != 0)
                {
                    removed.Add(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(node.Key.Span));
                }
            }

            var links = 0;
            await foreach (var list in BTree.RangeAsync(pager, header.Links, default, default, Ct))
            {
                if (list.Key.Span[4] == 0 && !removed.Contains(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(list.Key.Span)))
                {
                    links += System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(list.Value.Span).ToArray().Count(removed.Contains);
                }
            }

            return links;
        }
    }

    /// <summary>Las herramientas del núcleo siguen los árboles del índice vectorial sin saber qué guardan.</summary>
    [Fact]
    public async Task The_file_tools_inspect_compact_and_repair_a_database_with_vector_indexes()
    {
        var device = NewDevice();
        var options = new VectorIndexOptions { Dimensions = 16, Metric = VectorMetric.Euclidean, Quantize = true, QuantizationMin = -4, QuantizationMax = 4 };
        var vectors = VectorData.Clustered(800, 16, seed: 101);
        var expected = new List<int[]>();
        await using (var database = await OpenAsync(device))
        {
            await InsertAsync(database, database.VectorIndex<int>("kept", options), vectors);
            await InsertAsync(database, database.VectorIndex<int>("hurt", options), vectors[..300]);
            foreach (var query in vectors[..20])
            {
                expected.Add([.. (await database.VectorIndex<int>("kept", options).SearchAsync(query, 5, cancellationToken: Ct)).Select(static hit => hit.Key)]);
            }
        }

        var inspection = await TkInspector.InspectAsync(() => device, default, Ct);
        Assert.True(inspection.IsHealthy);
        Assert.Equal(
            ["vector index kept, tree 1", "vector index kept, tree 2", "vector index kept, tree 3", "vector index kept, tree 4"],
            inspection.Trees.Select(static tree => tree.Name).Where(static name => name.Contains("kept", StringComparison.Ordinal)));
        Assert.Equal(800, inspection.Trees.Single(static tree => tree.Name == "vector index kept, tree 1").Entries);

        // Compactar: el índice responde igual desde la copia.
        var compacted = NewDevice();
        var report = await TkMaintenance.CopyAsync(() => device, _ => compacted, null, salvage: false, Ct);
        Assert.True(report.DestinationBytes < report.SourceBytes);
        await using (var database = await OpenAsync(compacted))
        {
            var index = database.VectorIndex<int>("kept", options);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i], (await index.SearchAsync(vectors[i], 5, cancellationToken: Ct)).Select(static hit => hit.Key));
            }

            await AssertNoLeaksAsync(database);
        }

        // Reparar con un árbol del índice dañado: ese índice no se copia (un grafo incompleto no se nota), el otro sí.
        uint page;
        await using (var database = await OpenAsync(device))
        {
            using var read = database.Pager.BeginRead();
            page = HnswHeader.Decode(await CatalogStore.ReadAsync(database.Pager, read.Root, CatalogStore.VectorIndexKey("hurt"), Ct)).Links.Value;
        }

        await device.CorruptAsync(((long)page * 4096) + 300);
        var damaged = await TkInspector.InspectAsync(() => device, default, Ct);
        Assert.Equal("vector index hurt, tree 2", Assert.Single(damaged.Findings).Where);
        var repaired = NewDevice();
        report = await TkMaintenance.CopyAsync(() => device, _ => repaired, null, salvage: true, Ct);
        Assert.Equal(["vector index hurt"], report.Dropped);
        await using (var database = await OpenAsync(repaired))
        {
            Assert.Empty(await database.VectorIndex<int>("hurt", options).SearchAsync(vectors[0], 5, cancellationToken: Ct));
            Assert.Equal(expected[0], (await database.VectorIndex<int>("kept", options).SearchAsync(vectors[0], 5, cancellationToken: Ct)).Select(static hit => hit.Key));
            await AssertNoLeaksAsync(database);
        }
    }

    [Fact]
    public async Task A_hot_backup_takes_the_vector_index_as_it_was_when_it_started()
    {
        await using var database = await OpenAsync(NewDevice(), cacheMb: 16);
        var options = new VectorIndexOptions { Dimensions = 16, Metric = VectorMetric.Euclidean };
        var index = database.VectorIndex<int>("live", options);
        var vectors = VectorData.Clustered(1200, 16, seed: 111);
        await InsertAsync(database, index, vectors[..600]);
        var expected = new List<int[]>();
        foreach (var query in vectors[..15])
        {
            expected.Add([.. (await index.SearchAsync(query, 5, cancellationToken: Ct)).Select(static hit => hit.Key)]);
        }

        // Con el estado ya fijado, el índice sigue creciendo y pierde vectores.
        var target = NewDevice();
        var report = await database.BackupAsync(
            _ => target,
            null,
            async () =>
            {
                await using var writer = await database.BeginWriteAsync(Ct);
                for (var i = 600; i < 1200; i++)
                {
                    await index.UpsertAsync(writer, i, vectors[i], Ct);
                }

                for (var i = 0; i < 300; i++)
                {
                    await index.RemoveAsync(writer, i, Ct);
                }

                await writer.CommitAsync(Ct);
            },
            Ct);

        Assert.True(report.TransactionId < database.LastTransactionId);

        // La verificación de la base abierta también sigue los cuatro árboles del índice.
        var verification = await database.VerifyAsync(Ct);
        Assert.True(verification.IsHealthy);
        Assert.Equal(1200, verification.Trees.Single(static tree => tree.Name == "vector index live, tree 1").Entries);
        await using var copy = await OpenAsync(target);
        var copied = copy.VectorIndex<int>("live", options);
        await using var read = copy.BeginRead();
        Assert.Equal(600, await copied.CountAsync(read, Ct));
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i], (await copied.SearchAsync(vectors[i], 5, cancellationToken: Ct)).Select(static hit => hit.Key));
        }

        await AssertNoLeaksAsync(copy);
        await AssertNoLeaksAsync(database);
    }

    [Fact]
    public async Task A_search_as_of_a_past_transaction_finds_what_the_index_held_then()
    {
        await using var database = await TekiumDb.OpenAsync(
            _ => NewDevice(),
            new DbOptions { InMemory = true, PageSize = 4096, CacheSizeMb = 16, ChangeRetention = 10, KeyDerivation = new Argon2Parameters(MemoryKiB: 64, Iterations: 1, Parallelism: 1) },
            Ct);
        var index = database.VectorIndex<int>("history", new VectorIndexOptions { Dimensions = 8, Metric = VectorMetric.Euclidean });
        var vectors = VectorData.Clustered(900, 8, seed: 121);
        await InsertAsync(database, index, vectors[..300]);
        var then = database.LastTransactionId;
        await InsertAsync(database, index, vectors[300..]);
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            for (var i = 0; i < 300; i++)
            {
                await index.RemoveAsync(writer, i, Ct);
            }

            await writer.CommitAsync(Ct);
        }

        // Ahora ninguno de los primeros trescientos existe; entonces eran los únicos.
        Assert.All(await index.SearchAsync(vectors[5], 20, cancellationToken: Ct), static hit => Assert.InRange(hit.Key, 300, 899));
        await using var past = await database.AsOfAsync(then, Ct);
        Assert.Equal(300, await index.CountAsync(past, Ct));
        var hits = await index.SearchAsync(past, vectors[5], 20, cancellationToken: Ct);
        Assert.Equal(5, hits[0].Key);
        Assert.All(hits, static hit => Assert.InRange(hit.Key, 0, 299));
    }

    private protected abstract TestDevice CreateDevice();

    private TestDevice NewDevice()
    {
        var device = CreateDevice();
        _devices.Add(device);
        return device;
    }

    private static ValueTask<TekiumDb> OpenAsync(TestDevice device, int cacheMb = 4) =>
        TekiumDb.OpenAsync(
            _ => device,
            new DbOptions
            {
                InMemory = true,
                PageSize = 4096,
                CacheSizeMb = cacheMb,
                KeyDerivation = new Argon2Parameters(MemoryKiB: 64, Iterations: 1, Parallelism: 1),
            },
            Ct);

    private static async Task InsertAsync(TekiumDb database, IVectorIndex<int> index, float[][] vectors)
    {
        for (var start = 0; start < vectors.Length; start += 1000)
        {
            await using var writer = await database.BeginWriteAsync(Ct);
            for (var i = start; i < Math.Min(start + 1000, vectors.Length); i++)
            {
                await index.UpsertAsync(writer, i, vectors[i], Ct);
            }

            await writer.CommitAsync(Ct);
        }
    }

    private static async Task<double> RecallAsync(
        IVectorIndex<int> index, VectorMetric metric, IReadOnlyList<float[]?> vectors, float[][] queries, int efSearch)
    {
        var found = 0;
        foreach (var query in queries)
        {
            var hits = await index.SearchAsync(query, 10, efSearch, Ct);
            found += hits.Select(static hit => hit.Key).Intersect(VectorData.Nearest(metric, vectors, query, 10)).Count();
        }

        return found / (10.0 * queries.Length);
    }

    private static async Task<int> NodePagesAsync(TekiumDb database, string name)
    {
        using var read = database.Pager.BeginRead();
        var entry = await CatalogStore.ReadAsync(database.Pager, read.Root, CatalogStore.VectorIndexKey(name), Ct);
        return await BTreeVerifier.VerifyAsync(database.Pager, HnswHeader.Decode(entry).Nodes, Ct);
    }

    /// <summary>Toda página es de cabecera, del catálogo, de algún árbol de un índice vectorial, o está libre.</summary>
    private static async Task AssertNoLeaksAsync(TekiumDb database)
    {
        var pager = database.Pager;
        using var read = pager.BeginRead();
        var pages = await BTreeVerifier.VerifyAsync(pager, read.Root, Ct);
        var roots = new List<PageNumber>();
        await foreach (var entry in BTree.RangeAsync(pager, read.Root, default, default, Ct))
        {
            if (CatalogStore.IsVectorIndexKey(entry.Key.Span))
            {
                var header = HnswHeader.Decode(entry.Value.Span);
                roots.AddRange([header.Nodes, header.Links, header.Keys, header.Exact]);
            }
        }

        foreach (var root in roots)
        {
            pages += await BTreeVerifier.VerifyAsync(pager, root, Ct);
        }

        Assert.Equal(
            read.Snapshot.PageCount,
            FileHeader.MetaPages + (uint)pages + (uint)pager.FreePageCount + (uint)pager.FreeListPageCount);
    }
}

public sealed class HnswIndexTests_File : HnswIndexTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class HnswIndexTests_Memory : HnswIndexTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
