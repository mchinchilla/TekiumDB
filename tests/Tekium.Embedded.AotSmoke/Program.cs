// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Tekium.Embedded;
using Tekium.Embedded.AotSmoke;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Security;
using Tekium.Embedded.Storage;
using Tekium.Embedded.Transactions;
using Tekium.Embedded.Vectors;

// Ejercita el núcleo bajo Native AOT. Devuelve 0 si todo va bien; CI ejecuta el binario publicado.
// Cada fase agrega aquí su camino feliz mínimo.
var failures = 0;

foreach (var size in PageSize.Supported.ToArray())
{
    if (PageSize.FromBytes(size.Bytes) != size)
    {
        await Console.Error.WriteLineAsync($"PageSize round-trip failed for {size}.");
        failures++;
    }
}

if (PageSize.TryFromBytes(12288, out _))
{
    await Console.Error.WriteLineAsync("PageSize accepted an unsupported size.");
    failures++;
}

// Motor: escribir, cerrar, reabrir y leer por el camino asíncrono, sobre archivo y sobre memoria.
var path = Path.Combine(Path.GetTempPath(), $"tekium-aot-{Guid.NewGuid():N}.tkdb");
try
{
    failures += await RoundTripAsync("file", () => FileStorageDevice.Open(path, FileMode.OpenOrCreate), reopen: true);
}
finally
{
    File.Delete(path);
}

failures += await RoundTripAsync("memory", static () => new MemoryStorageDevice(), reopen: false);
failures += await EncryptedRoundTripAsync();
failures += await TypedRoundTripAsync();
failures += await VectorRoundTripAsync();

await Console.Out.WriteLineAsync(
    $"TekiumDB AOT smoke: {(failures == 0 ? "OK" : "FAILED")} " +
    $"(dynamic code supported: {RuntimeFeature.IsDynamicCodeSupported})");

return failures;

// Búsqueda vectorial: construir, cerrar, reabrir y encontrar, con y sin cuantización, sobre una base cifrada.
static async Task<int> VectorRoundTripAsync()
{
    var file = Path.Combine(Path.GetTempPath(), $"tekium-aot-vectors-{Guid.NewGuid():N}.tkdb");
    var options = new DbOptions { Path = file, Password = "aot vector password".AsMemory(), CacheSizeMb = 16 };
    var plain = new VectorIndexOptions { Dimensions = 24, Metric = VectorMetric.Cosine };
    var small = new VectorIndexOptions { Dimensions = 24, Metric = VectorMetric.Euclidean, Quantize = true, QuantizationMin = -2, QuantizationMax = 2 };
    var random = new Random(5);
    var vectors = new float[800][];
    for (var i = 0; i < vectors.Length; i++)
    {
        vectors[i] = new float[24];
        for (var j = 0; j < 24; j++)
        {
            vectors[i][j] = (float)((random.NextDouble() * 2) - 1);
        }
    }

    try
    {
        var database = await TekiumDb.OpenAsync(options);
        await using (database)
        {
            await using var writer = await database.BeginWriteAsync();
            for (var i = 0; i < vectors.Length; i++)
            {
                await database.VectorIndex<Guid>("by_id", plain).UpsertAsync(writer, Guid.CreateVersion7(DateTimeOffset.UnixEpoch.AddSeconds(i)), vectors[i]);
                await database.VectorIndex<string>("by_name", small).UpsertAsync(writer, $"v{i}", vectors[i]);
            }

            await database.VectorIndex<string>("by_name", small).RemoveAsync(writer, "v7");
            await writer.CommitAsync();
        }

        database = await TekiumDb.OpenAsync(options);
        await using (database)
        {
            // Cada vector es su propio vecino más cercano; el borrado no aparece.
            var misses = 0;
            for (var i = 0; i < vectors.Length; i += 13)
            {
                var byId = await database.VectorIndex<Guid>("by_id", plain).SearchAsync(vectors[i], topK: 3);
                var byName = await database.VectorIndex<string>("by_name", small).SearchAsync(vectors[i], topK: 3);
                misses += byId.Count == 3 && byId[0].Distance < 1e-3f ? 0 : 1;
                misses += byName.Count == 3 && byName[0].Key == $"v{i}" && byName[0].Distance < 1e-3f ? 0 : 1;
            }

            var removed = await database.VectorIndex<string>("by_name", small).SearchAsync(vectors[7], topK: 800, efSearch: 800);
            if (misses == 0 && removed.Count == 799 && !removed.Any(static hit => hit.Key == "v7"))
            {
                return 0;
            }

            await Console.Error.WriteLineAsync($"Vector round-trip failed: {misses} misses, {removed.Count} found after a removal.");
            return 1;
        }
    }
    finally
    {
        File.Delete(file);
    }
}

