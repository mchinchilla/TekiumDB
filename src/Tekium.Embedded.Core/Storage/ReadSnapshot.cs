// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Storage;

/// <summary>
/// A reader's hold on one committed state. While it lives, no page reachable from that state is reused.
/// A forgotten one is a space leak: dispose exactly once.
/// </summary>
internal readonly struct ReadSnapshot : IDisposable
{
    private readonly SnapshotState _state;

    internal ReadSnapshot(SnapshotState state) => _state = state;

    public Snapshot Snapshot => _state.Snapshot;

    public PageNumber Root => _state.Snapshot.Root;

    public void Dispose() => _state.RemoveReader();
}
