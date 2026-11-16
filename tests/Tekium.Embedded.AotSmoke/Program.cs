// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Storage;

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

await Console.Out.WriteLineAsync(
    $"TekiumDB AOT smoke: {(failures == 0 ? "OK" : "FAILED")} " +
    $"(dynamic code supported: {RuntimeFeature.IsDynamicCodeSupported})");

return failures;

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

        var scanned = 0;
        await foreach (var entry in tree.RangeAsync(default, default, CancellationToken.None))
        {
            scanned += entry.Key.Span.SequenceEqual(entry.Value.Span) ? 1 : 0;
        }

        BinaryPrimitives.WriteInt64BigEndian(key, Rows / 2);
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
