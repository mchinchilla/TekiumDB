// Copyright (c) Tekium. Licensed under the MIT License.

namespace Tekium.Embedded;

/// <summary>What a commit did to a row.</summary>
public enum TkChangeKind
{
    /// <summary>The row did not exist before the commit.</summary>
    Insert = 0,

    /// <summary>The row existed and the commit left it different.</summary>
    Update = 1,

    /// <summary>The row no longer exists after the commit.</summary>
    Delete = 2,
}

/// <summary>
/// One row a commit changed, as <see cref="TekiumDb.WatchAsync{T}"/> reports it: the net effect of the
/// whole transaction on that row, not each step of it.
/// </summary>
/// <typeparam name="T">The row type.</typeparam>
/// <param name="Kind">What happened to the row.</param>
/// <param name="Before">The row before the commit, or <see langword="null"/> if it was inserted.</param>
/// <param name="After">The row after the commit, or <see langword="null"/> if it was deleted.</param>
/// <param name="TransactionId">The transaction that committed the change. Resume from it to get what came after.</param>
public sealed record TkChange<T>(TkChangeKind Kind, T? Before, T? After, ulong TransactionId)
    where T : class, ITkRow<T>;
