// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;

namespace Tekium.Embedded.Storage;

/// <summary>
/// Pages nothing reaches any more, grouped by the transaction that freed them. A page freed by
/// transaction F may still be part of every older state, so it is handed out again only once no
/// reader and no fallback root slot can need those states: the oldest batches are consumed first,
/// and only up to a horizon the writer computes when it starts.
/// </summary>
/// <remarks>
/// On disk each batch is a trunk page (count u32, then page numbers u32) that is never modified.
/// A chain of directory pages (next u32, entry count u32, then entries of trunk u32, first unused
/// index u32, freeing transaction u64) lists the trunks, oldest first. A commit that changes the
/// free-list writes a new directory and, if it freed anything, new trunks; consuming pages only
/// moves an index in the directory. The pages of the replaced directory and of exhausted trunks
/// are themselves freed by that commit. The whole list is kept in memory, so taking a page is synchronous.
/// </remarks>
internal sealed class FreeList
{
    private const int TrunkHeader = 4;
    private const int DirectoryHeader = 8;
    private const int DirectoryEntrySize = 16;

    private readonly int _pagesPerTrunk;
    private readonly int _entriesPerDirectory;
    private List<Batch> _batches;
    private List<PageNumber> _directory;

    private FreeList(PageSize pageSize, List<Batch> batches, List<PageNumber> directory)
    {
        var payload = PageLayout.PayloadSize(pageSize);
        _pagesPerTrunk = (payload - TrunkHeader) / sizeof(uint);
        _entriesPerDirectory = (payload - DirectoryHeader) / DirectoryEntrySize;
        _batches = batches;
        _directory = directory;
    }

    /// <summary>Gets the number of free pages, whether or not they can be reused yet.</summary>
    public int Count
    {
        get
        {
            var count = 0;
            foreach (var batch in _batches)
            {
                count += batch.Pages.Length - batch.Start;
            }

            return count;
        }
    }

    public static FreeList Empty(PageSize pageSize) => new(pageSize, [], []);

    public static async ValueTask<FreeList> LoadAsync(
        IPageSource pages, PageSize pageSize, PageNumber head, CancellationToken cancellationToken)
    {
        var list = Empty(pageSize);
        var next = head;
        while (!next.IsNone)
        {
            if (list._directory.Contains(next))
            {
                throw new InvalidDataException("The free-list directory loops back on itself: the file is corrupt.");
            }

            list._directory.Add(next);
            using var directory = await pages.PinAsync(next, cancellationToken).ConfigureAwait(false);
            if (directory.Type != PageType.FreeListDirectory)
            {
                throw new InvalidDataException($"Page {next.Value} is not a free-list directory: the file is corrupt.");
            }

            var entries = (int)BinaryPrimitives.ReadUInt32LittleEndian(directory.Span[4..]);
            if (entries > list._entriesPerDirectory)
            {
                throw new InvalidDataException($"Free-list directory page {next.Value} is corrupt.");
            }

            for (var i = 0; i < entries; i++)
            {
                var entry = directory.Span.Slice(DirectoryHeader + (i * DirectoryEntrySize), DirectoryEntrySize);
                var trunk = new PageNumber(BinaryPrimitives.ReadUInt32LittleEndian(entry));
                var start = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
                var freedBy = new TransactionId(BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]));
                list._batches.Add(await list.LoadBatchAsync(pages, trunk, start, freedBy, cancellationToken)
                    .ConfigureAwait(false));
            }

