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
                free = await FreeListCheck.RunAsync(pager, pager.PageSize, current, walker.Visited, file.Findings, cancellationToken).ConfigureAwait(false);
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
}
