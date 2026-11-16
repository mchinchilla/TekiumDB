// Copyright (c) Tekium. Licensed under the MIT License.

using BenchmarkDotNet.Running;

// Uso: dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- --filter '*'
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
