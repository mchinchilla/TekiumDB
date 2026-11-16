// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Storage;

/// <summary>
/// An immutable committed state of the database: everything reachable from <paramref name="Root"/>.
/// It is what a root slot in the header stores.
/// </summary>
/// <param name="Root">Root page of the tree, or <see cref="PageNumber.None"/> for an empty database.</param>
/// <param name="PageCount">Pages in use, including the header page.</param>
/// <param name="TransactionId">Transaction that produced this state.</param>
internal sealed record Snapshot(PageNumber Root, uint PageCount, TransactionId TransactionId);
