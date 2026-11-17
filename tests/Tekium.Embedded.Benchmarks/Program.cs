// Copyright (c) Tekium. Licensed under the MIT License.

using BenchmarkDotNet.Running;
using Tekium.Embedded.Benchmarks;

// Uso: dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- --filter '*'
//      dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- space
//      dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- ratio
//      dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- feed
//      dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- range [filas]
//      dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- uuid [filas]
//      dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- backup [filas]
//      dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- vector [vectores] [dimensiones] [plain|quantized] [clustered|uniform] [M]
//      dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- bulk one|bulk|shuffled|indexed-one|indexed-bulk|sqlite [filas]
if (args is ["space"])
{
    await SpaceAmplification.RunAsync();
    return;
}

if (args is ["ratio"])
{
    await CompressionRatio.RunAsync();
    return;
}

if (args is ["feed"])
{
    await ChangeFeedBenchmarks.ReportConsumerAsync();
    await ChangeFeedBenchmarks.ReportSpaceAsync();
    return;
}

if (args is ["bulk", var mode, ..])
{
    await BulkLoadReport.RunAsync(mode, args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 10_000_000);
    return;
}

if (args is ["vector", ..])
{
    var culture = System.Globalization.CultureInfo.InvariantCulture;
    await VectorSearchReport.RunAsync(
        args.Length > 1 ? int.Parse(args[1], culture) : 1_000_000,
        args.Length > 2 ? int.Parse(args[2], culture) : 128,
        args.Length > 3 && args[3] == "quantized",
        args.Length <= 4 || args[4] != "uniform",
        args.Length > 5 ? int.Parse(args[5], culture) : 16);
    return;
}

if (args is ["backup", ..])
{
    await BackupReport.RunAsync(args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 1_000_000);
    return;
}

if (args is ["procedures", ..])
{
    await ProcedureReport.RunAsync(args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 10_000);
    return;
}

if (args is ["routines", ..])
{
    await RoutineReport.RunAsync(args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 1_000_000);
    return;
}

if (args is ["uuid", ..])
{
    await UuidInsertReport.RunAsync(args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 1_000_000);
    return;
}

if (args is ["range-write", ..])
{
    await DateRangeReport.WritesAsync(args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 1_000_000);
    return;
}

if (args is ["range", ..])
{
    await DateRangeReport.RunAsync(args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 10_000_000);
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
