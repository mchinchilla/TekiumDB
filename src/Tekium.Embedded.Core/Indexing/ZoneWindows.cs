// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Indexing;

/// <summary>
/// The lowest and highest key of the rows of each window of time, kept in memory: what a transaction
/// has learnt about a zone map and not yet written to it.
/// </summary>
internal sealed class ZoneWindows
{
    // A time series writes window after window: the last one touched is nearly always the next one too.
    private long _lastWindow;
    private Range? _last;

    public SortedDictionary<long, Range> Ranges { get; } = [];

    public void Add(long window, ReadOnlySpan<byte> key)
    {
        var range = _last;
        if (range is null || window != _lastWindow)
        {
            if (!Ranges.TryGetValue(window, out range))
            {
                range = new Range(key);
                Ranges.Add(window, range);
            }

            (_lastWindow, _last) = (window, range);
        }

        range.Include(key);
    }

    public void Clear()
    {
        Ranges.Clear();
        _last = null;
    }

    /// <summary>The keys of one window: only ever wider.</summary>
    internal sealed class Range(ReadOnlySpan<byte> key)
    {
        public byte[] Low { get; private set; } = key.ToArray();

        public byte[] High { get; private set; } = key.ToArray();

        public void Include(ReadOnlySpan<byte> key)
        {
            if (key.SequenceCompareTo(High) > 0)
            {
                High = Replace(High, key);
            }
            else if (key.SequenceCompareTo(Low) < 0)
            {
                Low = Replace(Low, key);
            }
        }

        private static byte[] Replace(byte[] current, ReadOnlySpan<byte> key)
        {
            if (current.Length != key.Length)
            {
                return key.ToArray();
            }

            key.CopyTo(current);
            return current;
        }
    }
}
