// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Tests.Storage;

/// <summary>
/// El contrato de <see cref="IStorageDevice"/>. Cada implementación lo hereda: el pager no distingue
/// un archivo de la memoria, así que ambas deben comportarse igual.
/// </summary>
public abstract class StorageDeviceContractTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_device_is_empty()
    {
        await using var device = Create();

        Assert.Equal(0, device.Length);
    }

    [Fact]
    public async Task Write_then_read_round_trips_at_an_offset()
    {
        await using var device = Create();
        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8];

        await device.WriteAsync(4096, payload, Ct);
        var read = new byte[payload.Length];
        await device.ReadAsync(4096, read, Ct);

        Assert.Equal(payload, read);
        Assert.Equal(4096 + payload.Length, device.Length);
    }

    [Fact]
    public async Task A_gap_left_by_writing_past_the_end_reads_as_zeros()
    {
        await using var device = Create();
        await device.WriteAsync(300_000, new byte[] { 9 }, Ct);

        var read = new byte[300_001];
        await device.ReadAsync(0, read, Ct);

        Assert.Equal(9, read[^1]);
        Assert.Equal(0, read.AsSpan(0, 300_000).IndexOfAnyExcept((byte)0) + 1);
    }

    [Fact]
    public async Task A_large_write_at_an_odd_offset_round_trips()
    {
        // 500 KB desde un desplazamiento no alineado: cruza cualquier límite interno de bloque.
        await using var device = Create();
        var payload = new byte[500_000];
        new Random(5).NextBytes(payload);

        await device.WriteAsync(12_345, payload, Ct);
        var read = new byte[payload.Length];
        await device.ReadAsync(12_345, read, Ct);
        var middle = new byte[70_000];
        await device.ReadAsync(12_345 + 100_001, middle, Ct);

        Assert.True(payload.AsSpan().SequenceEqual(read));
        Assert.True(payload.AsSpan(100_001, 70_000).SequenceEqual(middle));
    }

    [Fact]
    public async Task Overwriting_changes_only_the_bytes_written()
    {
        await using var device = Create();
        await device.WriteAsync(0, Enumerable.Repeat((byte)1, 200_000).ToArray(), Ct);

        await device.WriteAsync(65_530, new byte[] { 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7 }, Ct);
        var read = new byte[200_000];
        await device.ReadAsync(0, read, Ct);

        Assert.Equal(200_000, device.Length);
        Assert.Equal(12, read.Count(b => b == 7));
        Assert.Equal(200_000 - 12, read.Count(b => b == 1));
        Assert.Equal(7, read[65_530]);
        Assert.Equal(7, read[65_541]);
    }

    [Fact]
    public async Task Reading_past_the_end_fails_instead_of_returning_short_data()
    {
        await using var device = Create();
        await device.WriteAsync(0, new byte[10], Ct);

        await Assert.ThrowsAsync<EndOfStreamException>(async () => await device.ReadAsync(5, new byte[10], Ct));
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await device.ReadAsync(10, new byte[1], Ct));
    }

    [Fact]
    public async Task SetLength_grows_with_zeros()
    {
        await using var device = Create();

        device.SetLength(1 << 20);
        var read = new byte[16];
        await device.ReadAsync((1 << 20) - 16, read, Ct);

        Assert.Equal(1 << 20, device.Length);
        Assert.All(read, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Shrinking_discards_the_data_beyond_the_new_length()
    {
        await using var device = Create();
        await device.WriteAsync(0, Enumerable.Repeat((byte)0xFF, 200_000).ToArray(), Ct);

        device.SetLength(1000);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await device.ReadAsync(990, new byte[20], Ct));

        device.SetLength(200_000);
        var read = new byte[200_000];
        await device.ReadAsync(0, read, Ct);

        Assert.Equal(1000, read.Count(b => b == 0xFF));
        Assert.Equal(1000, read.AsSpan().IndexOf((byte)0));
        Assert.Equal(200_000 - 1000, read.Count(b => b == 0));
    }

    [Fact]
    public async Task Flush_completes_and_keeps_the_data()
    {
        await using var device = Create();
        await device.WriteAsync(100, new byte[] { 42, 43, 44 }, Ct);

        await device.FlushAsync(Ct);
        var read = new byte[3];
        await device.ReadAsync(100, read, Ct);

        Assert.Equal([42, 43, 44], read);
    }

    [Fact]
    public async Task A_cancelled_operation_does_not_run()
    {
        await using var device = Create();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await device.WriteAsync(0, new byte[8], cancelled.Token));
        Assert.Equal(0, device.Length);
    }

    private protected abstract IStorageDevice Create();
}

public sealed class FileStorageDeviceTests : StorageDeviceContractTests, IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tekium-{Guid.NewGuid():N}.tkdb");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public async Task Flushed_data_survives_reopening_the_file()
    {
        var ct = TestContext.Current.CancellationToken;
        byte[] payload = [42, 43, 44];
        await using (var device = FileStorageDevice.Open(_path, FileMode.CreateNew))
        {
            await device.WriteAsync(100, payload, ct);
            await device.FlushAsync(ct);
        }

        await using var reopened = FileStorageDevice.Open(_path, FileMode.Open);
        var read = new byte[payload.Length];
        await reopened.ReadAsync(100, read, ct);

        Assert.Equal(103, reopened.Length);
        Assert.Equal(payload, read);
    }

    private protected override IStorageDevice Create() => FileStorageDevice.Open(_path, FileMode.CreateNew);
}

public sealed class MemoryStorageDeviceTests : StorageDeviceContractTests
{
    [Fact]
    public async Task Preallocating_space_does_not_commit_memory_for_it()
    {
        // El pager pide espacio en bloques grandes; en memoria sólo debe costar lo que se escribe.
        await using var device = new MemoryStorageDevice();
        var before = GC.GetAllocatedBytesForCurrentThread();

        device.SetLength(1L << 30);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(1L << 30, device.Length);
        Assert.InRange(allocated, 0, 1 << 20);
    }

    [Fact]
    public async Task Reads_and_writes_complete_synchronously_without_allocating()
    {
        await using var device = new MemoryStorageDevice();
        var page = new byte[8192];
        var ct = TestContext.Current.CancellationToken;
        await device.WriteAsync(0, page, ct);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var synchronous = true;

        for (var i = 0; i < 1000; i++)
        {
            synchronous &= device.WriteAsync(0, page, ct).IsCompletedSuccessfully;
            synchronous &= device.ReadAsync(0, page, ct).IsCompletedSuccessfully;
            synchronous &= device.FlushAsync(ct).IsCompletedSuccessfully;
        }

        Assert.True(synchronous);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public async Task Disposing_releases_the_memory()
    {
        var device = new MemoryStorageDevice();
        await device.WriteAsync(0, new byte[100_000], TestContext.Current.CancellationToken);

        await device.DisposeAsync();

        Assert.Equal(0, device.Length);
    }

    private protected override IStorageDevice Create() => new MemoryStorageDevice();
}
