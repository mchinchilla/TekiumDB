// Copyright (c) Tekium. Licensed under the MIT License.

using BenchmarkDotNet.Running;
using Tekium.Embedded.Benchmarks;

// Uso: dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- --filter '*'
//      dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- space
if (args is ["space"])
{
    await SpaceAmplification.RunAsync();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