// La API pública de punta a punta: serializadores generados, catálogo y evolución de esquema, sin reflexión.
static async Task<int> TypedRoundTripAsync()
{
    var file = Path.Combine(Path.GetTempPath(), $"tekium-aot-typed-{Guid.NewGuid():N}.tkdb");
    // Cifrada y comprimida: Zstandard viene de la biblioteca nativa del propio runtime, que AOT enlaza dentro del binario.
    var options = new DbOptions { Path = file, Password = "aot typed password".AsMemory(), CacheSizeMb = 8, Compression = true, ChangeRetention = 16 };
    // Con su marca de tiempo explícita: dos UUID v7 del mismo milisegundo no tienen orden garantizado.
    var at = new DateTimeOffset(2026, 11, 16, 9, 30, 0, TimeSpan.FromHours(-6));
    var first = Guid.CreateVersion7(at);
    var second = Guid.CreateVersion7(at.AddSeconds(1));

    // Mucho más grande que una página: la fila sale a páginas de overflow.
    var large = new byte[200_000];
    new Random(11).NextBytes(large);
    try
    {
        var database = await TekiumDb.OpenAsync(options);
        await using (database)
        {
            await using var writer = await database.BeginWriteAsync();
            await database.Table<EventV1>().InsertAsync(writer, new EventV1 { Id = first, Title = "written as version 1", At = at, Obsolete = 9 });
            await writer.CommitAsync();
        }

        database = await TekiumDb.OpenAsync(options);
        await using (database)
        {
            var events = database.Table<EventV2>();
            var old = await events.GetAsync(first);
            await using (var writer = await database.BeginWriteAsync())
            {
                await database.Table<Venue>().InsertAsync(writer, new Venue { Name = "here" });
                await events.InsertAsync(writer, new EventV2 { Id = second, Title = "written as version 2", At = at, Place = "here", Cost = 12.50m, Mood = Mood.Busy, Attachment = large, Weight = 0.5 });
                await writer.CommitAsync();
            }

            var titles = new List<string>();
            await foreach (var row in events.ScanAsync())
            {
                titles.Add(row.Title);
            }

            // Índices: el de fecha se construyó con la fila vieja dentro, y el parcial sólo se usa si la consulta lo implica.
            var byDate = new List<string>();
            await foreach (var row in events.QueryAsync(EventV2.Fields.At.Between(at.AddMinutes(-1), at.ToOffset(TimeSpan.Zero))))
            {
                byDate.Add(row.Title);
            }

            var busy = EventV2.Fields.Mood.EqualTo(Mood.Busy).And(EventV2.Fields.Cost.AtLeast(10m));
            var byCost = new List<string>();
            await foreach (var row in events.QueryAsync(busy))
            {
                byCost.Add(row.Title);
            }

            var byLength = new List<string>();
            await foreach (var row in events.QueryAsync(EventV2.Fields.TitleLength.EqualTo("written as version 2".Length).And(EventV2.Fields.Place.IsNotNull())))
            {
                byLength.Add(row.Title);
            }

            if (byDate.Count != 2
                || byCost is not ["written as version 2"]
                || byLength is not ["written as version 2"]
                || await events.ExplainAsync(busy) is not { Access: TkAccess.IndexScan, Index: "busy" }
                || await events.ExplainAsync(EventV2.Fields.Cost.AtLeast(10m)) is not { Access: TkAccess.TableScan }
                || await events.ExplainAsync(EventV2.Fields.TitleLength.EqualTo(3)) is not { Access: TkAccess.IndexScan, Index: "TitleLength" })
            {
                await Console.Error.WriteLineAsync($"Index round-trip failed: {byDate.Count} by date, {byCost.Count} by cost, {byLength.Count} by length.");
                return 1;
            }

            // Restricciones: cada violación nombra la suya y aborta su transacción.
            var third = Guid.CreateVersion7(at.AddSeconds(2));
            var violated = new List<string?>();
            foreach (var attempt in new Func<TkWriteTransaction, ValueTask>[]
            {
                writer => events.InsertAsync(writer, new EventV2 { Id = third, Title = "negative", At = at, Cost = -1m }),
                writer => events.InsertAsync(writer, new EventV2 { Id = third, Title = "written as version 2", At = at }),
                writer => events.InsertAsync(writer, new EventV2 { Id = third, Title = "nowhere", At = at, Place = "missing" }),
                async writer => await database.Table<Venue>().DeleteAsync(writer, "here"),
            })
            {
                await using var writer = await database.BeginWriteAsync();
                try
                {
                    await attempt(writer);
                    await writer.CommitAsync();
                    violated.Add("none");
                }
                catch (TkConstraintException violation)
                {
                    violated.Add(violation.Constraint);
                }
            }

            if (violated is not ["CostIsNotNegative", "Title", "fk_Place", "fk_Place"]
                || await events.ExplainAsync(EventV2.Fields.Title.EqualTo("written as version 2")) is not { Access: TkAccess.UniqueLookup, Index: "Title" })
            {
                await Console.Error.WriteLineAsync($"Constraint round-trip failed: {string.Join(", ", violated)}.");
                return 1;
            }

            // Change feed: desde el principio, con lo que escribió la sesión anterior con el otro tipo.
            var kinds = new List<TkChangeKind>();
            await foreach (var change in database.WatchAsync<EventV2>(0))
            {
                kinds.Add(change.Kind);
                if (change.TransactionId == database.LastTransactionId)
                {
                    break;
                }
            }

            if (kinds is not [TkChangeKind.Insert, TkChangeKind.Insert])
            {
                await Console.Error.WriteLineAsync($"Change feed failed: {string.Join(", ", kinds)}.");
                return 1;
            }

            // Carga masiva: filas desordenadas, a una tabla con índices y restricciones que ya tiene filas.
            await using (var writer = await database.BeginWriteAsync())
            {
                var loaded = await events.BulkInsertAsync(
                    writer,
                    Enumerable.Range(0, 3000).Select(i => new EventV2 { Id = Guid.CreateVersion7(at.AddMinutes(3000 - i)), Title = $"loaded {i}", At = at.AddDays(1 + i), Place = "here" }));
                await writer.CommitAsync();
                var counted = 0;
                await foreach (var row in events.QueryAsync(EventV2.Fields.At.GreaterThan(at.AddHours(1))))
                {
                    counted += row.Title.StartsWith("loaded", StringComparison.Ordinal) ? 1 : 0;
                }

                // Lo creado en un lapso es un rango de la clave UUID v7: las filas cargadas son una por minuto.
                var created = EventV2.Fields.Id.CreatedBetween(at.AddMinutes(1), at.AddMinutes(100));
                var inSpan = 0;
                await foreach (var row in events.QueryAsync(created))
                {
                    inSpan += row.Title.StartsWith("loaded", StringComparison.Ordinal) ? 1 : 0;
                }

                // Un rango de fechas de varias ventanas va por el zone map; dentro de una, por el índice.
                if (loaded != 3000
                    || counted != 3000
                    || inSpan != 100
                    || await events.ExplainAsync(created) is not { Access: TkAccess.KeyRange }
                    || await events.ExplainAsync(EventV2.Fields.At.GreaterThan(at.AddHours(1))) is not { Access: TkAccess.ZoneScan, Index: "zone_At" }
                    || await events.ExplainAsync(EventV2.Fields.At.EqualTo(at)) is not { Access: TkAccess.IndexScan, Index: "At" }
                    || await events.ExplainAsync(EventV2.Fields.Title.EqualTo("loaded 7")) is not { Access: TkAccess.UniqueLookup }
                    || (await events.GetAsync(first))?.Title != "written as version 1")
                {
                    await Console.Error.WriteLineAsync($"Bulk load failed: {loaded} loaded, {counted} found by index.");
                    return 1;
                }
            }

            var recent = await events.GetAsync(second);
            if (old is { Title: "written as version 1", Place: null, Cost: null, Mood: Mood.Calm, Weight: 0 }
                && old.At == at
                && old.At.Offset == at.Offset
                && recent is { Place: "here", Cost: 12.50m, Mood: Mood.Busy, Weight: 0.5 }
                && recent.Attachment.AsSpan().SequenceEqual(large)
                && titles is ["written as version 1", "written as version 2"]
                && database.IsEncrypted
                && database.IsCompressed)
            {
                return 0;
            }

            await Console.Error.WriteLineAsync($"Typed round-trip failed: {titles.Count} rows, old row {(old is null ? "missing" : old.Title)}.");
            return 1;
        }
    }
    finally
    {
        File.Delete(file);
    }
}

