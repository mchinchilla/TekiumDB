// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded.Storage;

/// <summary>Monotonic id of a committed write transaction. Id 0 is the empty database.</summary>
internal readonly record struct TransactionId(ulong Value)
{
    public TransactionId Next() => new(Value + 1);
}
