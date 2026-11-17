// Copyright (c) Tekium. Licensed under the MIT License.

using System.Diagnostics.Metrics;
using Tekium.Embedded.Security;
using Tekium.Embedded.Tests.Storage;

namespace Tekium.Embedded.Tests.Database;

/// <summary>
/// Contadores de diagnóstico por <see cref="Meter"/>: sólo existen si la base se abre con
/// <see cref="DbOptions.Diagnostics"/>, y lo que dicen es lo que pasó.
/// </summary>
public sealed class DiagnosticsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Cache_hits_and_misses_are_counted()
    {
        var device = TestDevice.Memory();
        await using (var first = await OpenAsync(device, "counted", diagnostics: true))
        {
            await FillAsync(first, 400);
        }

        // Recién abierta, la caché está vacía: leer todo son fallos; leerlo otra vez, aciertos.
        await using var database = await OpenAsync(device, "counted", diagnostics: true);
        using var meter = new Readings("counted");
        _ = await ToListAsync(database.Table<Ticket>().ScanAsync(Ct));
        var cold = meter.Observe();
        _ = await ToListAsync(database.Table<Ticket>().ScanAsync(Ct));
        var warm = meter.Observe();

        Assert.True(cold["tekium.cache.misses"] >= 10, $"{cold["tekium.cache.misses"]} misses reading a cold table.");
        Assert.Equal(cold["tekium.cache.misses"], warm["tekium.cache.misses"]);
        Assert.True(warm["tekium.cache.hits"] - cold["tekium.cache.hits"] >= cold["tekium.cache.misses"] - 2);
    }

    [Fact]
    public async Task Every_commit_reports_how_long_it_took_and_how_many_pages_it_wrote()
    {
        await using var database = await OpenAsync(TestDevice.Memory(), "commits", diagnostics: true);
        using var meter = new Readings("commits");

        await FillAsync(database, 300);
        await FillAsync(database, 1);
        await using (var empty = await database.BeginWriteAsync(Ct))
        {
            await empty.CommitAsync(Ct);
        }

        // Dos commits que escribieron algo; el vacío no escribe ni se cuenta.
        Assert.Equal(2, meter.Recorded["tekium.commit.duration"].Count);
        Assert.All(meter.Recorded["tekium.commit.duration"], static milliseconds => Assert.InRange(milliseconds, 0.0001, 60_000));
        Assert.Equal(2, meter.Recorded["tekium.commit.pages"].Count);
        Assert.True(meter.Recorded["tekium.commit.pages"][0] > meter.Recorded["tekium.commit.pages"][1]);
        Assert.InRange(meter.Recorded["tekium.commit.pages"][1], 2, 30);
    }

    [Fact]
    public async Task Live_snapshots_and_the_age_of_the_oldest_are_reported()
    {
        await using var database = await OpenAsync(TestDevice.Memory(), "snapshots", diagnostics: true);
        using var meter = new Readings("snapshots");
        await FillAsync(database, 10);
        Assert.Equal(0, meter.Observe()["tekium.snapshots.live"]);

        // Dos lectores sobre el mismo estado y, bastante después, uno sobre el siguiente.
        var old = database.BeginRead();
        var twin = database.BeginRead();
        await Task.Delay(80, Ct);
        await FillAsync(database, 10);
        var recent = database.BeginRead();
        var all = meter.Observe();
        Assert.Equal(3, all["tekium.snapshots.live"]);
        Assert.InRange(all["tekium.snapshots.oldest_age"], 0.08, 60);

        // Al soltar los viejos, el más antiguo pasa a ser el reciente; sin ninguno, no hay edad.
        await old.DisposeAsync();
        await twin.DisposeAsync();
        Assert.InRange(meter.Observe()["tekium.snapshots.oldest_age"], 0, 0.079);
        await FillAsync(database, 10);
        var one = meter.Observe();
        Assert.Equal(1, one["tekium.snapshots.live"]);
        Assert.True(one["tekium.snapshots.oldest_age"] > 0);
        await recent.DisposeAsync();
        var none = meter.Observe();
        Assert.Equal((0.0, 0.0), (none["tekium.snapshots.live"], none["tekium.snapshots.oldest_age"]));
    }

    [Fact]
    public async Task The_depth_of_each_tree_written_is_reported_by_name()
    {
        await using var database = await OpenAsync(TestDevice.Memory(), "depths", diagnostics: true);
        using var meter = new Readings("depths");
        await FillAsync(database, 5);
        var small = meter.Observe();
        Assert.Equal(1, small["tekium.tree.depth|table tickets"]);
        Assert.Equal(1, small["tekium.tree.depth|index tickets.Owner"]);

        await FillAsync(database, 2000);
        var large = meter.Observe();
        Assert.True(large["tekium.tree.depth|table tickets"] >= 2);
        Assert.True(large["tekium.tree.depth|index tickets.TitleLower"] >= 2);
    }

    [Fact]
    public async Task Without_the_flag_there_is_no_meter_and_each_database_reports_under_its_own_name()
    {
        using var all = new Readings(null);
        await using (var silent = await OpenAsync(TestDevice.Memory(), "silent", diagnostics: false))
        {
            await FillAsync(silent, 50);
            _ = await ToListAsync(silent.Table<Ticket>().ScanAsync(Ct));
            Assert.Empty(all.Observe());
            Assert.Empty(all.Recorded);
        }

        await using var a = await OpenAsync(TestDevice.Memory(), "first", diagnostics: true);
        await using var b = await OpenAsync(TestDevice.Memory(), "second", diagnostics: true);
        using var first = new Readings("first");
        using var second = new Readings("second");
        await FillAsync(a, 50);
        Assert.Single(first.Recorded["tekium.commit.pages"]);
        Assert.False(second.Recorded.ContainsKey("tekium.commit.pages"));
    }

    [Fact]
    public async Task Closing_the_database_stops_its_meter()
    {
        using var meter = new Readings("closed");
        await using (var database = await OpenAsync(TestDevice.Memory(), "closed", diagnostics: true))
        {
            await FillAsync(database, 10);
            Assert.NotEmpty(meter.Observe());
        }

        Assert.Empty(meter.Observe());
    }

    private static ValueTask<TekiumDb> OpenAsync(TestDevice device, string name, bool diagnostics) =>
        TekiumDb.OpenAsync(
            _ => device,
            new DbOptions
            {
                InMemory = true,
                PageSize = 4096,
                CacheSizeMb = 4,
                Diagnostics = diagnostics,
                DiagnosticsName = name,
                KeyDerivation = new Argon2Parameters(MemoryKiB: 64, Iterations: 1, Parallelism: 1),
            },
            Ct);

    private static async Task FillAsync(TekiumDb database, int rows)
    {
        await using var writer = await database.BeginWriteAsync(Ct);
        for (var i = 0; i < rows; i++)
        {
            await database.Table<Ticket>().UpsertAsync(writer, new Ticket { Id = i, Owner = $"owner{i % 17}", Title = $"Ticket {i}", Body = new string('b', 150) }, Ct);
        }

        await writer.CommitAsync(Ct);
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> rows)
    {
        var list = new List<T>();
        await foreach (var row in rows)
        {
            list.Add(row);
        }

        return list;
    }

    /// <summary>Escucha los instrumentos de TekiumDB de una base (o de todas) como lo haría un exportador de métricas.</summary>
    private sealed class Readings : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Dictionary<string, double> _observed = [];

        public Readings(string? database)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == TekiumDb.MeterName
                    && (database is null || instrument.Meter.Tags!.Any(tag => tag.Key == "tekium.db" && (string?)tag.Value == database)))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Take(instrument, value, tags));
            _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Take(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Take(instrument, value, tags));
            _listener.Start();
        }

        /// <summary>Gets what histograms recorded, in order.</summary>
        public Dictionary<string, List<double>> Recorded { get; } = [];

        /// <summary>Lee los contadores y medidores observables: el último valor de cada uno, por nombre (y árbol).</summary>
        public Dictionary<string, double> Observe()
        {
            lock (_observed)
            {
                _observed.Clear();
            }

            _listener.RecordObservableInstruments();
            lock (_observed)
            {
                return new Dictionary<string, double>(_observed);
            }
        }

        public void Dispose() => _listener.Dispose();

        private void Take(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            if (instrument.IsObservable)
            {
                var name = instrument.Name;
                foreach (var tag in tags)
                {
                    name += tag.Key == "tekium.tree" ? $"|{tag.Value}" : string.Empty;
                }

                lock (_observed)
                {
                    _observed[name] = value;
                }
            }
            else
            {
                lock (Recorded)
                {
                    if (!Recorded.TryGetValue(instrument.Name, out var values))
                    {
                        Recorded[instrument.Name] = values = [];
                    }

                    values.Add(value);
                }
            }
        }
    }
}
