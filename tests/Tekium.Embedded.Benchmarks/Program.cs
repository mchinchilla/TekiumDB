// Copyright (c) Tekium. Licensed under the MIT License.

using BenchmarkDotNet.Running;
using Tekium.Embedded.Benchmarks;

// Uso: dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- --filter '*'
//      dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- space
//      dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- ratio
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

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
