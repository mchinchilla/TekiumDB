// Copyright (c) Tekium. Licensed under the MIT License.

namespace TekiumDB;

/// <summary>
/// Something that keeps trees of its own inside a write transaction and has to record where they are
/// when the transaction commits. The engine calls it once, before the catalog root is fixed.
/// </summary>
internal interface ITkCommitParticipant
{
    /// <summary>Writes to the catalog what the participant changed, if anything.</summary>
    ValueTask FlushAsync(TkWriteTransaction transaction, CancellationToken cancellationToken);
}
