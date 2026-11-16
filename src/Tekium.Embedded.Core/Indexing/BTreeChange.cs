// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Indexing;

/// <summary>
/// One key whose value differs between two states of a tree: added, removed or replaced. Like a scan
/// entry, it is valid until the comparison moves on.
/// </summary>
/// <param name="Key">The key.</param>
/// <param name="HasBefore">Whether the older state has the key.</param>
/// <param name="Before">Its value in the older state.</param>
/// <param name="HasAfter">Whether the newer state has the key.</param>
/// <param name="After">Its value in the newer state.</param>
internal readonly record struct BTreeChange(
    ReadOnlyMemory<byte> Key, bool HasBefore, ReadOnlyMemory<byte> Before, bool HasAfter, ReadOnlyMemory<byte> After);
