// Copyright (c) Tekium. Licensed under the MIT License.

namespace TekiumDB.Indexing;

/// <summary>
/// One key and value produced by a scan. Both point into the page itself, which is immutable
/// because committed pages are never overwritten, so nothing is copied.
/// </summary>
internal readonly record struct BTreeEntry(ReadOnlyMemory<byte> Key, ReadOnlyMemory<byte> Value);
