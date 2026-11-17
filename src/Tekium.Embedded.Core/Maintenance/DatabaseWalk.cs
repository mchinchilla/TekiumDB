// Copyright (c) Tekium. Licensed under the MIT License.

using System.Text;
using Tekium.Embedded.Catalog;
using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Maintenance;

/// <summary>What a catalog entry is, for a tool that goes through all of them.</summary>
internal enum CatalogEntryKind
{
    Table,
    Schema,
    Index,
    Constraints,
    Referrers,
    History,
    VectorIndex,
    View,
    Procedure,
    Unknown,
}

/// <summary>Something that receives the entries of a database as a walk reads them.</summary>
internal interface IDatabaseVisitor
{
    /// <summary>A catalog entry is about to be followed. <paramref name="trees"/> is how many trees hang from it.</summary>
    ValueTask BeginAsync(CatalogEntryKind kind, byte[] key, byte[] value, int trees);

    /// <summary>The walk of tree number <paramref name="tree"/> of the entry starts.</summary>
    ValueTask BeginTreeAsync(int tree);

    /// <summary>An entry of the tree being walked.</summary>
    ValueTask EntryAsync(byte[] key, byte[] value);

    /// <summary>The walk of a tree ended; <paramref name="summary"/> says whether any of it was damaged.</summary>
    ValueTask EndTreeAsync(int tree, TkTreeSummary summary);

    /// <summary>Every tree of the catalog entry was walked.</summary>
    ValueTask EndAsync();
}

/// <summary>Goes through a whole database: the catalog, and from each of its entries the trees it points to.</summary>
internal static class DatabaseWalk
{
    public static async ValueTask TreesAsync(
        TreeWalker walker, PageNumber catalogRoot, List<TkFinding> findings, List<TkTreeSummary> trees, IDatabaseVisitor? visitor)
    {
        var catalog = new List<(byte[] Key, byte[] Value)>();
        trees.Add(await walker.WalkAsync(catalogRoot, "catalog", (key, value) =>
        {
            catalog.Add((key, value));
            return default;
        }).ConfigureAwait(false));

        foreach (var (key, value) in catalog)
        {
            var (kind, name, roots) = Describe(key, value, findings);
            if (visitor is not null)
            {
                await visitor.BeginAsync(kind, key, value, roots.Length).ConfigureAwait(false);
            }

            for (var i = 0; i < roots.Length; i++)
            {
                if (visitor is not null)
                {
                    await visitor.BeginTreeAsync(i).ConfigureAwait(false);
                }

                var summary = await walker.WalkAsync(
                    roots[i], roots.Length == 1 ? name : $"{name}, tree {i + 1}", visitor is null ? null : visitor.EntryAsync).ConfigureAwait(false);
                trees.Add(summary);
                if (visitor is not null)
                {
                    await visitor.EndTreeAsync(i, summary).ConfigureAwait(false);
                }
            }

            if (visitor is not null)
            {
                await visitor.EndAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>What a catalog entry is, what to call it, and the trees that hang from it.</summary>
    private static (CatalogEntryKind Kind, string Name, PageNumber[] Roots) Describe(byte[] key, byte[] value, List<TkFinding> findings)
    {
        if (CatalogStore.IsTableKey(key) && value.Length == TableInfo.EncodedSize)
        {
            return (CatalogEntryKind.Table, $"table {Text(key.AsSpan(1))}", [TableInfo.Decode(value).Root]);
        }

        if (CatalogStore.IsIndexKey(key) && value.Length >= sizeof(uint) + 1)
        {
            var separator = key.AsSpan(1).IndexOf((byte)0);
            var name = separator < 0 ? Text(key.AsSpan(1)) : $"{Text(key.AsSpan(1, separator))}.{Text(key.AsSpan(separator + 2))}";
            return (CatalogEntryKind.Index, $"index {name}", [CatalogStore.IndexRoot(value)]);
        }

        if (CatalogStore.IsVectorIndexKey(key) && CatalogStore.TryReadVectorRoots(value, out var roots))
        {
            return (CatalogEntryKind.VectorIndex, $"vector index {Text(key.AsSpan(1))}", roots);
        }

        var kind = key.Length == 0 ? CatalogEntryKind.Unknown : key[0] switch
        {
            2 => CatalogEntryKind.Schema,
            4 => CatalogEntryKind.Constraints,
            5 => CatalogEntryKind.Referrers,
            6 => CatalogEntryKind.History,
            8 => CatalogEntryKind.View,
            9 => CatalogEntryKind.Procedure,
            _ => CatalogEntryKind.Unknown,
        };
        if (kind == CatalogEntryKind.Unknown)
        {
            findings.Add(new TkFinding(
                TkSeverity.Warning, TkFindingKind.UnknownCatalogEntry, null, "catalog",
                $"The catalog holds an entry this build does not know (it starts with {(key.Length == 0 ? "nothing" : key[0].ToString(System.Globalization.CultureInfo.InvariantCulture))}). If trees hang from it, they were not checked."));
        }

        return (kind, string.Empty, []);
    }

    private static string Text(ReadOnlySpan<byte> utf8) => Encoding.UTF8.GetString(utf8);
}
