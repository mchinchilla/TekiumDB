// Copyright (c) Tekium. Licensed under the MIT License.

using BenchmarkDotNet.Running;
using Tekium.Embedded.Benchmarks;

// Uso: dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- --filter '*'
//      dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- space
//      dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- ratio
//      dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- feed
//      dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- range [filas]
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
