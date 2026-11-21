// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using CsCheck;
using TekiumDB.Indexing;
using TekiumDB.Security;
using TekiumDB.Storage;
using TekiumDB.Tests.Storage;

namespace TekiumDB.Tests.Indexing;

/// <summary>
/// Ordenar más de lo que cabe en memoria, con las tandas en páginas temporales del propio archivo. El
/// modelo es ordenar una lista.
/// </summary>
public sealed class RunSorterTests
{
    private static readonly PagerOptions Options = new()
    {
        PageSize = PageSize.FromBytes(4096),
        CachePages = 64,
        KeyDerivation = new Argon2Parameters(MemoryKiB: 64, Iterations: 1, Parallelism: 1),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Entries_come_out_in_key_order_and_those_with_the_same_key_in_the_order_they_went_in()
    {
        // Claves cortas y repetidas, valores de todos los tamaños (algunos más largos que una página), y un
        // presupuesto de memoria que va desde "todo en tandas de una entrada" hasta "todo en memoria".
        var entry = Gen.Select(Gen.Byte[0, 5].Array[0, 3], Gen.Int[0, 20], Gen.Int[0, 300])
            .Select(static (key, large, length) => (key, large == 0 ? 5000 + length : length));
        await Gen.Select(entry.Array[0, 400], Gen.Int[0, 4].Select(static choice => new[] { 1, 300, 4000, 60_000, 10_000_000 }[choice])).SampleAsync(
            async (entries, budget) =>
            {
                await using var pager = await Pager.CreateAsync(TestDevice.Memory(), Options, Ct);
                await using var writer = await pager.BeginWriteAsync(Ct);
                var sorter = new RunSorter(writer, budget);
                var added = new List<(string Key, string Value)>();
                for (var i = 0; i < entries.Length; i++)
                {
                    // El valor lleva su número de orden: así se ve que los de igual clave no se cruzan.
                    var value = new byte[4 + entries[i].Item2];
                    BinaryPrimitives.WriteInt32BigEndian(value, i);
                    value.AsSpan(4).Fill((byte)i);
                    sorter.Add(entries[i].Item1, value);
                    added.Add((Convert.ToHexString(entries[i].Item1), Convert.ToHexString(value)));
                    if (sorter.IsFull)
                    {
                        await sorter.WriteRunAsync(Ct);
                    }
                }

                var sorted = new List<(string, string)>();
                while (await sorter.NextAsync(Ct))
                {
                    sorted.Add((Convert.ToHexString(sorter.Key), Convert.ToHexString(sorter.Value)));
                }

                Assert.Equal(entries.Length, sorter.Count);
                Assert.Equal(added.OrderBy(static pair => pair.Key, StringComparer.Ordinal), sorted);
                Assert.False(await sorter.NextAsync(Ct));

                // Las páginas temporales se devuelven: al confirmar no queda ninguna en uso.
                await writer.CommitAsync(Ct);
                await BTreeVerifier.AssertNoLeaksAsync(pager, Ct);
            },
            iter: 150);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sorter")]
    public async Task A_sort_larger_than_its_budget_keeps_little_in_memory_and_nothing_in_the_clear(string? password)
    {
        var device = TestDevice.Memory();
        await using var pager = await Pager.CreateAsync(device, Options, password, Ct);
        await using var writer = await pager.BeginWriteAsync(Ct);
        var sorter = new RunSorter(writer, 200_000);
        var random = new Random(68);
        var keys = new List<int>();
        var runs = 0;
        var most = 0;
        for (var i = 0; i < 60_000; i++)
        {
            keys.Add(random.Next());
            sorter.Add(Key(keys[^1]), "the secret payload of an entry"u8);
            if (sorter.IsFull)
            {
                most = Math.Max(most, writer.Dirty.Count);
                await sorter.WriteRunAsync(Ct);
                Assert.Empty(writer.Dirty);
                runs++;
            }
        }

        Assert.True(runs > 10, $"Only {runs} runs were written.");
        Assert.True(most < 80, $"{most} pages were held in memory.");

        keys.Sort();
        var position = 0;
        while (await sorter.NextAsync(Ct))
        {
            Assert.Equal(keys[position++], BinaryPrimitives.ReadInt32BigEndian(sorter.Key));
        }

        Assert.Equal(keys.Count, position);

        // Lo que llegó al archivo pasó por el pager: cifrado si la base lo está.
        var file = await device.ReadAllAsync();
        Assert.Equal(password is null, file.AsSpan().IndexOf("the secret payload"u8) >= 0);
    }

    private static byte[] Key(int value)
    {
        var key = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(key, value);
        return key;
    }
}
