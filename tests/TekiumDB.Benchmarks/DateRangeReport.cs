// Copyright (c) Tekium. Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TekiumDB.Benchmarks;

/// <summary>Una muestra por segundo, con un zone map sobre su fecha.</summary>
[TkTable("samples_zoned")]
public sealed partial class SampleZoned
{
    [TkKey]
    public Guid Id { get; set; }

    [TkZoneMap]
    public DateTimeOffset At { get; set; }

    public int Sensor { get; set; }

    public double Value { get; set; }

    public string Notes { get; set; } = string.Empty;
}

/// <summary>La misma muestra con un índice secundario sobre su fecha.</summary>
[TkTable("samples_indexed")]
public sealed partial class SampleIndexed
{
    [TkKey]
    public Guid Id { get; set; }

    [TkIndex]
    public DateTimeOffset At { get; set; }

    public int Sensor { get; set; }

    public double Value { get; set; }

    public string Notes { get; set; } = string.Empty;
}

/// <summary>La misma muestra sin nada sobre su fecha.</summary>
[TkTable("samples_plain")]
public sealed partial class SamplePlain
{
    [TkKey]
    public Guid Id { get; set; }

    public DateTimeOffset At { get; set; }

    public int Sensor { get; set; }

    public double Value { get; set; }

    public string Notes { get; set; } = string.Empty;
}

