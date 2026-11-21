// Copyright (c) Tekium. Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using TekiumDB.CrashChild;
using TekiumDB.Storage;

namespace TekiumDB.Tests.Transactions;

/// <summary>
/// Criterio de salida de la Fase 4: un proceso hijo escribe sobre un archivo real y se mata a sí mismo en
/// cada punto del commit; este proceso reabre el archivo y exige un estado consistente.
/// </summary>
public sealed class ProcessCrashTests
{
    private const int Seed = 23;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null, false)]
    [InlineData("crash test", false)]
    [InlineData(null, true)]
    [InlineData("crash test", true)]
    public async Task Killing_the_process_at_any_point_of_a_commit_leaves_a_consistent_database(string? password, bool compress)
    {
        // Con compresión cada página es una escritura aparte y hay el triple de puntos donde morir: una
        // carga más corta recorre los mismos caminos.
        var transactions = compress ? 4 : 8;
        var workload = new CrashWorkload(Seed, transactions, password, compress);
        var directory = Path.Combine(Path.GetTempPath(), $"tekium-crash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            // Una corrida completa dice qué operaciones hace la carga, y que el hijo sano la termina.
            var complete = await RunChildAsync(Path.Combine(directory, "complete.tkdb"), 0, "before", password, compress, transactions);
            Assert.Equal(0, complete.ExitCode);
            Assert.Equal(transactions, complete.Acknowledged);
            var operations = complete.Operations!;
            Assert.True(operations.Contains('L', StringComparison.Ordinal), "The workload never grows the file.");
            Assert.True(operations.Length > 30, "The workload is too small to say anything about the commit.");

            // Morir antes de cada operación; y, si es una escritura, también a medio hacerla.
            var cases = new List<(int At, string Mode)>();
            for (var at = 1; at <= operations.Length; at++)
            {
                cases.Add((at, "before"));
                if (operations[at - 1] == 'W')
                {
                    cases.Add((at, "torn"));
                }
            }

            await Parallel.ForEachAsync(
                cases,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = Ct },
                async (crash, cancellationToken) =>
                {
                    var path = Path.Combine(directory, $"{crash.At}-{crash.Mode}.tkdb");
                    var child = await RunChildAsync(path, crash.At, crash.Mode, password, compress, transactions);
                    var context = $"Killed {crash.Mode} operation {crash.At} of {operations.Length} ({operations[crash.At - 1]})";
                    Assert.True(child.ExitCode != 0 && child.Operations is null, $"{context}: the child was supposed to die.");

                    await CrashRecovery.AssertRecoversAsync(
                        workload, FileStorageDevice.Open(path, FileMode.Open), child.Acknowledged, context, cancellationToken);
                });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<(int ExitCode, int Acknowledged, string? Operations)> RunChildAsync(
        string path, int crashAt, string mode, string? password, bool compress, int transactions)
    {
        var start = new ProcessStartInfo { RedirectStandardOutput = true, UseShellExecute = false };

        // El hijo se compila junto a las pruebas. Con su lanzador si está; si no, con el host de dotnet.
        var name = typeof(CrashWorkload).Assembly.GetName().Name!;
        var launcher = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? name + ".exe" : name);
        if (File.Exists(launcher))
        {
            start.FileName = launcher;
        }
        else
        {
            start.FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, name + ".dll"));
        }

        start.ArgumentList.Add(path);
        start.ArgumentList.Add(Seed.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(transactions.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(crashAt.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(compress ? "compressed" : "plain");
        if (password is not null)
        {
            start.ArgumentList.Add(password);
        }

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);

        var acknowledged = -1;
        string? operations = null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("committed ", StringComparison.Ordinal))
            {
                acknowledged = int.Parse(line.AsSpan("committed ".Length), CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("done ", StringComparison.Ordinal))
            {
                operations = line["done ".Length..];
            }
        }

        return (process.ExitCode, acknowledged, operations);
    }
}
