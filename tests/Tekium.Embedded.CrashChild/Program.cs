// Copyright (c) Tekium. Licensed under the MIT License.

using System.Globalization;
using Tekium.Embedded.CrashChild;
using Tekium.Embedded.Storage;

// Uso: <archivo> <semilla> <transacciones> <operación en la que muere, 0 = nunca> <before|torn> [contraseña]
// Crea la base, ejecuta la carga y avisa por la salida estándar de cada paso confirmado. El padre lee esas
// líneas para saber qué se le había prometido al llamador antes de que el proceso muriera.
var workload = new CrashWorkload(
    int.Parse(args[1], CultureInfo.InvariantCulture),
    int.Parse(args[2], CultureInfo.InvariantCulture),
    args.Length > 5 ? args[5] : null);
var device = new CrashingDevice(
    FileStorageDevice.Open(args[0], FileMode.CreateNew),
    int.Parse(args[3], CultureInfo.InvariantCulture),
    torn: args[4] == "torn");

var pager = await workload.CreateAsync(device, CancellationToken.None);
await using (pager)
{
    Report("committed 0");
    await workload.RunAsync(pager, 0, static count => Report($"committed {count}"), CancellationToken.None);
}

Report($"done {device.Operations}");
return 0;

static void Report(string line)
{
    Console.Out.WriteLine(line);
    Console.Out.Flush();
}