// Cifrado: AesGcm, HKDF, HMAC y el Argon2id administrado bajo Native AOT.
static async Task<int> EncryptedRoundTripAsync()
{
    var device = new MemoryStorageDevice();
    var options = new PagerOptions { KeyDerivation = new Argon2Parameters(256, 1, 2) };
    var key = new byte[8];
    var pager = await Pager.CreateAsync(device, options, "aot smoke password", CancellationToken.None);
    await using (pager)
    {
        var tree = new BTree(pager);
        await using (var transaction = await pager.BeginWriteAsync(CancellationToken.None))
        {
            for (var i = 0; i < 2000; i++)
            {
                BinaryPrimitives.WriteInt64BigEndian(key, i);
                await tree.PutAsync(transaction, key, key, CancellationToken.None);
            }

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Cambiar la contraseña ejercita la envoltura de la clave en ambas direcciones.
        await pager.ChangePasswordAsync("another password", CancellationToken.None);
        var scanned = 0;
        await foreach (var entry in tree.RangeAsync(default, default, CancellationToken.None))
        {
            scanned += entry.Key.Span.SequenceEqual(entry.Value.Span) ? 1 : 0;
        }

        if (scanned == 2000 && pager.IsEncrypted)
        {
            return 0;
        }

        await Console.Error.WriteLineAsync($"Encrypted round-trip failed: scanned {scanned}.");
        return 1;
    }
}

static async Task<int> RoundTripAsync(string name, Func<IStorageDevice> open, bool reopen)
{
    const int Rows = 5000;
    var key = new byte[8];
    var pager = await Pager.CreateAsync(open(), new PagerOptions(), CancellationToken.None);
    try
    {
        var tree = new BTree(pager);
        await using (var transaction = await pager.BeginWriteAsync(CancellationToken.None))
        {
            for (var i = 0; i < Rows; i++)
            {
                BinaryPrimitives.WriteInt64BigEndian(key, i);
                await tree.PutAsync(transaction, key, key, CancellationToken.None);
            }

            await transaction.CommitAsync(CancellationToken.None);
        }

        // Una base en memoria no sobrevive al cierre: sólo el archivo se reabre.
        if (reopen)
        {
            await pager.DisposeAsync();
            pager = await Pager.OpenAsync(open(), new PagerOptions(), CancellationToken.None);
            tree = new BTree(pager);
        }

        // Una transacción de lectura sigue viendo su estado aunque el escritor borre todo después.
        var scanned = 0;
        await using (var read = ReadTransaction.Begin(pager))
        {
            await using (var transaction = await pager.BeginWriteAsync(CancellationToken.None))
            {
                for (var i = 0; i < Rows; i += 2)
                {
                    BinaryPrimitives.WriteInt64BigEndian(key, i);
                    await tree.DeleteAsync(transaction, key, CancellationToken.None);
                }

                await transaction.CommitAsync(CancellationToken.None);
            }

            await foreach (var entry in BTree.RangeAsync(read, read.Root, default, default, CancellationToken.None))
            {
                scanned += entry.Key.Span.SequenceEqual(entry.Value.Span) ? 1 : 0;
            }
        }

        BinaryPrimitives.WriteInt64BigEndian(key, (Rows / 2) + 1);
        var found = await tree.GetAsync(key, new byte[8], CancellationToken.None);
        if (scanned == Rows && found == 8)
        {
            return 0;
        }

        await Console.Error.WriteLineAsync($"B+Tree round-trip on {name} failed: scanned {scanned}, point read {found}.");
        return 1;
    }
    finally
    {
        await pager.DisposeAsync();
    }
}
