// Copyright (c) Tekium. Licensed under the MIT License.

namespace TekiumDB.Storage;

/// <summary>
/// An immutable committed state of the database: everything reachable from <paramref name="Root"/>.
/// It is what a root slot in the header stores.
/// </summary>
/// <param name="Root">Root page of the tree, or <see cref="PageNumber.None"/> for an empty database.</param>
/// <param name="PageCount">High-water mark of the file in pages, including the two meta pages.</param>
/// <param name="TransactionId">Transaction that produced this state.</param>
/// <param name="FreeList">First directory page of the free-list, or <see cref="PageNumber.None"/>.</param>
internal sealed record Snapshot(PageNumber Root, uint PageCount, TransactionId TransactionId, PageNumber FreeList);
