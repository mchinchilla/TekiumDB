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

// Fase 0.5: escribir en un archivo real, cerrar, reabrir y leer, todo por el camino asíncrono.
var path = Path.Combine(Path.GetTempPath(), $"tekium-aot-{Guid.NewGuid():N}.tkdb");
try
{
    const int Rows = 5000;
    var key = new byte[8];
    await using (var pager = await Pager.CreateAsync(
        FileStorageDevice.Open(path, FileMode.CreateNew), PageSize.Default, CancellationToken.None))
    {
        var tree = new BTree(pager);
        await using var transaction = await pager.BeginWriteAsync(CancellationToken.None);
        for (var i = 0; i < Rows; i++)
        {
            BinaryPrimitives.WriteInt64BigEndian(key, i);
            await tree.PutAsync(transaction, key, key, CancellationToken.None);
        }

        await transaction.CommitAsync(CancellationToken.None);
    }

    await using var reopened = await Pager.OpenAsync(
        FileStorageDevice.Open(path, FileMode.Open), CancellationToken.None);
    var reader = new BTree(reopened);
    var scanned = 0;
    await foreach (var entry in reader.RangeAsync(default, default, CancellationToken.None))
    {
        scanned += entry.Key.Span.SequenceEqual(entry.Value.Span) ? 1 : 0;
    }

    BinaryPrimitives.WriteInt64BigEndian(key, Rows / 2);
    var found = await reader.GetAsync(key, new byte[8], CancellationToken.None);
    if (scanned != Rows || found != 8)
    {
        await Console.Error.WriteLineAsync($"B+Tree round-trip failed: scanned {scanned}, point read {found}.");
        failures++;
    }
}
finally
{
    File.Delete(path);
}

await Console.Out.WriteLineAsync(
    $"TekiumDB AOT smoke: {(failures == 0 ? "OK" : "FAILED")} " +
    $"(dynamic code supported: {RuntimeFeature.IsDynamicCodeSupported})");

return failures;
