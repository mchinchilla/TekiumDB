// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Maintenance;
using Tekium.Embedded.Storage;

namespace Tekium.Embedded;

/// <summary>How <see cref="TkMaintenance"/> reads the file it copies and writes the copy.</summary>
public sealed record TkCopyOptions
{
    /// <summary>Gets the password of the file to copy, if it is encrypted.</summary>
    public ReadOnlyMemory<char> Password { get; init; }

    /// <summary>
    /// Gets the password of the copy. <see langword="null"/> keeps the one of the original; an empty one
    /// leaves the copy unencrypted. Either way the copy gets a file key of its own.
    /// </summary>
    public ReadOnlyMemory<char>? NewPassword { get; init; }
}

/// <summary>What a copy made by <see cref="TkMaintenance"/> holds, and what it could not take along.</summary>
public sealed class TkCopyReport
{
    /// <summary>Gets the length of the original file, in bytes.</summary>
    public long SourceBytes { get; init; }

    /// <summary>Gets the size of the copy, in bytes: its pages. The file may be a little longer.</summary>
    public long DestinationBytes { get; init; }

    /// <summary>Gets the number of entries copied: rows, index entries and whatever else the trees of the database hold.</summary>
    public long EntriesCopied { get; init; }

    /// <summary>
    /// Gets what was left out of the copy because part of it was damaged: indexes (the next write to
    /// their table builds them again) and vector indexes (which have to be filled again).
    /// </summary>
    public IReadOnlyList<string> Dropped { get; init; } = [];

    /// <summary>Gets what was wrong with the original. Empty after a compaction, which refuses a damaged file.</summary>
    public IReadOnlyList<TkFinding> Findings { get; init; } = [];
}

/// <summary>
/// Maintenance that works on a closed database file and writes a new one next to it. The original is
/// never changed.
/// </summary>
public static class TkMaintenance
{
    /// <summary>
    /// Writes a copy of the database without the space its history of changes left behind: every tree
    /// rebuilt with full pages, and nothing free. The retained history of changes is not copied.
    /// </summary>
    /// <param name="source">The file to copy. It must not be open for writing.</param>
    /// <param name="destination">The file to create. It must not exist.</param>
    /// <param name="options">Passwords, if any.</param>
    /// <param name="cancellationToken">Cancels the copy. The destination is removed.</param>
    /// <returns>What was copied.</returns>
    /// <exception cref="InvalidDataException">The original is damaged: use <see cref="RepairAsync(string, string, TkCopyOptions?, CancellationToken)"/>.</exception>
    public static ValueTask<TkCopyReport> CompactAsync(
        string source, string destination, TkCopyOptions? options = null, CancellationToken cancellationToken = default) =>
        CopyAsync(source, destination, options, salvage: false, cancellationToken);

    /// <summary>
    /// Writes a copy of a damaged database with everything that can still be read. Rows on damaged pages
    /// are lost; an index that was damaged, or whose table was, is left out and built again by the next
    /// write to that table. The report says what was wrong and what was left out.
    /// </summary>
    /// <param name="source">The damaged file.</param>
    /// <param name="destination">The file to create. It must not exist.</param>
    /// <param name="options">Passwords, if any.</param>
    /// <param name="cancellationToken">Cancels the copy. The destination is removed.</param>
    /// <returns>What was copied and what was not.</returns>
    /// <exception cref="InvalidDataException">Nothing of the original can be reached: not a database, wrong password, or no valid record of where it is.</exception>
    public static ValueTask<TkCopyReport> RepairAsync(
        string source, string destination, TkCopyOptions? options = null, CancellationToken cancellationToken = default) =>
        CopyAsync(source, destination, options, salvage: true, cancellationToken);

