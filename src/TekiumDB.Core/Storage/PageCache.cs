// Copyright (c) Tekium. Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace TekiumDB.Storage;

/// <summary>
/// Bounded cache of committed pages with clock eviction. Lock-free: readers pin with a compare-and-swap,
/// and eviction claims a frame the same way, so neither ever waits for the other.
/// </summary>
/// <remarks>
/// It only ever holds committed pages, which are immutable, so eviction never writes anything back.
/// The capacity is a target, not a wall: when every frame is pinned a load gets a transient frame
/// that is dropped as soon as it is unpinned.
/// </remarks>
internal sealed class PageCache
{
    private const int MinimumCapacity = 16;

    private readonly ConcurrentDictionary<uint, PageFrame> _map;
    private readonly PageFrame?[] _frames;
    private int _hand;

    public PageCache(int capacity)
    {
        capacity = Math.Max(capacity, MinimumCapacity);
        _frames = new PageFrame?[capacity];
        _map = new ConcurrentDictionary<uint, PageFrame>(Environment.ProcessorCount, capacity);
    }

    public int Capacity => _frames.Length;

    /// <summary>The hot path: one dictionary lookup and one compare-and-swap, no allocation.</summary>
    public bool TryPin(PageNumber number, out PageLease lease)
    {
        if (_map.TryGetValue(number.Value, out var frame) && frame.TryPin(number.Value))
        {
            lease = new PageLease(this, frame);
            return true;
        }

        lease = default;
        return false;
    }

    /// <summary>Takes exclusive ownership of a frame to fill it. Never fails.</summary>
    public PageFrame ClaimForLoad() => TryClaim(out var frame) ? frame : new PageFrame { Transient = true };

    /// <summary>Takes exclusive ownership of an evictable frame, if there is one.</summary>
    public bool TryClaim([NotNullWhen(true)] out PageFrame? frame)
    {
        var capacity = _frames.Length;
        for (var attempt = 0; attempt < capacity * 2; attempt++)
        {
            var index = (int)((uint)Interlocked.Increment(ref _hand) % (uint)capacity);
            var candidate = Volatile.Read(ref _frames[index]);
            if (candidate is null)
            {
                var fresh = new PageFrame();
                if (Interlocked.CompareExchange(ref _frames[index], fresh, null) is null)
                {
                    frame = fresh;
                    return true;
                }

                continue;
            }

            if (candidate.Referenced)
            {
                // Second chance: used since the hand last passed.
                candidate.Referenced = false;
                continue;
            }

            if (candidate.TryClaim())
            {
                _map.TryRemove(new KeyValuePair<uint, PageFrame>(candidate.Number, candidate));
                frame = candidate;
                return true;
            }
        }

        frame = null;
        return false;
    }

    /// <summary>
    /// Makes a frame just read from disk visible and returns it pinned. If another reader loaded the
    /// same page first, that copy wins and this frame goes back to the pool.
    /// </summary>
    public PageLease PublishLoaded(PageFrame frame, PageNumber number)
    {
        frame.Publish(number.Value, pins: 1);
        while (true)
        {
            if (_map.TryAdd(number.Value, frame))
            {
                return new PageLease(this, frame);
            }

            if (_map.TryGetValue(number.Value, out var existing))
            {
                if (existing.TryPin(number.Value))
                {
                    Abandon(frame);
                    return new PageLease(this, existing);
                }

                // The entry points to a frame that was since claimed for something else: replace it.
                if (_map.TryUpdate(number.Value, frame, existing))
                {
                    return new PageLease(this, frame);
                }
            }
        }
    }

    /// <summary>
    /// Makes a page just committed visible, replacing any copy cached under the same number
    /// (page numbers are reused once nobody can reach their old contents).
    /// </summary>
    public void PublishCommitted(PageFrame frame, PageNumber number)
    {
        // Into the map while still claimed, so eviction cannot take it half-published.
        frame.SetNumber(number.Value);
        _map[number.Value] = frame;
        frame.Publish(number.Value, pins: 0);
    }

    public void Invalidate(PageNumber number) => _map.TryRemove(number.Value, out _);

    /// <summary>Returns a claimed frame unused, for example after a failed read.</summary>
    public static void Abandon(PageFrame frame)
    {
        if (!frame.Transient)
        {
            frame.Reset();
        }
    }

    internal void Release(PageFrame frame)
    {
        if (frame.Unpin() == 0 && frame.Transient && frame.TryClaim())
        {
            _map.TryRemove(new KeyValuePair<uint, PageFrame>(frame.Number, frame));
        }
    }
}