            next = new PageNumber(BinaryPrimitives.ReadUInt32LittleEndian(directory.Span));
        }

        return list;
    }

    /// <summary>Where a transaction that has taken nothing yet starts taking from.</summary>
    public Cursor Begin() => new(0, _batches.Count > 0 ? _batches[0].Start : 0);

    /// <summary>
    /// Takes the next reusable page, if the oldest remaining batch was freed at or before
    /// <paramref name="horizon"/>. Nothing changes until the transaction commits: the cursor is the only state.
    /// </summary>
    public bool TryTake(long horizon, ref Cursor cursor, out PageNumber page)
    {
        while (cursor.Batch < _batches.Count)
        {
            var batch = _batches[cursor.Batch];
            if ((long)batch.FreedBy.Value > horizon)
            {
                break;
            }

            if (cursor.Offset < batch.Pages.Length)
            {
                page = new PageNumber(batch.Pages[cursor.Offset]);
                cursor = new Cursor(cursor.Batch, cursor.Offset + 1);
                return true;
            }

            var following = cursor.Batch + 1;
            cursor = new Cursor(following, following < _batches.Count ? _batches[following].Start : 0);
        }

        page = default;
        return false;
    }

    /// <summary>
    /// Builds the free-list a commit will publish: writes the new trunk and directory pages into the
    /// transaction and returns the state to <see cref="Apply"/> once the commit is durable.
    /// Returns <see langword="null"/> when the transaction neither freed nor took anything.
    /// </summary>
    public Update? Prepare(WriteTransaction transaction)
    {
        var freed = transaction.Freed;
        if (freed.Count == 0 && transaction.Cursor == Begin())
        {
            return null;
        }

        // The directory is rewritten, so its current pages become free, as does every exhausted trunk.
        foreach (var page in _directory)
        {
            freed.Add(page.Value);
        }

        // The new pages come from the free-list too, which can exhaust another trunk and so free one
        // more page. Allocate one at a time until what is needed stops growing; it converges because
        // each allocation frees at most one page while each trunk holds thousands.
        var metadata = new List<PageNumber>();
        var exhausted = 0;
        int trunks;
        while (true)
        {
            var nowExhausted = ExhaustedBatches(transaction.Cursor);
            for (; exhausted < nowExhausted; exhausted++)
            {
                freed.Add(_batches[exhausted].Trunk.Value);
            }

            trunks = (freed.Count + _pagesPerTrunk - 1) / _pagesPerTrunk;
            var entries = _batches.Count - exhausted + trunks;
            var directories = (entries + _entriesPerDirectory - 1) / _entriesPerDirectory;
            if (metadata.Count >= trunks + directories)
            {
                break;
            }

            metadata.Add(transaction.AllocateNumber());
        }

        freed.Sort();
        var batches = new List<Batch>(_batches.Count - exhausted + trunks);
        for (var i = exhausted; i < _batches.Count; i++)
        {
            var batch = _batches[i];
            batches.Add(i == transaction.Cursor.Batch ? batch with { Start = transaction.Cursor.Offset } : batch);
        }

        for (var i = 0; i < trunks; i++)
        {
            var pages = freed.GetRange(i * _pagesPerTrunk, Math.Min(_pagesPerTrunk, freed.Count - (i * _pagesPerTrunk)))
                .ToArray();
            var payload = transaction.CreatePage(metadata[i], PageType.FreeListTrunk).Span;
            BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)pages.Length);
            for (var p = 0; p < pages.Length; p++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(payload[(TrunkHeader + (p * sizeof(uint)))..], pages[p]);
            }

            batches.Add(new Batch(transaction.Id, metadata[i], pages, 0));
        }

        // Every remaining allocated page becomes a directory page, even if the last one ends up empty.
        var directory = metadata.GetRange(trunks, metadata.Count - trunks);
        for (var d = 0; d < directory.Count; d++)
        {
            var payload = transaction.CreatePage(directory[d], PageType.FreeListDirectory).Span;
            var first = d * _entriesPerDirectory;
            var count = Math.Clamp(batches.Count - first, 0, _entriesPerDirectory);
            BinaryPrimitives.WriteUInt32LittleEndian(payload, d + 1 < directory.Count ? directory[d + 1].Value : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], (uint)count);
            for (var i = 0; i < count; i++)
            {
                var batch = batches[first + i];
                var entry = payload.Slice(DirectoryHeader + (i * DirectoryEntrySize), DirectoryEntrySize);
                BinaryPrimitives.WriteUInt32LittleEndian(entry, batch.Trunk.Value);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], (uint)batch.Start);
                BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], batch.FreedBy.Value);
            }
        }

        return new Update(batches, directory);
    }

    public void Apply(Update update)
    {
        _batches = update.Batches;
        _directory = update.Directory;
    }

    private int ExhaustedBatches(Cursor cursor) =>
        cursor.Batch < _batches.Count && cursor.Offset == _batches[cursor.Batch].Pages.Length
            ? cursor.Batch + 1
            : cursor.Batch;

    private async ValueTask<Batch> LoadBatchAsync(
        IPageSource pages, PageNumber trunk, int start, TransactionId freedBy, CancellationToken cancellationToken)
    {
        using var page = await pages.PinAsync(trunk, cancellationToken).ConfigureAwait(false);
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(page.Span);
        if (page.Type != PageType.FreeListTrunk || count > _pagesPerTrunk || start > count)
        {
            throw new InvalidDataException($"Free-list trunk page {trunk.Value} is corrupt.");
        }

        var numbers = new uint[count];
        for (var i = 0; i < count; i++)
        {
            numbers[i] = BinaryPrimitives.ReadUInt32LittleEndian(page.Span[(TrunkHeader + (i * sizeof(uint)))..]);
        }

        return new Batch(freedBy, trunk, numbers, start);
    }

    /// <summary>Position of a transaction in the list: the batch it is taking from and the next index in it.</summary>
    internal readonly record struct Cursor(int Batch, int Offset);

    /// <summary>The state a commit publishes.</summary>
    internal sealed record Update(List<Batch> Batches, List<PageNumber> Directory)
    {
        public PageNumber Head => Directory.Count > 0 ? Directory[0] : PageNumber.None;
    }

    /// <summary>The pages one transaction freed; those before <paramref name="Start"/> were already reused.</summary>
    internal sealed record Batch(TransactionId FreedBy, PageNumber Trunk, uint[] Pages, int Start);
}
