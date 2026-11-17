// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Maintenance;

/// <summary>Checks the list of free pages of a state against the pages that state uses.</summary>
internal static class FreeListCheck
{
    /// <summary>Reads the free-list and compares it with the pages in use. Returns how many pages are free.</summary>
    public static async ValueTask<long> RunAsync(
        IPageSource source, PageSize pageSize, Snapshot current, HashSet<uint> inUse, List<TkFinding> findings, CancellationToken cancellationToken)
    {
        FreeList list;
        var pages = new LastPage(source);
        try
        {
            list = await FreeList.LoadAsync(pages, pageSize, current.FreeList, cancellationToken).ConfigureAwait(false);
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
    private sealed class LastPage(IPageSource pager) : IPageSource
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
