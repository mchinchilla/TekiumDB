// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Security;
using Tekium.Embedded.Storage;
using Tekium.Embedded.Tests.Indexing;

namespace Tekium.Embedded.Tests.Storage;

/// <summary>
/// Compresión Zstandard por página, activable por archivo. El archivo recuerda si la usa: quien lo abre
/// no tiene que saberlo, y un archivo con compresión y otro sin ella pasan por el mismo código.
/// </summary>
public abstract class CompressionTests : DeviceBackedTests
{
    private const int PageBytes = 4096;
    private const string Password = "compressed and encrypted";

    private static readonly PagerOptions Plain = new()
    {
        PageSize = PageSize.FromBytes(PageBytes),
        CachePages = 48,
        KeyDerivation = new Argon2Parameters(MemoryKiB: 64, Iterations: 1, Parallelism: 1),
    };

    private static readonly PagerOptions Compressed = Plain with { Compression = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_compressed_database_reads_back_what_was_written_and_opens_without_being_told(bool encrypted)
    {
        var device = NewDevice();
        var password = encrypted ? Password : null;
        await using (var pager = await Pager.CreateAsync(device, Compressed, password, Ct))
        {
            Assert.True(pager.IsCompressed);
            await FillAsync(pager, rows: 4000, compressible: true);
            await AssertRowsAsync(pager, rows: 4000, compressible: true);
            await BTreeVerifier.AssertNoLeaksAsync(pager, Ct);
        }

        // Quien abre no pide compresión: el archivo dice que la tiene. Y lo que se escriba después, también va comprimido.
        await using (var reopened = await Pager.OpenAsync(device, Plain, password, Ct))
        {
            Assert.True(reopened.IsCompressed);
            Assert.Equal(encrypted, reopened.IsEncrypted);
            await AssertRowsAsync(reopened, rows: 4000, compressible: true);
            await FillAsync(reopened, rows: 4500, compressible: true);
        }

        await using var again = await Pager.OpenAsync(device, Plain, password, Ct);
        await AssertRowsAsync(again, rows: 4500, compressible: true);
        await BTreeVerifier.AssertNoLeaksAsync(again, Ct);
    }

    [Fact]
    public async Task A_database_created_without_compression_stays_that_way_whatever_the_options_say_later()
    {
        var device = NewDevice();
        await using (var pager = await Pager.CreateAsync(device, Plain, Ct))
        {
            Assert.False(pager.IsCompressed);
            await FillAsync(pager, rows: 1000, compressible: true);
        }

        await using var reopened = await Pager.OpenAsync(device, Compressed, Ct);
        Assert.False(reopened.IsCompressed);
        await AssertRowsAsync(reopened, rows: 1000, compressible: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compressible_pages_take_far_fewer_bytes_on_their_way_to_the_storage(bool encrypted)
    {
        var password = encrypted ? Password : null;
        var plain = await WrittenBytesAsync(Plain, password, compressible: true);
        var compressed = await WrittenBytesAsync(Compressed, password, compressible: true);

        Assert.True(compressed * 3 < plain, $"Compression wrote {compressed} bytes where the plain file wrote {plain}.");
    }

    [Fact]
    public async Task Pages_that_do_not_compress_are_stored_as_they_are_and_cost_no_extra_bytes()
    {
        var plain = await WrittenBytesAsync(Plain, null, compressible: false);
        var compressed = await WrittenBytesAsync(Compressed, null, compressible: false);

        // Datos al azar: comprimir no gana nada, y no puede costar más que no hacerlo.
        Assert.True(compressed <= plain, $"Compression wrote {compressed} bytes where the plain file wrote {plain}.");
        Assert.True(compressed * 10 > plain * 9, "Random data should not have compressed.");
    }

    [Fact]
    public async Task Free_list_pages_are_never_compressed()
    {
        var device = NewDevice();
        var types = new Dictionary<PageType, (int Compressed, int Raw)>();
        device.OnWrite = (offset, bytes) =>
        {
            // Con compresión cada página se escribe por separado, empezando por su cabecera.
            if (offset >= PageBytes * 2 && offset % PageBytes == 0)
            {
                var type = (PageType)bytes.Span[0];
                var compressed = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Span[2..]) != 0;
                types.TryGetValue(type, out var count);
                types[type] = compressed ? (count.Compressed + 1, count.Raw) : (count.Compressed, count.Raw + 1);
            }
        };

        await using var pager = await Pager.CreateAsync(device, Compressed, Ct);
        for (var round = 0; round < 6; round++)
        {
            await FillAsync(pager, rows: 3000, compressible: true, fill: (byte)round);
        }

        Assert.True(pager.FreeListPageCount > 0, "The workload never produced a free-list page.");
        Assert.True(types[PageType.BTreeLeaf].Compressed > 0);
        Assert.Equal(0, types.GetValueOrDefault(PageType.FreeListTrunk).Compressed + types.GetValueOrDefault(PageType.FreeListDirectory).Compressed);
        Assert.True(types.GetValueOrDefault(PageType.FreeListTrunk).Raw + types.GetValueOrDefault(PageType.FreeListDirectory).Raw > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Damage_to_a_compressed_page_is_reported_by_page_number_and_never_read(bool encrypted)
    {
        var device = NewDevice();
        var password = encrypted ? Password : null;
        PageNumber root;
        await using (var pager = await Pager.CreateAsync(device, Compressed, password, Ct))
        {
            await FillAsync(pager, rows: 50, compressible: true);
            root = pager.Current.Root;
        }

        // El cuerpo comprimido, la longitud que la cabecera declara, y el final de lo guardado.
        var page = root.Value * (long)PageBytes;
        var stored = await device.ReadAllAsync();
        var length = BinaryPrimitives.ReadUInt16LittleEndian(stored.AsSpan((int)page + 2));
        Assert.True(length > 0 && length < PageBytes / 2, $"The root page was expected to be compressed; its header says {length}.");
        foreach (var offset in new[] { page + 40, page + 2, page + 3, page + 16 + length + 3 })
        {
            await device.CorruptAsync(offset, 0x10);
            await using (var damaged = await Pager.OpenAsync(device, Plain, password, Ct))
            {
                var error = await Assert.ThrowsAsync<InvalidDataException>(async () => await damaged.PinAsync(root, Ct));
                Assert.Contains($"Page {root.Value}", error.Message, StringComparison.Ordinal);
            }

            await device.CorruptAsync(offset, 0x10);
        }

        await using var intact = await Pager.OpenAsync(device, Plain, password, Ct);
        await AssertRowsAsync(intact, rows: 50, compressible: true);
    }

    [Fact]
    public async Task An_encrypted_and_compressed_file_shows_no_plaintext()
    {
        var device = NewDevice();
        await using (var pager = await Pager.CreateAsync(device, Compressed, Password, Ct))
        {
            await FillAsync(pager, rows: 500, compressible: true);
        }

        var stored = await device.ReadAllAsync();
        Assert.True(stored.AsSpan().IndexOf("compressible text "u8) < 0, "Row text is readable in the file.");
    }

    [Fact]
    public async Task A_file_with_flags_this_build_does_not_know_is_refused()
    {
        var device = NewDevice();
        await using (var pager = await Pager.CreateAsync(device, Plain, Ct))
        {
            await FillAsync(pager, rows: 10, compressible: true);
        }

        // Un bit de los flags de la cabecera que nadie ha definido, en las dos copias.
        await device.CorruptAsync(12 + 1, 0x01);
        await device.CorruptAsync(PageBytes + 12 + 1, 0x01);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Pager.OpenAsync(device, Plain, Ct));
    }

    private static byte[] Key(int row)
    {
        var key = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(key, row);
        return key;
    }

    private static byte[] Value(int row, bool compressible, byte fill)
    {
        var value = new byte[150 + (row % 60)];
        if (compressible)
        {
            var text = System.Text.Encoding.UTF8.GetBytes($"compressible text {fill} for row {row % 50}; ");
            for (var i = 0; i < value.Length; i++)
            {
                value[i] = text[i % text.Length];
            }
        }
        else
        {
            new Random((row * 31) + fill).NextBytes(value);
        }

        return value;
    }

    private static async Task FillAsync(Pager pager, int rows, bool compressible, byte fill = 0)
    {
        var tree = new BTree(pager);
        await using var writer = await pager.BeginWriteAsync(Ct);
        for (var row = 0; row < rows; row++)
        {
            await tree.PutAsync(writer, Key(row), Value(row, compressible, fill), Ct);
        }

        await writer.CommitAsync(Ct);
    }

    private static async Task AssertRowsAsync(Pager pager, int rows, bool compressible)
    {
        var tree = new BTree(pager);
        var count = 0;
        await foreach (var entry in tree.RangeAsync(default, default, Ct))
        {
            Assert.True(entry.Value.Span.SequenceEqual(Value(count, compressible, 0)), $"Row {count} came back different.");
            count++;
        }

        Assert.Equal(rows, count);
    }

    /// <summary>Cuántos bytes de páginas llegan al dispositivo al escribir la misma carga.</summary>
    private async Task<long> WrittenBytesAsync(PagerOptions options, string? password, bool compressible)
    {
        var device = NewDevice();
        long bytes = 0;
        device.OnWrite = (offset, written) => bytes += offset >= PageBytes * 2 ? written.Length : 0;
        await using var pager = await Pager.CreateAsync(device, options, password, Ct);
        await FillAsync(pager, rows: 5000, compressible);
        await AssertRowsAsync(pager, rows: 5000, compressible);
        return bytes;
    }
}

public sealed class CompressionTests_File : CompressionTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class CompressionTests_Memory : CompressionTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
