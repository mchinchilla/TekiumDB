// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Tests.Storage;

public sealed class FreeListTests
{
    private static readonly PagerOptions Options = new() { PageSize = PageSize.FromBytes(4096), CachePages = 32 };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Rewriting_the_same_pages_forever_does_not_grow_the_file()
    {
        var device = new InMemoryStorageDevice();
        await using var pager = await Pager.CreateAsync(device, Options, Ct);
        var live = await AllocateAsync(pager, 50, fill: 1);

        for (var round = 0; round < 100; round++)
        {
            live = await RewriteAllAsync(pager, live, (byte)(round + 2));
        }

        var settled = pager.Current.PageCount;
        for (var round = 0; round < 200; round++)
        {
            live = await RewriteAllAsync(pager, live, (byte)round);
        }

        Assert.Equal(settled, pager.Current.PageCount);
        Assert.InRange(settled, 52u, 220u);
        await AssertContentAsync(pager, live, 199);
    }

    [Fact]
    public async Task Pages_freed_by_a_transaction_are_not_reused_by_the_next_one()
    {
        // El árbol anterior debe quedar intacto en disco un commit más: es el respaldo del slot de raíz alterno.
        var device = new InMemoryStorageDevice();
        await using var pager = await Pager.CreateAsync(device, Options, Ct);
        var first = await AllocateAsync(pager, 10, fill: 1);
        var afterFirst = pager.Current.PageCount;

        var second = await RewriteAllAsync(pager, first, 2);
        var third = await RewriteAllAsync(pager, second, 3);

        Assert.DoesNotContain(second, first.Contains);
        Assert.DoesNotContain(third, first.Contains);
        Assert.True(pager.Current.PageCount > afterFirst + 10);

        var fourth = await RewriteAllAsync(pager, third, 4);
        Assert.Contains(fourth, first.Contains);
    }

    [Fact]
    public async Task A_live_reader_keeps_every_page_of_its_snapshot_until_it_is_disposed()
    {
        var device = new InMemoryStorageDevice();
        await using var pager = await Pager.CreateAsync(device, Options, Ct);
        var original = await AllocateAsync(pager, 40, fill: 7);
        var live = original;

        var reader = pager.BeginRead();
        for (var round = 0; round < 30; round++)
        {
            live = await RewriteAllAsync(pager, live, (byte)(100 + round));
        }

        var grownWhileReading = pager.Current.PageCount;
        await AssertContentAsync(pager, original, 7);
        Assert.True(grownWhileReading > 40u * 25, "Nothing should be reused while the reader holds its snapshot.");

        reader.Dispose();
        for (var round = 0; round < 30; round++)
        {
            live = await RewriteAllAsync(pager, live, (byte)round);
        }

        Assert.Equal(grownWhileReading, pager.Current.PageCount);
        await AssertContentAsync(pager, live, 29);
    }

    [Fact]
    public async Task The_free_list_survives_reopening()
    {
        var device = new InMemoryStorageDevice();
        PageNumber[] live;
        uint settled;
        int freeBefore;
        await using (var pager = await Pager.CreateAsync(device, Options, Ct))
        {
            live = await AllocateAsync(pager, 3000, fill: 1);
            for (var round = 0; round < 5; round++)
            {
                live = await RewriteAllAsync(pager, live, (byte)(round + 2));
            }

            settled = pager.Current.PageCount;
            freeBefore = pager.FreePageCount;
            Assert.False(pager.Current.FreeList.IsNone);
        }

        await using var reopened = await Pager.OpenAsync(device, Options, Ct);
        Assert.Equal(freeBefore, reopened.FreePageCount);
        Assert.True(freeBefore >= 3000, "More than one trunk page is needed to hold this many free pages.");
        for (var round = 0; round < 5; round++)
        {
            live = await RewriteAllAsync(reopened, live, (byte)(round + 50));
        }

        Assert.InRange(reopened.Current.PageCount, settled, settled + 8);
        await AssertContentAsync(reopened, live, 54);
    }

