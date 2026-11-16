// Copyright (c) Tekium. Licensed under the MIT License.

using System.Runtime.CompilerServices;
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

await Console.Out.WriteLineAsync(
    $"TekiumDB AOT smoke: {(failures == 0 ? "OK" : "FAILED")} " +
    $"(dynamic code supported: {RuntimeFeature.IsDynamicCodeSupported})");

return failures;
