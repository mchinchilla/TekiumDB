// Copyright (c) Tekium. Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;

namespace Tekium.Embedded.Benchmarks;

/// <summary>
/// Backup en caliente: la latencia de los commits del escritor sin backup, con backups corriendo uno tras
/// otro, y otra vez sin; y cuánto crece el archivo mientras tanto. Se corre con <c>-- backup [filas]</c>.
/// </summary>
internal static class BackupReport
{
    private const int Commits = 400;
    private const int RowsPerCommit = 100;

    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync(int rows)
    {
        var directory = Workload.NewDirectory();
        try
        {
            var ids = new Guid[rows];
            for (var i = 0; i < rows; i++)
            {
                ids[i] = Guid.CreateVersion7(Epoch.AddMilliseconds(i));
            }

            var path = Path.Combine(directory, "live.tkdb");
            var database = await TekiumDb.OpenAsync(new DbOptions { Path = path, CacheSizeMb = 1024 });
            await using (database)
            {
                var invoices = database.Table<InvoiceLoaded>();
                await using (var writer = await database.BeginWriteAsync())
                {
                    await invoices.BulkInsertAsync(writer, ids.Select(static (id, i) => Row(id, i, 0)));
                    await writer.CommitAsync();
                }

                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{rows:N0} filas con dos índices, {Pages(database) * 8 / 1024.0:F0} MiB en uso. Cada commit actualiza {RowsPerCommit} filas al azar y es durable; {Commits} commits por tramo."));
                Console.WriteLine("| Tramo | Mediana | p95 | p99 | Backups terminados | Archivo al final |");
                Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: |");

                var random = new Random(4);
                await WriteAsync(database, ids, random, 50);
                Report("Sin backup", await WriteAsync(database, ids, random, Commits), 0, default, database);

                // Backups uno tras otro mientras el escritor hace sus commits.
                using var stop = new CancellationTokenSource();
                var finished = 0;
                var durations = new List<TimeSpan>();
                long backupBytes = 0;
                var backing = Task.Run(async () =>
                {
                    for (var i = 0; !stop.IsCancellationRequested; i++)
                    {
                        var target = Path.Combine(directory, $"backup-{i}.tkdb");
                        var watch = Stopwatch.StartNew();
                        var report = await database.BackupAsync(target);
                        durations.Add(watch.Elapsed);
                        backupBytes = report.Bytes;
                        finished++;
                        File.Delete(target);
                    }
                });
                var during = await WriteAsync(database, ids, random, Commits);
                await stop.CancelAsync();
                await backing;
                Report("Con backups corriendo", during, finished, durations.Count > 0 ? TimeSpan.FromTicks((long)durations.Average(static d => d.Ticks)) : default, database);

                Report("Sin backup, después", await WriteAsync(database, ids, random, Commits), 0, default, database);
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Cada backup: {durations.Average(static d => d.TotalSeconds):F2} s en promedio, {backupBytes / 1048576.0:F0} MiB."));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static long Pages(TekiumDb database) => database.Pager.Current.PageCount - database.Pager.FreePageCount;

    private static InvoiceLoaded Row(Guid id, int i, int version) => new()
    {
        Id = id,
        Customer = i % 1000,
        IssuedAt = Epoch.AddSeconds(i),
        Total = (i % 10_000) + version,
    };

    private static async Task<double[]> WriteAsync(TekiumDb database, Guid[] ids, Random random, int commits)
    {
        var invoices = database.Table<InvoiceLoaded>();
        var times = new double[commits];
        for (var commit = 0; commit < commits; commit++)
        {
            var started = Stopwatch.GetTimestamp();
            await using (var writer = await database.BeginWriteAsync())
            {
                for (var i = 0; i < RowsPerCommit; i++)
                {
                    var row = random.Next(ids.Length);
                    await invoices.UpsertAsync(writer, Row(ids[row], row, commit + 1));
                }

                await writer.CommitAsync();
            }

            times[commit] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        Array.Sort(times);
        return times;
    }

    private static void Report(string name, double[] times, int backups, TimeSpan each, TekiumDb database)
    {
        _ = each;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"| {name} | {times[times.Length / 2]:F2} ms | {times[(int)(times.Length * 0.95)]:F2} ms | {times[(int)(times.Length * 0.99)]:F2} ms | {(backups == 0 ? "—" : backups.ToString(CultureInfo.InvariantCulture))} | {database.Pager.Current.PageCount * 8 / 1024.0:F0} MiB |"));
    }
}
