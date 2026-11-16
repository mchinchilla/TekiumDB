// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Storage;

/// <summary>What a page holds. Stored in the clear in the first byte of every page.</summary>
internal enum PageType : byte
{
    None = 0,
    BTreeLeaf = 1,
    BTreeInternal = 2,
    FreeListTrunk = 3,
    FreeListDirectory = 4,
}
