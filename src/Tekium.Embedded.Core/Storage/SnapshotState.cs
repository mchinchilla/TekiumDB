// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Storage;

/// <summary>A committed state plus the number of readers currently holding it.</summary>
internal sealed class SnapshotState(Snapshot snapshot)
{
    private int _readers;

    public Snapshot Snapshot { get; } = snapshot;

    public int Readers => Volatile.Read(ref _readers);

    public void AddReader() => Interlocked.Increment(ref _readers);

    public void RemoveReader() => Interlocked.Decrement(ref _readers);
}