    internal static async ValueTask<TkCopyReport> CopyAsync(
        Func<IStorageDevice> source,
        Func<DbOptions, IStorageDevice> destination,
        TkCopyOptions? options,
        bool salvage,
        CancellationToken cancellationToken)
    {
        options ??= new TkCopyOptions();
        var file = await OpenedFile.OpenAsync(source, options.Password, cancellationToken).ConfigureAwait(false);
        await using (file.ConfigureAwait(false))
        {
            if (file.Pager is not { } pager || file.Current is not { } current || file.PageSize is not { } pageSize)
            {
                // Nothing to walk: the header said why.
                throw new InvalidDataException(file.Findings.Count > 0 ? file.Findings[^1].Message : "The file cannot be read.");
            }

            ThrowIfDamaged(file.Findings, salvage);
            var (entries, bytes, dropped) = await CopyTreesAsync(
                pager, current.Root, file.ReadablePages, pageSize, file.IsCompressed, file.Findings, destination,
                options.NewPassword ?? options.Password, salvage, null, cancellationToken).ConfigureAwait(false);
            return new TkCopyReport
            {
                SourceBytes = file.FileLength,
                DestinationBytes = bytes,
                EntriesCopied = entries,
                Dropped = dropped,
                Findings = file.Findings,
            };
        }
    }

    /// <summary>
    /// Writes into a new database every tree reachable from <paramref name="catalogRoot"/>, read through
    /// <paramref name="source"/>. Shared by compaction, repair and the backup of an open database.
    /// </summary>
    internal static async ValueTask<(long Entries, long Bytes, IReadOnlyList<string> Dropped)> CopyTreesAsync(
        IPageSource source,
        PageNumber catalogRoot,
        long pageCount,
        PageSize pageSize,
        bool compressed,
        List<TkFinding> findings,
        Func<DbOptions, IStorageDevice> destination,
        ReadOnlyMemory<char> password,
        bool salvage,
        Func<ValueTask>? beforeCopying,
        CancellationToken cancellationToken)
    {
        var database = await TekiumDb.OpenAsync(
            destination, new DbOptions { PageSize = pageSize.Bytes, Password = password, Compression = compressed }, cancellationToken)
            .ConfigureAwait(false);
        await using (database.ConfigureAwait(false))
        {
            if (beforeCopying is not null)
            {
                await beforeCopying().ConfigureAwait(false);
            }

            var writer = await database.BeginWriteAsync(cancellationToken).ConfigureAwait(false);
            await using (writer.ConfigureAwait(false))
            {
                var copier = new DatabaseCopier(database, writer.Inner, salvage, cancellationToken);
                var walker = new TreeWalker(source, pageCount, findings, cancellationToken);
                await DatabaseWalk.TreesAsync(walker, catalogRoot, findings, [], copier).ConfigureAwait(false);
                ThrowIfDamaged(findings, salvage);
                await writer.CommitAsync(cancellationToken).ConfigureAwait(false);
                return (copier.Entries, (long)database.Pager.Current.PageCount * pageSize.Bytes, copier.Dropped);
            }
        }
    }

    private static async ValueTask<TkCopyReport> CopyAsync(
        string source, string destination, TkCopyOptions? options, bool salvage, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        var created = false;
        try
        {
            return await CopyAsync(
                () => FileStorageDevice.Open(source, FileMode.Open),
                _ =>
                {
                    var device = FileStorageDevice.Open(destination, FileMode.CreateNew);
                    created = true;
                    return device;
                },
                options,
                salvage,
                cancellationToken).ConfigureAwait(false);
        }
        catch when (created)
        {
            // A copy that did not finish is not left behind to be mistaken for one that did.
            File.Delete(destination);
            throw;
        }
    }

    private static void ThrowIfDamaged(List<TkFinding> findings, bool salvage)
    {
        if (!salvage && findings.Find(static finding => finding.Severity == TkSeverity.Error) is { } damage)
        {
            throw new InvalidDataException($"The database is damaged and was not copied: {damage.Message} Repair it instead.");
        }
    }
}
