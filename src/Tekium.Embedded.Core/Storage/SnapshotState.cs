// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Storage;

/// <summary>A committed state plus the number of readers currently holding it.</summary>
internal sealed class SnapshotState(Snapshot snapshot, long created = 0)
{
    private int _readers;

    public Snapshot Snapshot { get; } = snapshot;

    /// <summary>Gets when the state was committed, as a stopwatch timestamp. Only taken when diagnostics are on.</summary>
    public long Created { get; } = created;

    public int Readers => Volatile.Read(ref _readers);

    public void AddReader() => Interlocked.Increment(ref _readers);

    public void RemoveReader() => Interlocked.Decrement(ref _readers);
}
