// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Catalog;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Tests.Indexing;
using Tekium.Embedded.Tests.Storage;

namespace Tekium.Embedded.Tests.Database;

/// <summary>
/// Zone maps: por cada ventana de tiempo, entre qué claves están las filas de esa ventana. Una consulta
/// por rango de fechas lee sólo ese tramo de la tabla, y nunca devuelve de menos.
/// </summary>
public abstract class ZoneMapTests : DatabaseTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_range_of_dates_gives_the_same_rows_as_filtering_the_whole_table_whatever_was_written()
    {
        var device = NewDevice();
        var random = new Random(7);
        var model = new SortedDictionary<long, Reading>();
        await using (var database = await OpenAsync(device))
        {
            var readings = database.Table<Reading>();
            long next = 0;
            for (var round = 0; round < 14; round++)
            {
                var changed = new SortedDictionary<long, Reading>(model);
                await using (var writer = await database.BeginWriteAsync(Ct))
                {
                    for (var i = 0; i < 200; i++)
                    {
                        switch (random.Next(10))
                        {
                            case 0:
                                // Una fila vieja cambia de fecha: su ventana nueva tiene que aprender su clave.
                                var moved = NewReading(random, random.NextInt64(Math.Max(next, 1)), random.Next(4000));
                                await readings.UpsertAsync(writer, moved, Ct);
                                changed[moved.Id] = moved;
                                break;
                            case 1:
                                var gone = random.NextInt64(Math.Max(next, 1));
                                Assert.Equal(changed.Remove(gone), await readings.DeleteAsync(writer, gone, Ct));
                                break;
                            default:
                                // Lo normal en una serie de tiempo: clave que crece, fecha que avanza con algo de desorden.
                                var reading = NewReading(random, next, (int)next + random.Next(-30, 30));
                                next++;
                                await readings.InsertAsync(writer, reading, Ct);
                                changed[reading.Id] = reading;
                                break;
                        }
                    }

                    await AssertRangesAsync(readings, writer, changed.Values);
                    if (round % 4 != 3)
                    {
                        await writer.CommitAsync(Ct);
                        model = changed;
                    }
                }

                await AssertRangesAsync(readings, null, model.Values);
                await AssertNoLeaksAsync(database);
            }
        }

        await using var reopened = await OpenAsync(device);
        await AssertRangesAsync(reopened.Table<Reading>(), null, model.Values);
    }

    /// <summary>Las claves de una ventana pueden llegar en cualquier orden, también dentro de una misma transacción.</summary>
    [Fact]
    public async Task A_window_takes_in_keys_below_and_above_the_ones_it_has_in_whatever_order_they_come()
    {
        await using var database = await OpenAsync(NewDevice());
        var readings = database.Table<Reading>();
        var hour = Reading.Fields.At.Between(Epoch, Epoch.AddMinutes(59));
        await InTransactionAsync(database, async writer =>
        {
            await readings.InsertAsync(writer, new Reading { Id = 100, At = Epoch.AddMinutes(10) }, Ct);
            await readings.InsertAsync(writer, new Reading { Id = 5, At = Epoch.AddMinutes(20) }, Ct);
            await readings.InsertAsync(writer, new Reading { Id = 300, At = Epoch.AddMinutes(30) }, Ct);
            await readings.InsertAsync(writer, new Reading { Id = 50, At = Epoch.AddHours(9) }, Ct);
        });
        Assert.Equal([5L, 100L, 300L], (await ToListAsync(readings.QueryAsync(hour, Ct))).Select(static row => row.Id));

        // Y sobre una ventana ya guardada, en otra transacción.
        await InTransactionAsync(database, async writer =>
        {
            await readings.InsertAsync(writer, new Reading { Id = 900, At = Epoch.AddMinutes(40) }, Ct);
            await readings.InsertAsync(writer, new Reading { Id = 1, At = Epoch.AddMinutes(50) }, Ct);
        });
        Assert.Equal([1L, 5L, 100L, 300L, 900L], (await ToListAsync(readings.QueryAsync(hour, Ct))).Select(static row => row.Id));
        Assert.Equal(new TkPlan(TkAccess.ZoneScan, "zone_At"), await readings.ExplainAsync(hour, Ct));
    }

    /// <summary>Lo que justifica el zone map: con la caché vacía, un rango lee un tramo de la tabla, no la tabla.</summary>
    [Fact]
    public async Task A_range_of_dates_reads_only_the_stretch_of_the_table_that_holds_it()
    {
        var device = NewDevice();
        await using (var database = await OpenAsync(device))
        {
            await using var writer = await database.BeginWriteAsync(Ct);
            await database.Table<Reading>().BulkInsertAsync(
                writer, Enumerable.Range(0, 60_000).Select(static i => new Reading { Id = i, At = Epoch.AddMinutes(i), Value = i }), Ct);
            await writer.CommitAsync(Ct);
        }

        async Task<(int Rows, int Reads)> CountAsync(TkFilter<Reading> filter)
        {
            await using var database = await OpenAsync(device);
            var before = device.ReadCount;
            var rows = (await ToListAsync(database.Table<Reading>().QueryAsync(filter, Ct))).Count;
            return (rows, device.ReadCount - before);
        }

        // Tres horas de mil: 181 filas. Sin zone map habría que leer la tabla entera para encontrarlas.
        var range = await CountAsync(Reading.Fields.At.Between(Epoch.AddHours(500), Epoch.AddHours(503)));
        var everything = await CountAsync(Reading.Fields.Value.Between(30_000, 30_180));
        Assert.Equal(181, range.Rows);
        Assert.Equal(181, everything.Rows);
        Assert.True(everything.Reads > 400, $"The whole table is only {everything.Reads} reads.");
        Assert.True(range.Reads < 30, $"The range read {range.Reads} pages of {everything.Reads}.");
    }

    [Fact]
    public async Task A_zone_map_is_built_by_the_first_write_kept_by_bulk_loads_and_dropped_by_who_does_not_declare_it()
    {
        var device = NewDevice();
        await using var database = await OpenAsync(device);
        var plain = database.Table<ReadingPlain>();
        var readings = database.Table<Reading>();
        var from = Epoch.AddMinutes(1000);
        var to = Epoch.AddMinutes(1500);
        var range = Reading.Fields.At.Between(from, to);
        await InTransactionAsync(database, async writer =>
        {
            for (var i = 0; i < 3000; i++)
            {
                await plain.InsertAsync(writer, new ReadingPlain { Id = i, At = Epoch.AddMinutes(i), Value = i }, Ct);
            }
        });

        // Todavía no existe: se recorre la tabla. La primera escritura lo construye con lo que ya había.
        Assert.Equal(new TkPlan(TkAccess.TableScan, null), await readings.ExplainAsync(range, Ct));
        Assert.Equal(501, (await ToListAsync(readings.QueryAsync(range, Ct))).Count);
        await InTransactionAsync(database, writer => readings.InsertAsync(writer, new Reading { Id = 5000, At = Epoch.AddMinutes(1200) }, Ct));
        Assert.Equal(new TkPlan(TkAccess.ZoneScan, "zone_At"), await readings.ExplainAsync(range, Ct));
        Assert.Equal(502, (await ToListAsync(readings.QueryAsync(range, Ct))).Count);

        // Una carga masiva lo mantiene, sobre una tabla que ya tenía filas.
        await InTransactionAsync(database, async writer => await readings.BulkInsertAsync(
            writer, Enumerable.Range(6000, 2000).Select(static i => new Reading { Id = i, At = Epoch.AddMinutes(i - 5000), Day = new DateOnly(2026, 6, 1 + (i % 20)) }), Ct));
        Assert.Equal(502 + 501, (await ToListAsync(readings.QueryAsync(range, Ct))).Count);
        Assert.Equal(new TkPlan(TkAccess.ZoneScan, "zone_Day"), await readings.ExplainAsync(Reading.Fields.Day.AtLeast(new DateOnly(2026, 6, 19)), Ct));
        Assert.Equal(200, (await ToListAsync(readings.QueryAsync(Reading.Fields.Day.AtLeast(new DateOnly(2026, 6, 19)), Ct))).Count);
        await AssertNoLeaksAsync(database);

        // Quien no lo declara lo quita: no podría mantenerlo. También a mitad de una transacción en la que
        // el otro tipo ya había escrito, con lo suyo todavía en memoria.
        await InTransactionAsync(database, async writer =>
        {
            await readings.InsertAsync(writer, new Reading { Id = 9001, At = Epoch.AddMinutes(90_000) }, Ct);
            await plain.InsertAsync(writer, new ReadingPlain { Id = 9000, At = Epoch.AddMinutes(1201) }, Ct);
        });
        Assert.Equal(new TkPlan(TkAccess.TableScan, null), await readings.ExplainAsync(range, Ct));
        Assert.Equal(502 + 501 + 1, (await ToListAsync(readings.QueryAsync(range, Ct))).Count);
        await AssertNoLeaksAsync(database);

        // Y quien vuelve a declararlo lo reconstruye con todo lo que hay, incluida la fila de aquella transacción.
        await InTransactionAsync(database, async writer => await readings.DeleteAsync(writer, 0L, Ct));
        var late = Reading.Fields.At.AtLeast(Epoch.AddMinutes(89_000));
        Assert.Equal(new TkPlan(TkAccess.ZoneScan, "zone_At"), await readings.ExplainAsync(late, Ct));
        Assert.Equal(9001, Assert.Single(await ToListAsync(readings.QueryAsync(late, Ct))).Id);
    }

    [Fact]
    public async Task The_planner_takes_the_zone_map_for_wide_ranges_and_the_index_for_narrow_ones()
    {
        await using var database = await OpenAsync(NewDevice());
        var events = database.Table<TimedEvent>();
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var all = Enumerable.Range(0, 2000).Select(i => new TimedEvent { Id = Guid.CreateVersion7(Epoch.AddSeconds(i * 30)), At = start.AddSeconds(i * 30), Name = $"e{i}" }).ToList();
        await InTransactionAsync(database, async writer =>
        {
            foreach (var row in all)
            {
                await events.InsertAsync(writer, row, Ct);
            }
        });

        // Dentro de una ventana de diez minutos, el índice va derecho a sus filas.
        var narrow = TimedEvent.Fields.At.Between(start.AddMinutes(61), start.AddMinutes(63));
        Assert.Equal(new TkPlan(TkAccess.IndexScan, "At"), await events.ExplainAsync(narrow, Ct));
        Assert.Equal(5, (await ToListAsync(events.QueryAsync(narrow, Ct))).Count);

        // De varias ventanas en adelante, el zone map lee el tramo de la tabla de corrido.
        var wide = TimedEvent.Fields.At.Between(start.AddMinutes(61), start.AddMinutes(200));
        Assert.Equal(new TkPlan(TkAccess.ZoneScan, "zone_At"), await events.ExplainAsync(wide, Ct));
        Assert.Equal(all.Where(row => row.At >= start.AddMinutes(61) && row.At <= start.AddMinutes(200)), await ToListAsync(events.QueryAsync(wide, Ct)));
        Assert.Equal(new TkPlan(TkAccess.ZoneScan, "zone_At"), await events.ExplainAsync(TimedEvent.Fields.At.LessThan(start.AddMinutes(5)), Ct));
        Assert.Equal(10, (await ToListAsync(events.QueryAsync(TimedEvent.Fields.At.LessThan(start.AddMinutes(5)), Ct))).Count);

        // Una fecha exacta es del índice; un rango de claves, de la clave.
        Assert.Equal(new TkPlan(TkAccess.IndexScan, "At"), await events.ExplainAsync(TimedEvent.Fields.At.EqualTo(start.AddMinutes(61)), Ct));
        Assert.Equal(
            new TkPlan(TkAccess.KeyRange, null),
            await events.ExplainAsync(wide.And(TimedEvent.Fields.Id.AtLeast(all[100].Id)), Ct));
    }

    /// <summary>
    /// El "append optimizado" del índice temporal: insertando en orden de fecha, las hojas del índice se
    /// parten 100/0 y quedan llenas, no a la mitad.
    /// </summary>
    [Fact]
    public async Task An_index_on_a_date_that_only_grows_keeps_its_leaves_full()
    {
        await using var database = await OpenAsync(NewDevice());
        var events = database.Table<TimedEvent>();
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var batch = 0; batch < 20; batch++)
        {
            await InTransactionAsync(database, async writer =>
            {
                for (var i = batch * 1000; i < (batch + 1) * 1000; i++)
                {
                    await events.InsertAsync(writer, new TimedEvent { Id = Guid.CreateVersion7(Epoch.AddSeconds(i)), At = start.AddSeconds(i), Name = "e" }, Ct);
                }
            });
        }

        // Cada entrada: 8 bytes de fecha y 16 de clave, 2 de valor, 4 de cabecera y 2 de casilla.
        using var read = database.Pager.BeginRead();
        var key = CatalogStore.IndexKey(CatalogStore.TableKey("events_by_time"), "At");
        var root = default(Tekium.Embedded.Storage.PageNumber);
        await foreach (var entry in BTree.RangeAsync(database.Pager, read.Root, key, default, Ct))
        {
            root = CatalogStore.IndexRoot(entry.Value.Span);
            break;
        }

        var pages = await BTreeVerifier.VerifyAsync(database.Pager, root, Ct);
        var full = 20_000 * 32 / (4096.0 - 32 - 12);
        Assert.True(pages * 0.9 <= full, $"The index takes {pages} pages; full leaves would take {full:F0}.");
    }

    private static Reading NewReading(Random random, long id, int minute) => new()
    {
        Id = id,
        At = Epoch.AddMinutes(minute).AddSeconds(random.Next(60)).ToOffset(TimeSpan.FromHours(random.Next(-3, 4))),
        Day = random.Next(5) == 0 ? null : DateOnly.FromDateTime(Epoch.UtcDateTime).AddDays((minute / 300) - 3),
        Value = random.NextDouble(),
    };

    private static async Task InTransactionAsync(TekiumDb database, Func<TkWriteTransaction, ValueTask> work)
    {
        await using var writer = await database.BeginWriteAsync(Ct);
        await work(writer);
        await writer.CommitAsync(Ct);
    }

    private static async Task AssertRangesAsync(Table<Reading> readings, TkTransaction? transaction, IEnumerable<Reading> model)
    {
        var rows = model.ToList();
        var a = Epoch.AddMinutes(400);
        var b = Epoch.AddMinutes(900);
        var day = DateOnly.FromDateTime(Epoch.UtcDateTime);
        var queries = new (TkFilter<Reading> Filter, Func<Reading, bool> Expected, string? Zone)[]
        {
            (Reading.Fields.At.Between(a, b), row => row.At >= a && row.At <= b, "zone_At"),
            (Reading.Fields.At.GreaterThan(a).And(Reading.Fields.At.LessThan(b)), row => row.At > a && row.At < b, "zone_At"),
            (Reading.Fields.At.Between(a, a.AddMinutes(1)), row => row.At >= a && row.At <= a.AddMinutes(1), "zone_At"),
            (Reading.Fields.At.EqualTo(a), row => row.At == a, "zone_At"),
            (Reading.Fields.At.AtLeast(b), row => row.At >= b, "zone_At"),
            (Reading.Fields.At.LessThan(a), row => row.At < a, "zone_At"),
            (Reading.Fields.At.LessThan(Epoch.AddYears(-50)), static _ => false, "zone_At"),
            (Reading.Fields.At.Between(a, b).And(Reading.Fields.Value.LessThan(0.5)), row => row.At >= a && row.At <= b && row.Value < 0.5, "zone_At"),
            (Reading.Fields.Day.Between(day, day.AddDays(1)), row => row.Day >= day && row.Day <= day.AddDays(1), "zone_Day"),
            (Reading.Fields.Day.LessThan(day), row => row.Day < day, "zone_Day"),
            (Reading.Fields.Day.EqualTo(day.AddDays(2)), row => row.Day == day.AddDays(2), "zone_Day"),
            (Reading.Fields.Day.IsNull(), static row => row.Day is null, null),
            (Reading.Fields.Day.IsNotNull().And(Reading.Fields.Value.GreaterThan(0.9)), static row => row.Day is not null && row.Value > 0.9, null),
        };

        foreach (var (filter, expected, zone) in queries)
        {
            var found = transaction is null
                ? await ToListAsync(readings.QueryAsync(filter, Ct))
                : await ToListAsync(readings.QueryAsync(transaction, filter, Ct));
            Assert.Equal(rows.Where(expected), found);
            if (transaction is null && rows.Count > 0)
            {
                Assert.Equal(zone is null ? new TkPlan(TkAccess.TableScan, null) : new TkPlan(TkAccess.ZoneScan, zone), await readings.ExplainAsync(filter, Ct));
            }
        }
    }
}

public sealed class ZoneMapTests_File : ZoneMapTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class ZoneMapTests_Memory : ZoneMapTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
