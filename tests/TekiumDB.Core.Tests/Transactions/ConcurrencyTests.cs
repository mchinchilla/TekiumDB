// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using System.Diagnostics;
using TekiumDB.Indexing;
using TekiumDB.Storage;
using TekiumDB.Tests.Indexing;
using TekiumDB.Tests.Storage;
using TekiumDB.Transactions;

namespace TekiumDB.Tests.Transactions;

/// <summary>
/// Un escritor y varios lectores en hilos distintos. Cada transacción reescribe la base entera con su
/// número de generación, así que un lector que viera medio commit, o una página reutilizada debajo de
/// su snapshot, lo notaría en cualquier fila.
/// </summary>
public abstract class ConcurrencyTests : DeviceBackedTests
{
    private const int Keys = 300;
    private const int Readers = 4;

    // Caché mucho más chica que la base: los lectores vuelven al almacenamiento todo el tiempo.
    private static readonly PagerOptions Options = new() { PageSize = PageSize.FromBytes(4096), CachePages = 24 };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public Task Readers_never_see_a_partial_commit_while_the_writer_runs() => RunAsync(TimeSpan.FromSeconds(2));

    /// <summary>La versión larga que pide el plan. No corre en CI: <c>--explicit only</c>.</summary>
    [Fact(Explicit = true)]
    public Task Readers_never_see_a_partial_commit_for_several_minutes() => RunAsync(TimeSpan.FromMinutes(3));

    private static byte[] KeyOf(int key)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, key * 2654435761L);
        return bytes;
    }

    private static bool IsPresent(int key, long generation) => (key + generation) % 7 != 0;

    private static int LengthOf(int key, long generation) => 8 + (int)(((key * 31L) + (generation * 17)) % 300);

    private static async Task WriteGenerationAsync(Pager pager, BTree tree, long generation)
    {
        var value = new byte[8 + 300];
        await using var writer = await pager.BeginWriteAsync(Ct);
        for (var key = 0; key < Keys; key++)
        {
            if (!IsPresent(key, generation))
            {
                await tree.DeleteAsync(writer, KeyOf(key), Ct);
                continue;
            }

            var length = LengthOf(key, generation);
            BinaryPrimitives.WriteInt64LittleEndian(value, generation);
            value.AsSpan(8, length - 8).Fill((byte)key);
            await tree.PutAsync(writer, KeyOf(key), value.AsMemory(0, length), Ct);
        }

        await writer.CommitAsync(Ct);
    }

    /// <summary>Lee la base entera dentro de una transacción y devuelve la generación que vio, toda de la misma.</summary>
    private static async Task<long> ReadGenerationAsync(ReadTransaction read)
    {
        var seen = new Dictionary<long, int>();
        long generation = -1;
        await foreach (var entry in BTree.RangeAsync(read, read.Root, default, default, Ct))
        {
            var entryGeneration = BinaryPrimitives.ReadInt64LittleEndian(entry.Value.Span);
            generation = generation < 0 ? entryGeneration : generation;
            Assert.Equal(generation, entryGeneration);
            seen.Add(BinaryPrimitives.ReadInt64BigEndian(entry.Key.Span), entry.Value.Length);
        }

        for (var key = 0; key < Keys; key++)
        {
            var present = seen.TryGetValue(key * 2654435761L, out var length);
            Assert.Equal(IsPresent(key, generation), present);
            Assert.True(!present || length == LengthOf(key, generation), "A value has the length of another generation.");
        }

        return generation;
    }

    private async Task RunAsync(TimeSpan duration)
    {
        await using var pager = await Pager.CreateAsync(NewDevice(), Options, Ct);
        var tree = new BTree(pager);
        await WriteGenerationAsync(pager, tree, 0);

        using var stop = new CancellationTokenSource();
        var rounds = new int[Readers];
        var readers = new Task[Readers];
        for (var i = 0; i < Readers; i++)
        {
            // Uno de los lectores es lento: retiene su snapshot mientras pasan muchos commits.
            var reader = i;
            var pause = i == 0 ? TimeSpan.FromMilliseconds(15) : TimeSpan.Zero;
            readers[i] = Task.Run(() => ReadLoopAsync(pager, pause, () => Interlocked.Increment(ref rounds[reader]), stop.Token), Ct);
        }

        // Se mide por trabajo hecho, no sólo por tiempo: con la máquina ocupada un lector puede tardar en
        // arrancar, y la prueba no dice nada si no leyó mientras el escritor escribía.
        long generation = 0;
        try
        {
            var clock = Stopwatch.StartNew();
            while ((clock.Elapsed < duration || generation < 20 || rounds.Any(static count => Volatile.Read(ref count) < 3))
                && clock.Elapsed < duration + TimeSpan.FromMinutes(2)
                && !readers.Any(static reader => reader.IsCompleted))
            {
                await WriteGenerationAsync(pager, tree, ++generation);
            }
        }
        finally
        {
            await stop.CancelAsync();
        }

        await Task.WhenAll(readers);
        Assert.All(rounds, static count => Assert.True(count >= 3, "A reader barely read while the writer ran."));

        // Sin lectores, lo retenido se libera en los commits siguientes y la contabilidad cierra.
        await WriteGenerationAsync(pager, tree, ++generation);
        await WriteGenerationAsync(pager, tree, ++generation);
        await BTreeVerifier.AssertNoLeaksAsync(pager, Ct);
        TestContext.Current.SendDiagnosticMessage(
            "Concurrency: {0} generations, {1} reader rounds, {2} pages in the file.",
            generation,
            rounds.Sum(),
            pager.Current.PageCount);
    }

    private static async Task ReadLoopAsync(Pager pager, TimeSpan pause, Action completedRound, CancellationToken stop)
    {
        var buffer = new byte[8 + 300];
        var random = new Random(pause.Milliseconds + 1);
        long last = 0;
        while (!stop.IsCancellationRequested)
        {
            await using var read = ReadTransaction.Begin(pager);
            var generation = await ReadGenerationAsync(read);
            Assert.True(generation >= last, $"A reader went back in time: {last} then {generation}.");
            last = generation;

            // Lectura repetible: tiempo después, la misma transacción sigue viendo la misma generación.
            if (pause > TimeSpan.Zero)
            {
                await Task.Delay(pause, Ct);
            }
            else
            {
                await Task.Yield();
            }

            for (var i = 0; i < 20; i++)
            {
                var key = random.Next(Keys);
                var length = await BTree.GetAsync(read, read.Root, KeyOf(key), buffer, Ct);
                Assert.Equal(IsPresent(key, generation) ? LengthOf(key, generation) : BTree.NotFound, length);
                Assert.True(
                    length < 0 || BinaryPrimitives.ReadInt64LittleEndian(buffer) == generation,
                    "A point read saw another generation inside the same transaction.");
            }

            Assert.Equal(generation, await ReadGenerationAsync(read));
            completedRound();
        }
    }
}

public sealed class ConcurrencyTests_File : ConcurrencyTests
{
    private protected override TestDevice CreateDevice() => TestDevice.File(flushToDisk: false);
}

public sealed class ConcurrencyTests_Memory : ConcurrencyTests
{
    private protected override TestDevice CreateDevice() => TestDevice.Memory();
}
