// Copyright (c) Tekium. Licensed under the MIT License.

using System.Diagnostics;

namespace Tekium.Embedded.Storage;

/// <summary>
/// What the pager counts when diagnostics are on. A pager without one of these (the default) counts
/// nothing: every place that would count checks for it first and does no other work.
/// </summary>
internal sealed class PagerDiagnostics
{
    private long _hits;
    private long _misses;
    private SnapshotState[] _states = [];

    /// <summary>Gets or sets what to call when a commit is durable: pages written and milliseconds taken.</summary>
    public Action<int, double>? Committed { get; set; }

    public long Hits => Interlocked.Read(ref _hits);

    public long Misses => Interlocked.Read(ref _misses);

    public void Hit() => Interlocked.Increment(ref _hits);

    public void Miss() => Interlocked.Increment(ref _misses);

    /// <summary>The states that may still have readers, as the writer last saw them: the retained ones and the current.</summary>
    public void Publish(List<SnapshotState> retained, SnapshotState current) =>
        Volatile.Write(ref _states, [.. retained, current]);

    /// <summary>How many readers hold a state, and how long ago the oldest state held was committed, in seconds.</summary>
    public (long Live, double OldestAge) Snapshots()
    {
        long live = 0;
        long oldest = 0;
        foreach (var state in Volatile.Read(ref _states))
        {
            var readers = state.Readers;
            if (readers > 0)
            {
                live += readers;
                oldest = oldest == 0 ? state.Created : Math.Min(oldest, state.Created);
            }
        }

        return (live, oldest == 0 ? 0 : Stopwatch.GetElapsedTime(oldest).TotalSeconds);
    }
}
