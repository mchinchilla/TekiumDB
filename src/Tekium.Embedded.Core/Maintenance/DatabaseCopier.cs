// Copyright (c) Tekium. Licensed under the MIT License.

using System.Text;
using Tekium.Embedded.Catalog;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Maintenance;

/// <summary>
/// Writes into a new database what a walk reads from another: each tree is rebuilt bottom-up from its
/// entries, which arrive in key order, and its catalog entry is written with the new root. Pages are
/// spilled as they fill, so the copy does not hold the database in memory.
/// </summary>
/// <remarks>
/// When copying a damaged database, what is left out is chosen so that the copy never answers wrongly:
/// an index that lost entries, or whose table lost rows, is not copied (the engine builds an index it
/// does not find), and a vector index with any damage is not copied at all, because a graph with
/// missing nodes cannot be told from a whole one. The history of changes is never copied: it points to
/// states of the original file.
/// </remarks>
internal sealed class DatabaseCopier(TekiumDb destination, WriteTransaction transaction, bool salvage, CancellationToken cancellationToken)
    : IDatabaseVisitor
{
    private readonly HashSet<string> _damagedTables = new(StringComparer.Ordinal);
    private readonly List<string> _dropped = [];
    private CatalogEntryKind _kind;
    private byte[] _key = [];
    private byte[] _value = [];
    private PageNumber[] _roots = [];
    private BTreeBuilder? _builder;
    private bool _damaged;
    private bool _skip;
    private long _entriesOfEntry;

    public long Entries { get; private set; }

    public IReadOnlyList<string> Dropped => _dropped;

    public ValueTask BeginAsync(CatalogEntryKind kind, byte[] key, byte[] value, int trees)
    {
        _kind = kind;
        _key = key;
        _value = value;
        _roots = new PageNumber[trees];
        _damaged = false;
        _entriesOfEntry = 0;

        // An index over a table that lost rows would point at rows that are gone, or miss ones that are there.
        _skip = kind == CatalogEntryKind.History || (kind == CatalogEntryKind.Index && _damagedTables.Contains(TableOf(key)));
        return default;
    }

    public ValueTask BeginTreeAsync(int tree)
    {
        _builder = _skip ? null : new BTreeBuilder(destination.Tree, transaction, destination.BulkSpillPages);
        return default;
    }

    public ValueTask EntryAsync(byte[] key, byte[] value)
    {
        if (_builder is not { } builder)
        {
            return default;
        }

        builder.Add(key, value);
        Entries++;
        _entriesOfEntry++;
        return builder.ShouldSpill ? builder.SpillAsync(cancellationToken) : default;
    }

    public ValueTask EndTreeAsync(int tree, TkTreeSummary summary)
    {
        _damaged |= summary.DamagedPages > 0;
        _roots[tree] = _builder?.Complete() ?? PageNumber.None;
        _builder = null;
        return default;
    }

    public async ValueTask EndAsync()
    {
        switch (_kind)
        {
            case CatalogEntryKind.Table:
                if (_damaged)
                {
                    _damagedTables.Add(Text(_key.AsSpan(1)));
                }

                var info = new byte[TableInfo.EncodedSize];
                new TableInfo(_roots[0], TableInfo.Decode(_value).SchemaVersion).Encode(info);
                await PutAsync(info).ConfigureAwait(false);
                break;

            case CatalogEntryKind.Index:
                if (_skip || _damaged)
                {
                    await LeaveOutAsync($"index {TableOf(_key)}.{Text(_key.AsSpan(_key.AsSpan(1).IndexOf((byte)0) + 2))}").ConfigureAwait(false);
                    break;
                }

                await PutAsync(CatalogStore.EncodeIndex(_roots[0], CatalogStore.IndexDefinition(_value))).ConfigureAwait(false);
                break;

            case CatalogEntryKind.VectorIndex:
                if (_damaged)
                {
                    await LeaveOutAsync($"vector index {Text(_key.AsSpan(1))}").ConfigureAwait(false);
                    break;
                }

                var entry = _value.ToArray();
                CatalogStore.WriteVectorRoots(entry, _roots);
                await PutAsync(entry).ConfigureAwait(false);
                break;

            case CatalogEntryKind.Schema or CatalogEntryKind.Constraints or CatalogEntryKind.Referrers:
                await PutAsync(_value).ConfigureAwait(false);
                break;

            case CatalogEntryKind.Unknown when !salvage:
                // It may point to trees this build cannot follow: copying it would leave them behind.
                throw new InvalidDataException("The catalog holds an entry this build does not know: the database was not copied.");

            default:
                break;
        }
    }

    private static string TableOf(byte[] indexKey)
    {
        var separator = indexKey.AsSpan(1).IndexOf((byte)0);
        return Text(separator < 0 ? indexKey.AsSpan(1) : indexKey.AsSpan(1, separator));
    }

    private static string Text(ReadOnlySpan<byte> utf8) => Encoding.UTF8.GetString(utf8);

    private async ValueTask PutAsync(byte[] value) =>
        await destination.Tree.PutAsync(transaction, _key, value, cancellationToken).ConfigureAwait(false);

    /// <summary>Gives back the pages of what was built for the entry, and notes that it is not in the copy.</summary>
    private async ValueTask LeaveOutAsync(string what)
    {
        foreach (var root in _roots)
        {
            await BTree.DropAsync(transaction, root, cancellationToken).ConfigureAwait(false);
        }

        // What was built and thrown away is not in the copy.
        Entries -= _entriesOfEntry;
        _dropped.Add(what);
    }
}