/// <summary>
/// Criterio de la Fase 7: un rango de fechas sobre diez millones de filas por debajo del umbral. El umbral,
/// fijado antes de medir: por zone map, más rápido que SQLite con un índice sobre la fecha, en rangos de
/// una hora, un día y un mes, construyendo la fila completa. <c>-- range [filas]</c>.
/// </summary>
internal static class DateRangeReport
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync(int rows)
    {
        var directory = Workload.NewDirectory();
        try
        {
            var path = Path.Combine(directory, "range.tkdb");
            var watch = Stopwatch.StartNew();
            await using (var database = await TekiumDb.OpenAsync(new DbOptions { Path = path }))
            {
                await using var writer = await database.BeginWriteAsync();
                await database.Table<SampleZoned>().BulkInsertAsync(writer, Rows(rows).Select(static row => new SampleZoned { Id = row.Id, At = row.At, Sensor = row.Sensor, Value = row.Value, Notes = "Sample" }));
                await database.Table<SampleIndexed>().BulkInsertAsync(writer, Rows(rows).Select(static row => new SampleIndexed { Id = row.Id, At = row.At, Sensor = row.Sensor, Value = row.Value, Notes = "Sample" }));
                await writer.CommitAsync();
            }

            await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"loaded 2 x {rows} rows in {watch.Elapsed.TotalSeconds:F1} s, file {new FileInfo(path).Length / 1048576.0:F0} MiB"));

            watch.Restart();
            using var sqlite = Workload.OpenSqlite(Path.Combine(directory, "range.sqlite"));
            Workload.Execute(sqlite, "CREATE TABLE samples (id BLOB PRIMARY KEY, at INTEGER NOT NULL, sensor INTEGER NOT NULL, value REAL NOT NULL, notes TEXT NOT NULL) WITHOUT ROWID;");
            using (var transaction = sqlite.BeginTransaction())
            using (var insert = sqlite.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO samples VALUES ($id, $at, $sensor, $value, 'Sample');";
                var id = insert.Parameters.Add("$id", SqliteType.Blob);
                var at = insert.Parameters.Add("$at", SqliteType.Integer);
                var sensor = insert.Parameters.Add("$sensor", SqliteType.Integer);
                var value = insert.Parameters.Add("$value", SqliteType.Real);
                insert.Prepare();
                foreach (var row in Rows(rows))
                {
                    id.Value = row.Id.ToByteArray(bigEndian: true);
                    at.Value = row.At.UtcTicks;
                    sensor.Value = row.Sensor;
                    value.Value = row.Value;
                    insert.ExecuteNonQuery();
                }

                transaction.Commit();
            }

            Workload.Execute(sqlite, "CREATE INDEX ix_at ON samples (at);");
            await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"sqlite loaded and indexed in {watch.Elapsed.TotalSeconds:F1} s"));
            using var select = sqlite.CreateCommand();
            select.CommandText = "SELECT id, at, sensor, value, notes FROM samples WHERE at BETWEEN $a AND $b;";
            var low = select.Parameters.Add("$a", SqliteType.Integer);
            var high = select.Parameters.Add("$b", SqliteType.Integer);
            select.Prepare();

            // Cada rango empieza a mitad de la tabla. Primero con la caché del motor vacía, después caliente.
            var middle = Epoch.AddSeconds(rows / 2);
            foreach (var (name, length) in new[] { ("one hour", TimeSpan.FromHours(1)), ("one day", TimeSpan.FromDays(1)), ("thirty days", TimeSpan.FromDays(30)) })
            {
                var to = middle + length;
                var runs = length <= TimeSpan.FromHours(1) ? 200 : length <= TimeSpan.FromDays(1) ? 40 : 8;
                await Console.Out.WriteLineAsync($"--- {name}");
                await ReportAsync("zone map", path, runs, database => CountAsync(database.Table<SampleZoned>().QueryAsync(SampleZoned.Fields.At.Between(middle, to))));
                await ReportAsync("secondary index", path, runs, database => CountAsync(database.Table<SampleIndexed>().QueryAsync(SampleIndexed.Fields.At.Between(middle, to))));
                if (length <= TimeSpan.FromDays(1))
                {
                    var from = (int)(middle - Epoch).TotalSeconds;
                    await ReportAsync("table scan", path, 4, database => CountAsync(database.Table<SampleZoned>().QueryAsync(SampleZoned.Fields.Sensor.Between(from, from + (int)length.TotalSeconds))));
                }

                var times = new List<double>();
                long count = 0;
                for (var i = 0; i < runs; i++)
                {
                    watch.Restart();
                    low.Value = middle.UtcTicks;
                    high.Value = to.UtcTicks;
                    count = 0;
                    using var reader = select.ExecuteReader();
                    while (reader.Read())
                    {
                        var row = new SamplePlain
                        {
                            Id = new Guid(reader.GetFieldValue<byte[]>(0), bigEndian: true),
                            At = new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero),
                            Sensor = reader.GetInt32(2),
                            Value = reader.GetDouble(3),
                            Notes = reader.GetString(4),
                        };
                        count += row.Sensor >= 0 ? 1 : 0;
                    }

                    times.Add(watch.Elapsed.TotalMilliseconds);
                }

                Print("sqlite + index", count, times);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Lo que cuesta al escribir: las mismas filas, una por una, sin nada, con zone map y con índice.</summary>
    public static async Task WritesAsync(int rows)
    {
        async Task<(double Seconds, long Bytes)> RunAsync<T>(Func<(Guid Id, DateTimeOffset At, int Sensor, double Value), T> make)
            where T : class, ITkRow<T>
        {
            var best = double.MaxValue;
            long size = 0;
            for (var run = 0; run < 5; run++)
            {
                var directory = Workload.NewDirectory();
                try
                {
                    var path = Path.Combine(directory, "write.tkdb");
                    var watch = Stopwatch.StartNew();
                    await using (var database = await TekiumDb.OpenAsync(new DbOptions { Path = path }))
                    {
                        var table = database.Table<T>();
                        await using var writer = await database.BeginWriteAsync();
                        foreach (var row in Rows(rows))
                        {
                            await table.InsertAsync(writer, make(row));
                        }

                        await writer.CommitAsync();
                    }

                    best = Math.Min(best, watch.Elapsed.TotalSeconds);
                    size = new FileInfo(path).Length;
                }
                finally
                {
                    Directory.Delete(directory, recursive: true);
                }
            }

            return (best, size);
        }

        var plain = await RunAsync(static row => new SamplePlain { Id = row.Id, At = row.At, Sensor = row.Sensor, Value = row.Value, Notes = "Sample" });
        var zoned = await RunAsync(static row => new SampleZoned { Id = row.Id, At = row.At, Sensor = row.Sensor, Value = row.Value, Notes = "Sample" });
        var indexed = await RunAsync(static row => new SampleIndexed { Id = row.Id, At = row.At, Sensor = row.Sensor, Value = row.Value, Notes = "Sample" });
        foreach (var (name, result) in new[] { ("nothing", plain), ("zone map", zoned), ("secondary index", indexed) })
        {
            await Console.Out.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"{name,-16} {rows} rows one by one: {result.Seconds * 1000,8:F1} ms  ({result.Seconds / plain.Seconds:F2}x)  file {result.Bytes / 1048576.0,6:F1} MiB"));
        }
    }

    private static async Task ReportAsync(string name, string path, int runs, Func<TekiumDb, Task<long>> query)
    {
        await using var database = await TekiumDb.OpenAsync(new DbOptions { Path = path, CacheSizeMb = 4096 });
        var times = new List<double>();
        long count = 0;
        for (var i = 0; i < runs; i++)
        {
            var watch = Stopwatch.StartNew();
            count = await query(database);
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        Print(name, count, times);
    }

    /// <summary>La primera corrida es con la caché del motor vacía; después, la mediana de la segunda mitad, ya con el código compilado del todo.</summary>
    private static void Print(string name, long count, List<double> times)
    {
        var warm = times.Skip(Math.Max(1, times.Count / 2)).Order().ToList();
        Console.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture, $"{name,-16} {count,9} rows   first {times[0],9:F2} ms   warm {warm[warm.Count / 2],9:F2} ms"));
    }

    private static async Task<long> CountAsync<T>(IAsyncEnumerable<T> rows)
    {
        long count = 0;
        await foreach (var row in rows)
        {
            count += row is null ? 0 : 1;
        }

        return count;
    }

    private static IEnumerable<(Guid Id, DateTimeOffset At, int Sensor, double Value)> Rows(int rows)
    {
        for (var i = 0; i < rows; i++)
        {
            // Una por segundo, con la clave creciendo con el tiempo, como un UUID v7.
            yield return (new Guid(i >> 16, (short)i, 0x7000, 0x80, 0, 0, 0, 0, 0, 0, 1), Epoch.AddSeconds(i), i, i * 0.5);
        }
    }
}
