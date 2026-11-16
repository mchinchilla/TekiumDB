// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Catalog;

/// <summary>
/// The past commits a database keeps readable for its change feed. Each commit writes into the catalog
/// where the catalog of the commit before it was: the key is that transaction, the value its root. The
/// entries of a catalog are therefore the retained states older than itself, in order.
/// </summary>
/// <remarks>
/// A lone marker entry says the history does not go back to the creation of the database: entries were
/// trimmed, or it started being kept when there already was data.
/// </remarks>
internal static class HistoryStore
{
    private const byte Prefix = 6;
    private const int KeySize = 1 + sizeof(ulong);

    private static readonly byte[] First = [Prefix];
    private static readonly byte[] Past = [Prefix + 1];

    /// <summary>Gets the key of the entry that says the history is not complete.</summary>
    public static ReadOnlyMemory<byte> Marker => First;

    public static byte[] Key(ulong transaction)
    {
        var key = new byte[KeySize];
        key[0] = Prefix;
        BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(1), transaction);
        return key;
    }

    public static byte[] Value(PageNumber root)
    {
        var value = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(value, root.Value);
        return value;
    }

    /// <summary>The retained transactions of a catalog, oldest first, and whether the history is marked incomplete.</summary>
    public static async ValueTask<(List<ulong> Transactions, bool Incomplete)> LoadAsync(
        IPageSource source, PageNumber catalogRoot, CancellationToken cancellationToken)
    {
        var transactions = new List<ulong>();
        var incomplete = false;
        await foreach (var entry in BTree.RangeAsync(source, catalogRoot, First, Past, cancellationToken).ConfigureAwait(false))
        {
            if (entry.Key.Length == 1)
            {
                incomplete = true;
            }
            else
            {
                transactions.Add(Read(entry).Transaction);
            }
        }

        return (transactions, incomplete);
    }

    /// <summary>
    /// Finds, in the history a catalog holds, the state a consumer at <paramref name="position"/> has
    /// seen and the commit that follows it.
    /// </summary>
    /// <returns>
    /// <c>Lost</c> if the changes after that position are no longer retained. Otherwise the state to
    /// compare from (without <c>HasBasis</c>, the empty database from before the first commit), and the
    /// next commit if the history has it (if not, the next is the catalog itself).
    /// </returns>
    public static async ValueTask<Found> FindAsync(
        IPageSource source, PageNumber catalogRoot, ulong position, CancellationToken cancellationToken)
    {
        // A consumer is almost always at a commit: its entry and the one after it are next to each other.
        var found = false;
        var basis = PageNumber.None;
        var basisTransaction = 0ul;
        await foreach (var entry in BTree.RangeAsync(source, catalogRoot, Key(position), Past, cancellationToken).ConfigureAwait(false))
        {
            var (transaction, root) = Read(entry);
            if (found)
            {
                return new Found(false, true, basisTransaction, basis, true, transaction, root);
            }

            if (transaction != position)
            {
                break;
            }

            (found, basis, basisTransaction) = (true, root, transaction);
        }

        if (found)
        {
            return new Found(false, true, basisTransaction, basis, false, 0, PageNumber.None);
        }

        // Not at a commit: the state it has seen is that of the last commit before its position.
        var incomplete = false;
        await foreach (var entry in BTree.RangeAsync(source, catalogRoot, First, Past, cancellationToken).ConfigureAwait(false))
        {
            if (entry.Key.Length == 1)
            {
                incomplete = true;
                continue;
            }

            var (transaction, root) = Read(entry);
            if (transaction > position)
            {
                // Before the first retained commit there is the empty database, unless the history starts later.
                return new Found(!found && incomplete, found, basisTransaction, basis, true, transaction, root);
            }

            (found, basis, basisTransaction) = (true, root, transaction);
        }

        return new Found(!found && incomplete, found, basisTransaction, basis, false, 0, PageNumber.None);
    }

    /// <summary>Where a consumer stands in the history.</summary>
    /// <param name="Lost">Whether the changes after its position are no longer retained.</param>
    /// <param name="HasBasis">Whether the state it has seen is a retained commit, as opposed to the empty database.</param>
    /// <param name="BasisTransaction">The transaction of that commit.</param>
    /// <param name="Basis">The catalog root of the state it has seen.</param>
    /// <param name="HasNext">Whether the history has the commit that follows.</param>
    /// <param name="Next">The transaction of that commit.</param>
    /// <param name="NextRoot">Its catalog root.</param>
    internal readonly record struct Found(
        bool Lost, bool HasBasis, ulong BasisTransaction, PageNumber Basis, bool HasNext, ulong Next, PageNumber NextRoot);

    private static (ulong Transaction, PageNumber Root) Read(BTreeEntry entry) =>
        entry.Key.Length == KeySize && entry.Value.Length == sizeof(uint)
            ? (BinaryPrimitives.ReadUInt64BigEndian(entry.Key.Span[1..]), new PageNumber(BinaryPrimitives.ReadUInt32LittleEndian(entry.Value.Span)))
            : throw new InvalidDataException("The history of commits in the catalog is damaged.");
}
