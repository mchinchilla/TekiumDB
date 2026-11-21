// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using System.Security.Cryptography;
using TekiumDB.Indexing;
using TekiumDB.Security;
using TekiumDB.Storage;
using TekiumDB.Tests.Storage;

namespace TekiumDB.Tests.Security;

public abstract class EncryptedDatabaseTests : DeviceBackedTests
{
    private const string Password = "correct horse battery staple";
    private const int PageBytes = 4096;

    // Argon2id al mínimo: aquí se prueba el cifrado, no el costo de derivar la clave.
    private static readonly PagerOptions Options = new()
    {
        PageSize = PageSize.FromBytes(PageBytes),
        CachePages = 64,
        KeyDerivation = new Argon2Parameters(MemoryKiB: 64, Iterations: 1, Parallelism: 1),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Data_written_with_a_password_is_read_back_with_it_after_reopening()
    {
        var device = NewDevice();
        await using (var pager = await Pager.CreateAsync(device, Options, Password, Ct))
        {
            await FillAsync(pager, rows: 3000);
        }

        await using var reopened = await Pager.OpenAsync(device, Options, Password, Ct);

        await AssertRowsAsync(reopened, rows: 3000);
    }

    [Fact]
    public async Task The_wrong_password_fails_cleanly_and_leaves_the_file_untouched()
    {
        var device = NewDevice();
        await using (var pager = await Pager.CreateAsync(device, Options, Password, Ct))
        {
            await FillAsync(pager, rows: 500);
        }

        var before = await device.ReadAllAsync();
        var writes = device.WriteCount;

        await Assert.ThrowsAsync<CryptographicException>(
            async () => await Pager.OpenAsync(device, Options, "correct horse battery stapl", Ct));
        await Assert.ThrowsAsync<CryptographicException>(
            async () => await Pager.OpenAsync(device, Options, Password + " ", Ct));

        Assert.Equal(writes, device.WriteCount);
        Assert.Equal(before, await device.ReadAllAsync());
        await using var reopened = await Pager.OpenAsync(device, Options, Password, Ct);
        await AssertRowsAsync(reopened, rows: 500);
    }

    [Fact]
    public async Task An_encrypted_database_cannot_be_opened_without_a_password()
    {
        var device = NewDevice();
        await using (await Pager.CreateAsync(device, Options, Password, Ct))
        {
        }

        var error = await Assert.ThrowsAsync<CryptographicException>(async () => await Pager.OpenAsync(device, Options, Ct));
        Assert.Contains("password", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_password_given_for_an_unencrypted_database_is_an_error()
    {
        var device = NewDevice();
        await using (await Pager.CreateAsync(device, Options, Ct))
        {
        }

        await Assert.ThrowsAsync<CryptographicException>(async () => await Pager.OpenAsync(device, Options, Password, Ct));
    }

    [Fact]
    public async Task Nothing_written_by_the_application_appears_in_the_file()
    {
        var plain = NewDevice();
        var encrypted = NewDevice();
        await using (var pager = await Pager.CreateAsync(plain, Options, Ct))
        {
            await FillAsync(pager, rows: 2000);
        }

        await using (var pager = await Pager.CreateAsync(encrypted, Options, Password, Ct))
        {
            await FillAsync(pager, rows: 2000);
        }

        // Control: sin cifrado el valor está en el archivo tal cual.
        Assert.True((await plain.ReadAllAsync()).AsSpan().IndexOf("TEKIUM-SECRET-VALUE"u8) >= 0);
        Assert.Equal(-1, (await encrypted.ReadAllAsync()).AsSpan().IndexOf("TEKIUM-SECRET-VALUE"u8));
        Assert.Equal(-1, (await encrypted.ReadAllAsync()).AsSpan().IndexOf("TEKIUM-SECRET"u8));
    }

    [Fact]
    public async Task Two_databases_with_the_same_password_and_data_share_no_ciphertext()
    {
        var first = NewDevice();
        var second = NewDevice();
        foreach (var device in new[] { first, second })
        {
            await using var pager = await Pager.CreateAsync(device, Options, Password, Ct);
            await FillAsync(pager, rows: 200);
        }

        var a = await first.ReadAllAsync();
        var b = await second.ReadAllAsync();

        // Cada archivo tiene su propia clave aleatoria: el primer bloque cifrado de cada página no debe
        // coincidir en ninguna. Se saltan las páginas reservadas por adelantado, que aún están en ceros.
        var compared = 0;
        var shared = 0;
        for (var i = (2 * PageBytes) + PageLayout.HeaderSize; i + 16 <= Math.Min(a.Length, b.Length); i += PageBytes)
        {
            if (a.AsSpan(i, 16).IndexOfAnyExcept((byte)0) < 0)
            {
                continue;
            }

            compared++;
            shared += a.AsSpan(i, 16).SequenceEqual(b.AsSpan(i, 16)) ? 1 : 0;
        }

        Assert.InRange(compared, 3, int.MaxValue);
        Assert.Equal(0, shared);
    }

    [Fact]
    public async Task A_page_altered_on_disk_is_reported_by_number_instead_of_decrypted()
    {
        var device = NewDevice();
        PageNumber root;
        await using (var pager = await Pager.CreateAsync(device, Options, Password, Ct))
        {
            await FillAsync(pager, rows: 50);
            root = pager.Current.Root;
        }

        await device.CorruptAsync((root.Value * (long)PageBytes) + 500, mask: 0x01);
        await using var reopened = await Pager.OpenAsync(device, Options, Password, Ct);

        var error = await Assert.ThrowsAsync<InvalidDataException>(async () => await reopened.PinAsync(root, Ct));
        Assert.Contains(root.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_root_slot_forged_without_the_key_is_rejected()
    {
        // Sin cifrado el slot se valida con un hash público; cifrado, con una clave. Quien no la tiene
        // no puede apuntar la raíz a otra página, aunque recalcule el hash.
        var device = NewDevice();
        Snapshot previous;
        await using (var pager = await Pager.CreateAsync(device, Options, Password, Ct))
        {
            await FillAsync(pager, rows: 50);
            previous = pager.Current;
            await FillAsync(pager, rows: 60);

            var forged = new byte[FileHeader.SlotSize];
            FileHeader.EncodeSlot(
                forged,
                pager.Current with { Root = new PageNumber(3), TransactionId = new TransactionId(pager.Current.TransactionId.Value + 1) },
                ChecksumPageProtector.Instance);
            await device.OverwriteAsync(FileHeader.SlotOffset(pager.CurrentSlot, Options.PageSize), forged);
        }

        await using var reopened = await Pager.OpenAsync(device, Options, Password, Ct);

        Assert.Equal(previous, reopened.Current);
    }

    [Fact]
    public async Task Tampered_key_derivation_parameters_fail_cleanly()
    {
        var device = NewDevice();
        await using (var pager = await Pager.CreateAsync(device, Options, Password, Ct))
        {
            await FillAsync(pager, rows: 50);
        }

        // Los parámetros están en claro en ambas copias de la cabecera, pero autenticados con la clave.
        foreach (var metaPage in new[] { 0, 1 })
        {
            var iterations = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(iterations, 2);
            await device.OverwriteAsync((metaPage * (long)PageBytes) + FileHeader.KeyDerivationOffset + 4, iterations);
        }

        await Assert.ThrowsAsync<CryptographicException>(async () => await Pager.OpenAsync(device, Options, Password, Ct));
    }

    [Fact]
    public async Task Absurd_key_derivation_parameters_are_refused_before_spending_the_memory()
    {
        var device = NewDevice();
        await using (await Pager.CreateAsync(device, Options, Password, Ct))
        {
        }

        var memory = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(memory, uint.MaxValue);
        await device.OverwriteAsync(FileHeader.KeyDerivationOffset, memory);
        await device.OverwriteAsync(PageBytes + FileHeader.KeyDerivationOffset, memory);
        var before = GC.GetTotalAllocatedBytes();

        await Assert.ThrowsAsync<InvalidDataException>(async () => await Pager.OpenAsync(device, Options, Password, Ct));

        Assert.InRange(GC.GetTotalAllocatedBytes() - before, 0, 64L * 1024 * 1024);
    }

    [Fact]
    public async Task Changing_the_password_rewrites_only_the_header()
    {
        var device = NewDevice();
        await using (var pager = await Pager.CreateAsync(device, Options, Password, Ct))
        {
            await FillAsync(pager, rows: 3000);
            var bodyBefore = (await device.ReadAllAsync()).AsSpan(2 * PageBytes).ToArray();

            await pager.ChangePasswordAsync("a new password", Ct);

            Assert.Equal(bodyBefore, (await device.ReadAllAsync()).AsSpan(2 * PageBytes).ToArray());
            await AssertRowsAsync(pager, rows: 3000);
        }

        await Assert.ThrowsAsync<CryptographicException>(async () => await Pager.OpenAsync(device, Options, Password, Ct));
        await using var reopened = await Pager.OpenAsync(device, Options, "a new password", Ct);
        await AssertRowsAsync(reopened, rows: 3000);
    }

    [Fact]
    public async Task A_password_change_interrupted_halfway_still_opens_with_one_of_the_two_passwords()
    {
        var device = NewDevice();
        await using (var pager = await Pager.CreateAsync(device, Options, Password, Ct))
        {
            await FillAsync(pager, rows: 100);

            // La segunda escritura es la segunda copia de la cabecera: falla, y sólo la primera queda cambiada.
            device.FailOnWriteNumber = device.WriteCount + 2;
            await Assert.ThrowsAsync<IOException>(async () => await pager.ChangePasswordAsync("a new password", Ct));
        }

        await using (var withNew = await Pager.OpenAsync(device, Options, "a new password", Ct))
        {
            await AssertRowsAsync(withNew, rows: 100);
        }

        await using var withOld = await Pager.OpenAsync(device, Options, Password, Ct);
        await AssertRowsAsync(withOld, rows: 100);
    }

    [Fact]
    public async Task Changing_the_password_of_an_unencrypted_database_is_an_error()
    {
        var device = NewDevice();
        await using var pager = await Pager.CreateAsync(device, Options, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pager.ChangePasswordAsync("x", Ct));
    }

    /// <summary>
    /// Criterio de salida de la Fase 2. AES-GCM se rompe por completo si dos páginas distintas se cifran
    /// con la misma clave y el mismo nonce. El nonce es (número de página, id de transacción): esta prueba
    /// observa todo lo que llega al disco y exige que ese par no se repita nunca, incluso con commits que
    /// fallan a medias, rollbacks y reaperturas tras un commit incompleto, que es como se ve un crash.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task No_nonce_is_ever_used_twice(int seed)
    {
        var random = new Random(seed);
        var device = NewDevice();
        var nonces = new HashSet<(uint Page, ulong Transaction)>();
        var pagesWritten = 0;
        device.OnWrite = (offset, bytes) => pagesWritten += Record(nonces, offset, bytes.Span);

        var pager = await Pager.CreateAsync(device, Options, Password, Ct);
        var tree = new BTree(pager);
        for (var step = 0; step < 150; step++)
        {
            await using (var tx = await pager.BeginWriteAsync(Ct))
            {
                for (var i = 0; i < 40; i++)
                {
                    await tree.PutAsync(tx, Key(random.Next(2000)), new byte[random.Next(1, 200)], Ct);
                }

                switch (random.Next(5))
                {
                    case 0:
                        break;
                    case 1:
                        // Falla después de haber escrito páginas: esos nonces ya quedaron usados.
                        device.FailOnWriteNumber = device.WriteCount + random.Next(2, 4);
                        try
                        {
                            await tx.CommitAsync(Ct);
                        }
                        catch (IOException)
                        {
                        }

                        device.FailOnWriteNumber = null;
                        break;
                    default:
                        await tx.CommitAsync(Ct);
                        break;
                }
            }

            if (random.Next(6) == 0)
            {
                await pager.DisposeAsync();
                pager = await Pager.OpenAsync(device, Options, Password, Ct);
                tree = new BTree(pager);
            }
        }

        await pager.DisposeAsync();
        Assert.True(pagesWritten > 1000, "The workload should write enough pages to mean something.");
        Assert.Equal(pagesWritten, nonces.Count);
    }

    [Fact]
    public async Task Two_copies_of_the_same_file_written_independently_do_not_share_a_nonce()
    {
        // Un archivo sincronizado entre dos equipos, o restaurado de un respaldo y vuelto a escribir.
        // Las dos copias tienen la misma clave y parten de la misma transacción.
        var original = NewDevice();
        await using (var pager = await Pager.CreateAsync(original, Options, Password, Ct))
        {
            await FillAsync(pager, rows: 500);
        }

        var seen = new List<HashSet<(uint Page, ulong Transaction)>>();
        for (var copy = 0; copy < 2; copy++)
        {
            var device = await original.CloneAsync();
            var nonces = new HashSet<(uint Page, ulong Transaction)>();
            device.OnWrite = (offset, bytes) => Record(nonces, offset, bytes.Span);
            await using var pager = await Pager.OpenAsync(device, Options, Password, Ct);
            for (var round = 0; round < 20; round++)
            {
                await FillAsync(pager, rows: 500);
            }

            seen.Add(nonces);
        }

        Assert.NotEmpty(seen[0]);
        Assert.Empty(seen[0].Intersect(seen[1]));
    }

    /// <summary>Anota el nonce de cada página de datos escrita. Devuelve cuántas eran.</summary>
    private static int Record(HashSet<(uint Page, ulong Transaction)> nonces, long offset, ReadOnlySpan<byte> bytes)
    {
        if (offset < 2 * PageBytes || offset % PageBytes != 0 || bytes.Length % PageBytes != 0)
        {
            return 0;
        }

        for (var at = 0; at < bytes.Length; at += PageBytes)
        {
            var page = bytes.Slice(at, PageBytes);
            Assert.Equal((uint)((offset + at) / PageBytes), PageLayout.Number(page).Value);
            nonces.Add((PageLayout.Number(page).Value, PageLayout.Transaction(page).Value));
        }

        return bytes.Length / PageBytes;
    }

    private static byte[] Key(int value)
    {
        var key = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(key, value);
        return key;
    }

    private static async Task FillAsync(Pager pager, int rows)
    {
        var tree = new BTree(pager);
        await using var tx = await pager.BeginWriteAsync(Ct);
        for (var i = 0; i < rows; i++)
        {
            await tree.PutAsync(tx, Key(i), "TEKIUM-SECRET-VALUE"u8.ToArray(), Ct);
        }

        await tx.CommitAsync(Ct);
    }

    private static async Task AssertRowsAsync(Pager pager, int rows)
    {
        var tree = new BTree(pager);
        var count = 0;
        await foreach (var entry in tree.RangeAsync(default, default, Ct))
        {
            Assert.True(entry.Value.Span.SequenceEqual("TEKIUM-SECRET-VALUE"u8));
            count++;
        }

        Assert.Equal(rows, count);
    }
}

public sealed class EncryptedDatabaseTests_File : EncryptedDatabaseTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class EncryptedDatabaseTests_Memory : EncryptedDatabaseTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
