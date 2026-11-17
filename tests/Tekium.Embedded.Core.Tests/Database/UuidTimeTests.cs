// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Tests.Storage;

namespace Tekium.Embedded.Tests.Database;

/// <summary>
/// Un UUID v7 empieza por el milisegundo en que se creó: "todo lo creado entre X e Y" es un rango de la
/// clave, sin un índice de fecha aparte.
/// </summary>
public abstract class UuidTimeTests : DatabaseTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Rows_created_in_a_span_of_time_are_a_range_of_their_uuid_v7_key()
    {
        await using var database = await OpenAsync(NewDevice());
        var parcels = database.Table<Parcel>();
        var all = await FillAsync(database, 3000);
        var from = Epoch.AddMilliseconds(700);
        var to = Epoch.AddMilliseconds(1900);

        var between = Parcel.Fields.Id.CreatedBetween(from, to);
        Assert.Equal(new TkPlan(TkAccess.KeyRange, null), await parcels.ExplainAsync(between, Ct));
        Assert.Equal(Ids(all, static s => s.Id, 700, 1900), await IdsAsync(parcels, between));

        var since = Parcel.Fields.Id.CreatedSince(from);
        Assert.Equal(new TkPlan(TkAccess.KeyRange, null), await parcels.ExplainAsync(since, Ct));
        Assert.Equal(Ids(all, static s => s.Id, 700, int.MaxValue), await IdsAsync(parcels, since));

        var before = Parcel.Fields.Id.CreatedBefore(from);
        Assert.Equal(new TkPlan(TkAccess.KeyRange, null), await parcels.ExplainAsync(before, Ct));
        Assert.Equal(Ids(all, static s => s.Id, 0, 699), await IdsAsync(parcels, before));

        // Las dos mitades juntas son el mismo rango.
        Assert.Equal(
            Ids(all, static s => s.Id, 700, 1899),
            await IdsAsync(parcels, since.And(Parcel.Fields.Id.CreatedBefore(to))));
    }

    [Fact]
    public async Task The_span_is_cut_to_the_millisecond_and_includes_both_ends()
    {
        await using var database = await OpenAsync(NewDevice());
        var parcels = database.Table<Parcel>();
        var all = await FillAsync(database, 50);

        // Una fracción de milisegundo no excluye a nada creado en ese milisegundo.
        var filter = Parcel.Fields.Id.CreatedBetween(Epoch.AddMilliseconds(10.9), Epoch.AddMilliseconds(12.1));
        Assert.Equal(Ids(all, static s => s.Id, 10, 12), await IdsAsync(parcels, filter));
        Assert.Empty(await IdsAsync(parcels, Parcel.Fields.Id.CreatedBetween(Epoch.AddMilliseconds(12), Epoch.AddMilliseconds(10))));
    }

    [Fact]
    public async Task It_works_on_any_uuid_column_through_its_index_or_by_reading_the_table()
    {
        await using var database = await OpenAsync(NewDevice());
        var parcels = database.Table<Parcel>();
        var all = await FillAsync(database, 600);
        var from = Epoch.AddMilliseconds(100);
        var to = Epoch.AddMilliseconds(250);

        var byOrder = Parcel.Fields.Order.CreatedBetween(from, to);
        Assert.Equal(new TkPlan(TkAccess.IndexScan, "Order"), await parcels.ExplainAsync(byOrder, Ct));
        Assert.Equal(Ids(all, static s => s.Order, 100, 250), await IdsAsync(parcels, byOrder));

        // Anulable: las filas sin valor no entran, ni en "antes de".
        var byBatch = Parcel.Fields.Batch.CreatedBefore(to);
        Assert.Equal(new TkPlan(TkAccess.TableScan, null), await parcels.ExplainAsync(byBatch, Ct));
        Assert.Equal(
            all.Where(s => s.Batch is { } batch && Millisecond(batch) < 250).Select(static s => s.Id).Order(),
            await IdsAsync(parcels, byBatch));
        Assert.Equal(
            all.Where(s => s.Batch is { } batch && Millisecond(batch) >= 100).Select(static s => s.Id).Order(),
            await IdsAsync(parcels, Parcel.Fields.Batch.CreatedSince(from)));
    }

    [Fact]
    public async Task Times_a_uuid_v7_cannot_hold_are_the_ends_of_the_range()
    {
        await using var database = await OpenAsync(NewDevice());
        var parcels = database.Table<Parcel>();
        var all = await FillAsync(database, 40);
        var everything = all.Select(static s => s.Id).Order().ToList();

        Assert.Equal(everything, await IdsAsync(parcels, Parcel.Fields.Id.CreatedSince(DateTimeOffset.MinValue)));
        Assert.Equal(everything, await IdsAsync(parcels, Parcel.Fields.Id.CreatedBetween(DateTimeOffset.MinValue, DateTimeOffset.MaxValue)));
        Assert.Empty(await IdsAsync(parcels, Parcel.Fields.Id.CreatedBefore(DateTimeOffset.MinValue)));
        Assert.Empty(await IdsAsync(parcels, Parcel.Fields.Id.CreatedBefore(DateTimeOffset.UnixEpoch)));
        Assert.Equal(everything, await IdsAsync(parcels, Parcel.Fields.Id.CreatedBefore(DateTimeOffset.MaxValue)));
    }

    /// <summary>El primer UUID posible de un milisegundo (la marca de tiempo y ceros) ya es de ese milisegundo.</summary>
    [Fact]
    public async Task The_lowest_uuid_of_a_millisecond_belongs_to_it()
    {
        await using var database = await OpenAsync(NewDevice());
        var parcels = database.Table<Parcel>();
        var bytes = Guid.CreateVersion7(Epoch.AddMilliseconds(5)).ToByteArray(bigEndian: true);
        bytes.AsSpan(6).Clear();
        var lowest = new Guid(bytes, bigEndian: true);
        await using (var writer = await database.BeginWriteAsync(Ct))
        {
            await parcels.InsertAsync(writer, new Parcel { Id = lowest, Order = lowest }, Ct);
            await writer.CommitAsync(Ct);
        }

        Assert.Empty(await IdsAsync(parcels, Parcel.Fields.Id.CreatedBefore(Epoch.AddMilliseconds(5))));
        Assert.Empty(await IdsAsync(parcels, Parcel.Fields.Id.CreatedBetween(Epoch, Epoch.AddMilliseconds(4))));
        Assert.Equal([lowest], await IdsAsync(parcels, Parcel.Fields.Id.CreatedBetween(Epoch.AddMilliseconds(5), Epoch.AddMilliseconds(5))));
        Assert.Equal([lowest], await IdsAsync(parcels, Parcel.Fields.Id.CreatedSince(Epoch.AddMilliseconds(5))));
    }

    // Una fila por milisegundo desde Epoch; el pedido medio segundo antes, y el lote (una de cada tres no tiene) después.
    private static async Task<List<Parcel>> FillAsync(TekiumDb database, int count)
    {
        var all = Enumerable.Range(0, count)
            .Select(static i => new Parcel
            {
                Id = Guid.CreateVersion7(Epoch.AddMilliseconds(i)),
                Order = Guid.CreateVersion7(Epoch.AddMilliseconds((i * 7) % 600)),
                Batch = i % 3 == 0 ? null : Guid.CreateVersion7(Epoch.AddMilliseconds(i / 2)),
            })
            .ToList();
        await using var writer = await database.BeginWriteAsync(Ct);
        foreach (var parcel in all)
        {
            await database.Table<Parcel>().InsertAsync(writer, parcel, Ct);
        }

        await writer.CommitAsync(Ct);
        return all;
    }

    private static long Millisecond(Guid id) =>
        (long)(Convert.ToUInt64(Convert.ToHexString(id.ToByteArray(bigEndian: true).AsSpan(0, 6)), 16) - (ulong)Epoch.ToUnixTimeMilliseconds());

    private static IEnumerable<Guid> Ids(List<Parcel> all, Func<Parcel, Guid> field, int from, int to) =>
        all.Where(s => Millisecond(field(s)) >= from && Millisecond(field(s)) <= to).Select(static s => s.Id).Order();

    private static async Task<List<Guid>> IdsAsync(Table<Parcel> parcels, TkFilter<Parcel> filter) =>
        [.. (await ToListAsync(parcels.QueryAsync(filter, Ct))).Select(static s => s.Id).Order()];
}

public sealed class UuidTimeTests_File : UuidTimeTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class UuidTimeTests_Memory : UuidTimeTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
