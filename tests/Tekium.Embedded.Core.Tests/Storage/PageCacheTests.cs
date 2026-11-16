// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Tests.Storage;

public sealed class PageCacheTests
{
    private const int PageBytes = 4096;

    [Fact]
    public void A_page_that_was_never_loaded_is_a_miss()
    {
        var cache = new PageCache(16);

        Assert.False(cache.TryPin(new PageNumber(5), out _));
    }

    [Fact]
    public void A_loaded_page_is_a_hit_with_the_same_bytes()
    {
        var cache = new PageCache(16);
        using (Load(cache, 5))
        {
        }

        Assert.True(cache.TryPin(new PageNumber(5), out var lease));
        using (lease)
        {
            Assert.Equal(PageType.BTreeLeaf, lease.Type);
            Assert.Equal(PageBytes - 32, lease.Span.Length);
            Assert.All(lease.Span.ToArray(), b => Assert.Equal(5, b));
        }
    }

    [Fact]
    public void Loading_more_pages_than_the_capacity_evicts_unpinned_ones()
    {
        var cache = new PageCache(16);
        for (uint i = 2; i < 200; i++)
        {
            using (Load(cache, i))
            {
            }
        }

        var cached = Enumerable.Range(2, 198).Count(i => TryPinAndRelease(cache, (uint)i));

        Assert.InRange(cached, 1, 16);
    }

    [Fact]
    public void A_pinned_page_is_never_evicted_and_keeps_its_bytes()
    {
        var cache = new PageCache(16);
        using var pinned = Load(cache, 2);

        for (uint i = 3; i < 500; i++)
        {
            using (Load(cache, i))
            {
            }
        }

        Assert.All(pinned.Span.ToArray(), b => Assert.Equal(2, b));
        Assert.True(TryPinAndRelease(cache, 2));
    }

    [Fact]
    public void When_every_frame_is_pinned_loads_still_succeed()
    {
        var cache = new PageCache(16);
        var held = new List<PageLease>();
        for (uint i = 2; i < 60; i++)
        {
            held.Add(Load(cache, i));
        }

        for (var i = 0; i < held.Count; i++)
        {
            Assert.Equal((byte)(i + 2), held[i].Span[0]);
            held[i].Dispose();
        }
    }

    [Fact]
    public void Publishing_a_committed_page_replaces_the_stale_copy_of_a_reused_number()
    {
        var cache = new PageCache(16);
        using (Load(cache, 7))
        {
        }

        Assert.True(cache.TryClaim(out var frame));
        frame.Buffer = NewPage(7, fill: 0xEE);
        cache.PublishCommitted(frame, new PageNumber(7));

        Assert.True(cache.TryPin(new PageNumber(7), out var lease));
        using (lease)
        {
            Assert.Equal(0xEE, lease.Span[0]);
        }
    }

    [Fact]
    public void An_invalidated_page_becomes_a_miss()
    {
        var cache = new PageCache(16);
        using (Load(cache, 7))
        {
        }

        cache.Invalidate(new PageNumber(7));

        Assert.False(cache.TryPin(new PageNumber(7), out _));
    }

    [Fact]
    public async Task Concurrent_readers_always_see_the_page_they_asked_for()
    {
        // 8 lectores sobre 64 páginas con sólo 16 marcos: desalojo constante mientras otros fijan.
        var cache = new PageCache(16);
        var failures = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));

        var readers = Enumerable.Range(0, 8).Select(seed => Task.Run(
            () =>
            {
                var random = new Random(seed);
                while (!stop.IsCancellationRequested)
                {
                    var number = (uint)random.Next(2, 66);
                    using var lease = cache.TryPin(new PageNumber(number), out var hit) ? hit : Load(cache, number);
                    var span = lease.Span;
                    if (span[0] != (byte)number || span[^1] != (byte)number)
                    {
                        Interlocked.Increment(ref failures);
                    }
                }
            },
            TestContext.Current.CancellationToken));
        await Task.WhenAll(readers);

        Assert.Equal(0, failures);
    }

    private static bool TryPinAndRelease(PageCache cache, uint number)
    {
        if (!cache.TryPin(new PageNumber(number), out var lease))
        {
            return false;
        }

        lease.Dispose();
        return true;
    }

    // Lo que hace el pager en un fallo de caché: reclamar un marco, llenarlo y publicarlo.
    private static PageLease Load(PageCache cache, uint number)
    {
        var frame = cache.ClaimForLoad();
        frame.Buffer ??= new byte[PageBytes];
        NewPage(number, (byte)number).CopyTo(frame.Buffer, 0);
        return cache.PublishLoaded(frame, new PageNumber(number));
    }

    private static byte[] NewPage(uint number, byte fill)
    {
        var page = new byte[PageBytes];
        PageLayout.Stamp(page, PageType.BTreeLeaf, new PageNumber(number), new TransactionId(1));
        PageLayout.Payload(page).Span.Fill(fill);
        return page;
    }
}
