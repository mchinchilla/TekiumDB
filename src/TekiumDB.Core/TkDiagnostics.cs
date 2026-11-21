// Copyright (c) Tekium. Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using TekiumDB.Storage;

namespace TekiumDB;

/// <summary>
/// The counters of one database, published through a <see cref="Meter"/>. It exists only when the
/// database was opened with <see cref="DbOptions.Diagnostics"/>.
/// </summary>
internal sealed class TkDiagnostics : IDisposable
{
    private readonly ConcurrentDictionary<string, int> _depths = new(StringComparer.Ordinal);
    private readonly Meter _meter;

    public TkDiagnostics(string name, PagerDiagnostics pager)
    {
        _meter = new Meter(new MeterOptions(TekiumDb.MeterName) { Tags = [new KeyValuePair<string, object?>("tekium.db", name)] });
        _meter.CreateObservableCounter("tekium.cache.hits", () => pager.Hits, "{page}", "Pages found in the cache.");
        _meter.CreateObservableCounter("tekium.cache.misses", () => pager.Misses, "{page}", "Pages read from the file because they were not in the cache.");
        var pages = _meter.CreateHistogram<int>("tekium.commit.pages", "{page}", "Pages written by a commit.");
        var duration = _meter.CreateHistogram<double>("tekium.commit.duration", "ms", "Time a commit took, from its first write to being durable.");
        pager.Committed = (written, milliseconds) =>
        {
            pages.Record(written);
            duration.Record(milliseconds);
        };
        _meter.CreateObservableGauge("tekium.snapshots.live", () => pager.Snapshots().Live, "{snapshot}", "Readers holding a state of the database.");
        _meter.CreateObservableGauge(
            "tekium.snapshots.oldest_age", () => pager.Snapshots().OldestAge, "s", "How long ago the oldest state still held by a reader was committed.");
        _meter.CreateObservableGauge("tekium.tree.depth", Depths, "{level}", "Levels of each table and index written since the database was opened.");
    }

    /// <summary>Records how many levels a tree has after a commit that changed it.</summary>
    public void SetDepth(string tree, int depth) => _depths[tree] = depth;

    public void Dispose() => _meter.Dispose();

    private IEnumerable<Measurement<int>> Depths()
    {
        foreach (var (tree, depth) in _depths)
        {
            yield return new Measurement<int>(depth, new KeyValuePair<string, object?>("tekium.tree", tree));
        }
    }
}
