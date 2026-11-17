// Copyright (c) Tekium. Licensed under the MIT License.

using System.Text;
using Tekium.Embedded.Catalog;
using Tekium.Embedded.Maintenance;
using Tekium.Embedded.Storage;

namespace Tekium.Embedded;

/// <summary>
/// Examines a database file without opening it: the header, the two records of where the database is,
/// every page in use and the list of free ones. Whatever is wrong is reported, with the page and what it
/// belongs to; nothing is changed. It reads files the engine refuses to open.
/// </summary>
public static class TkInspector
{
    /// <summary>Examines a database file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="password">The password, if the file is encrypted. Without it only the header is read.</param>
    /// <param name="cancellationToken">Cancels the inspection.</param>
    /// <returns>What was found.</returns>
    /// <exception cref="IOException">The file cannot be read at all.</exception>
    public static ValueTask<TkInspection> InspectAsync(
        string path, ReadOnlyMemory<char> password = default, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return InspectAsync(() => FileStorageDevice.Open(path, FileMode.Open), password, cancellationToken);
    }

    /// <summary>Examines a database on the device <paramref name="open"/> returns, which is closed afterwards.</summary>
    internal static async ValueTask<TkInspection> InspectAsync(
        Func<IStorageDevice> open, ReadOnlyMemory<char> password, CancellationToken cancellationToken)
    {
        var file = await OpenedFile.OpenAsync(open, password, cancellationToken).ConfigureAwait(false);
        await using (file.ConfigureAwait(false))
        {
            var trees = new List<TkTreeSummary>();
            long inUse = 0;
            long free = 0;
            if (file.Pager is { } pager && file.Current is { } current)
            {
                var walker = new TreeWalker(pager, file.ReadablePages, file.Findings, cancellationToken);
                await DatabaseWalk.TreesAsync(walker, current.Root, file.Findings, trees, null).ConfigureAwait(false);
                inUse = walker.Visited.Count;
                free = await CheckFreeListAsync(pager, current, walker.Visited, file.Findings, cancellationToken).ConfigureAwait(false);
            }

            return new TkInspection
            {
                FileLength = file.FileLength,
                FormatVersion = file.FormatVersion,
                PageSize = file.PageSize?.Bytes ?? 0,
                IsEncrypted = file.IsEncrypted,
                IsCompressed = file.IsCompressed,
                RootSlots = file.Slots,
                CurrentSlot = file.CurrentSlot,
                PagesChecked = file.Pager is not null,
                PagesInUse = inUse,
                PagesFree = free,
                Trees = trees,
                Findings = file.Findings,
            };
        }
    }

    /// <summary>Reads the free-list and compares it with the pages in use. Returns how many pages are free.</summary>
    private static async ValueTask<long> CheckFreeListAsync(
        Pager pager, Snapshot current, HashSet<uint> inUse, List<TkFinding> findings, CancellationToken cancellationToken)
    {
        FreeList list;
        var pages = new LastPage(pager);
        try
        {
            list = await FreeList.LoadAsync(pages, pager.PageSize, current.FreeList, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException)
        {
            findings.Add(new TkFinding(
                TkSeverity.Error, TkFindingKind.FreeListDamaged, pages.Number, "free-list",
                $"Page {pages.Number} of the free-list cannot be read: {exception.Message.TrimEnd('.')}. The data is not affected, but the database will not open."));
            return 0;
        }

        long free = 0;
        var listed = new HashSet<uint>();
        foreach (var page in list.FreePages.Concat(list.MetadataPages))
        {
            if (inUse.Contains(page) || !listed.Add(page))
            {
                findings.Add(new TkFinding(
                    TkSeverity.Error, TkFindingKind.FreePageInUse, page, "free-list",
                    $"Page {page} is listed as free and is also in use: a later write would overwrite it."));
            }
        }

        free = list.Count;
        var unaccounted = (long)current.PageCount - FileHeader.MetaPages - inUse.Count - listed.Count;
        if (unaccounted > 0 && !findings.Exists(static finding => finding.Severity == TkSeverity.Error))
        {
            findings.Add(new TkFinding(
                TkSeverity.Warning, TkFindingKind.PagesUnaccounted, null, null,
                $"{unaccounted} pages are neither in use nor listed as free: space the database will not reuse until it is compacted."));
        }

        return free;
    }

    /// <summary>Remembers the page asked for last, so that a failure can say which one it was.</summary>
    private sealed class LastPage(Pager pager) : IPageSource
    {
        public long Number { get; private set; }

        public bool TryPin(PageNumber number, out PageLease lease)
        {
            Number = number.Value;
            return pager.TryPin(number, out lease);
        }

        public ValueTask<PageLease> PinAsync(PageNumber number, CancellationToken cancellationToken)
        {
            Number = number.Value;
            return pager.PinAsync(number, cancellationToken);
        }
    }
}
