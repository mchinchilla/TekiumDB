// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Tests.Storage;

public sealed class FileStorageDeviceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tekium-{Guid.NewGuid():N}.tkdb");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => File.Delete(_path);

    [Fact]
    public async Task New_file_is_empty()
    {
        await using var device = FileStorageDevice.Open(_path, FileMode.CreateNew);

        Assert.Equal(0, device.Length);
    }

    [Fact]
    public async Task Write_then_read_round_trips_at_an_offset()
    {
        await using var device = FileStorageDevice.Open(_path, FileMode.CreateNew);
        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8];

        await device.WriteAsync(4096, payload, Ct);
        var read = new byte[payload.Length];
        await device.ReadAsync(4096, read, Ct);

        Assert.Equal(payload, read);
        Assert.Equal(4096 + payload.Length, device.Length);
    }

    [Fact]
    public async Task Reading_past_the_end_fails_instead_of_returning_short_data()
    {
        await using var device = FileStorageDevice.Open(_path, FileMode.CreateNew);
        await device.WriteAsync(0, new byte[10], Ct);

        await Assert.ThrowsAsync<EndOfStreamException>(async () => await device.ReadAsync(5, new byte[10], Ct));
    }

    [Fact]
    public async Task SetLength_preallocates_zeroed_space()
    {
        await using var device = FileStorageDevice.Open(_path, FileMode.CreateNew);

        device.SetLength(8192);
        var read = new byte[16];
        await device.ReadAsync(8192 - 16, read, Ct);

        Assert.Equal(8192, device.Length);
        Assert.All(read, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Flushed_data_survives_reopening_the_file()
    {
        byte[] payload = [42, 43, 44];
        await using (var device = FileStorageDevice.Open(_path, FileMode.CreateNew))
        {
            await device.WriteAsync(100, payload, Ct);
            await device.FlushAsync(Ct);
        }

        await using var reopened = FileStorageDevice.Open(_path, FileMode.Open);
        var read = new byte[payload.Length];
        await reopened.ReadAsync(100, read, Ct);

        Assert.Equal(103, reopened.Length);
        Assert.Equal(payload, read);
    }
}