    [Fact]
    public async Task A_rolled_back_transaction_returns_the_free_pages_it_took()
    {
        var device = new InMemoryStorageDevice();
        await using var pager = await Pager.CreateAsync(device, Options, Ct);
        var live = await AllocateAsync(pager, 20, fill: 1);
        for (var round = 0; round < 5; round++)
        {
            live = await RewriteAllAsync(pager, live, 2);
        }

        var before = pager.Current;
        var freeBefore = pager.FreePageCount;
        await using (var tx = await pager.BeginWriteAsync(Ct))
        {
            for (var i = 0; i < 15; i++)
            {
                tx.Allocate(PageType.BTreeLeaf, out _);
            }
        }

        Assert.Equal(before, pager.Current);
        Assert.Equal(freeBefore, pager.FreePageCount);
        live = await RewriteAllAsync(pager, live, 3);
        await AssertContentAsync(pager, live, 3);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Random_allocation_and_release_never_hands_out_a_page_that_is_still_in_use(int seed)
    {
        // Modelo: página viva -> byte que contiene. Si la free-list entregara una página viva
        // (o una retenida por un lector), su contenido cambiaría y la verificación lo vería.
        var random = new Random(seed);
        var device = new InMemoryStorageDevice();
        var model = new Dictionary<uint, byte>();
        var pager = await Pager.CreateAsync(device, Options, Ct);
        ReadSnapshot? reader = null;
        Dictionary<uint, byte>? readerView = null;
        uint peak = 0;

        for (var step = 0; step < 400; step++)
        {
            var next = new Dictionary<uint, byte>(model);
            var failWrite = random.Next(12) == 0;
            await using (var tx = await pager.BeginWriteAsync(Ct))
            {
                foreach (var number in next.Keys.ToArray())
                {
                    var action = random.Next(10);
                    if (action < 3)
                    {
                        tx.Free(new PageNumber(number));
                        next.Remove(number);
                    }
                    else if (action < 6)
                    {
                        using var old = await pager.PinAsync(new PageNumber(number), Ct);
                        var fill = (byte)random.Next(256);
                        tx.CopyForWrite(old, new PageNumber(number), out var moved).Span.Fill(fill);
                        next.Remove(number);
                        next[moved.Value] = fill;
                    }
                }

                var additions = random.Next(0, Math.Max(1, 120 - next.Count));
                for (var i = 0; i < additions; i++)
                {
                    var fill = (byte)random.Next(256);
                    tx.Allocate(PageType.BTreeLeaf, out var number).Span.Fill(fill);
                    Assert.True(next.TryAdd(number.Value, fill), $"Page {number.Value} was handed out twice.");
                }

                var outcome = random.Next(10);
                if (failWrite)
                {
                    device.FailOnWriteNumber = device.WriteCount + random.Next(1, 3);
                    try
                    {
                        await tx.CommitAsync(Ct);
                        model = next;
                    }
                    catch (IOException)
                    {
                    }

                    device.FailOnWriteNumber = null;
                }
                else if (outcome > 1)
                {
                    await tx.CommitAsync(Ct);
                    model = next;
                }
            }

            await AssertModelAsync(pager, model);
            if (readerView is not null)
            {
                await AssertModelAsync(pager, readerView);
            }

            switch (random.Next(25))
            {
                case 0:
                    reader?.Dispose();
                    reader = null;
                    readerView = null;
                    await pager.DisposeAsync();
                    pager = await Pager.OpenAsync(device, Options, Ct);
                    await AssertModelAsync(pager, model);
                    break;
                case 1 or 2 when reader is null:
                    reader = pager.BeginRead();
                    readerView = new Dictionary<uint, byte>(model);
                    break;
                case 3 or 4 or 5 when reader is not null:
                    reader.Value.Dispose();
                    reader = null;
                    readerView = null;
                    break;
                default:
                    break;
            }

            peak = Math.Max(peak, pager.Current.PageCount);
        }

        reader?.Dispose();
        await pager.DisposeAsync();

        // Nunca hay más de ~120 páginas vivas; sin reutilización el archivo pasaría de las 20 000.
        Assert.InRange(peak, 3u, 6000u);
    }

    private static async Task<PageNumber[]> AllocateAsync(Pager pager, int count, byte fill)
    {
        var numbers = new PageNumber[count];
        await using var tx = await pager.BeginWriteAsync(Ct);
        for (var i = 0; i < count; i++)
        {
            tx.Allocate(PageType.BTreeLeaf, out numbers[i]).Span.Fill(fill);
        }

        await tx.CommitAsync(Ct);
        return numbers;
    }

    /// <summary>Copy-on-write de cada página: la vieja queda libre y la nueva recibe <paramref name="fill"/>.</summary>
    private static async Task<PageNumber[]> RewriteAllAsync(Pager pager, PageNumber[] live, byte fill)
    {
        var moved = new PageNumber[live.Length];
        await using var tx = await pager.BeginWriteAsync(Ct);
        for (var i = 0; i < live.Length; i++)
        {
            using var old = await pager.PinAsync(live[i], Ct);
            tx.CopyForWrite(old, live[i], out moved[i]).Span.Fill(fill);
        }

        await tx.CommitAsync(Ct);
        return moved;
    }

    private static async Task AssertContentAsync(Pager pager, PageNumber[] pages, byte fill)
    {
        foreach (var number in pages)
        {
            using var page = await pager.PinAsync(number, Ct);
            Assert.True(page.Span[0] == fill && page.Span[^1] == fill, $"Page {number.Value} lost its contents.");
        }
    }

    private static async Task AssertModelAsync(Pager pager, Dictionary<uint, byte> model)
    {
        foreach (var (number, fill) in model)
        {
            using var page = await pager.PinAsync(new PageNumber(number), Ct);
            Assert.True(
                page.Span[0] == fill && page.Span[^1] == fill,
                $"Page {number} holds {page.Span[0]} but the model expects {fill}.");
        }
    }
}
