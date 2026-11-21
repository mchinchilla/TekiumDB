// Copyright (c) Tekium. Licensed under the MIT License.

using TekiumDB.Security;
using TekiumDB.Storage;

namespace TekiumDB.Maintenance;

/// <summary>
/// A database file read the way the tools read it: as far as it can be, noting what is wrong instead of
/// refusing. The engine opens a file only if everything it looks at is right; this does not.
/// </summary>
internal sealed class OpenedFile : IAsyncDisposable
{
    private readonly IStorageDevice _device;

    private OpenedFile(IStorageDevice device) => _device = device;

    public List<TkFinding> Findings { get; } = [];

    public long FileLength { get; private set; }

    public int FormatVersion { get; private set; }

    public PageSize? PageSize { get; private set; }

    public bool IsEncrypted { get; private set; }

    public bool IsCompressed { get; private set; }

    public List<TkRootSlot> Slots { get; } = [];

    public int CurrentSlot { get; private set; } = -1;

    /// <summary>Gets the state the database would open with, if one was found.</summary>
    public Snapshot? Current { get; private set; }

    /// <summary>Gets the pager to read pages with, if the file got that far. It checks every page it reads.</summary>
    public Pager? Pager { get; private set; }

    /// <summary>Gets the pages the current state may reference: no more than the file holds.</summary>
    public long ReadablePages { get; private set; }

    /// <summary>Reads what can be read of the file on the device <paramref name="open"/> returns, which the result then owns.</summary>
    public static async ValueTask<OpenedFile> OpenAsync(
        Func<IStorageDevice> open, ReadOnlyMemory<char> password, CancellationToken cancellationToken)
    {
        var file = new OpenedFile(open());
        try
        {
            await file.ReadAsync(password, cancellationToken).ConfigureAwait(false);
            return file;
        }
        catch
        {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Pager is not null)
        {
            await Pager.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            await _device.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask ReadAsync(ReadOnlyMemory<char> password, CancellationToken cancellationToken)
    {
        FileLength = _device.Length;
        if (FileLength < FileHeader.FixedSize)
        {
            Add(TkSeverity.Error, TkFindingKind.NotADatabase, "The file is too short to be a TekiumDB database.");
            return;
        }

        // The header is written twice, at the start of each of the first two pages. The engine reads the
        // first; here the second is a way in when the first is damaged.
        var first = new byte[FileHeader.FixedSize];
        await _device.ReadAsync(0, first, cancellationToken).ConfigureAwait(false);
        var header = first;
        var size = TryReadPageSize(first, out var firstProblem);
        byte[]? second = null;
        if (size is null)
        {
            foreach (var candidate in TekiumDB.Storage.PageSize.Supported.ToArray())
            {
                if (FileLength < candidate.Bytes + FileHeader.FixedSize)
                {
                    continue;
                }

                var copy = new byte[FileHeader.FixedSize];
                await _device.ReadAsync(candidate.Bytes, copy, cancellationToken).ConfigureAwait(false);
                if (TryReadPageSize(copy, out _) == candidate)
                {
                    size = candidate;
                    second = copy;
                    header = copy;
                    break;
                }
            }

            if (size is null)
            {
                Add(TkSeverity.Error, firstProblem.Kind, firstProblem.Message);
                return;
            }

            Findings.Add(new TkFinding(
                TkSeverity.Error, TkFindingKind.HeaderDamaged, 0, "header",
                $"The first copy of the header is damaged ({firstProblem.Message}) The second copy was used; the engine reads the first and will not open this file."));
        }

        var pageSize = size.Value;
        PageSize = pageSize;
        FormatVersion = FileHeader.FormatVersion;
        IsEncrypted = FileHeader.IsEncrypted(header);
        IsCompressed = FileHeader.IsCompressed(header);
        if (FileLength < pageSize.Bytes * (long)FileHeader.MetaPages)
        {
            Add(TkSeverity.Error, TkFindingKind.Truncated, "The file ends inside its header.");
            return;
        }

        if (second is null)
        {
            second = new byte[FileHeader.FixedSize];
            await _device.ReadAsync(pageSize.Bytes, second, cancellationToken).ConfigureAwait(false);
            if (TryReadPageSize(second, out var secondProblem) != pageSize)
            {
                Findings.Add(new TkFinding(
                    TkSeverity.Warning, TkFindingKind.HeaderDamaged, 1, "header",
                    $"The second copy of the header is damaged ({secondProblem.Message}) The database still opens with the first."));
            }
        }

        IPageProtector protector = ChecksumPageProtector.Instance;
        if (IsEncrypted)
        {
            using var bytes = PasswordBytes.From(password.Span);
            if (bytes is null)
            {
                Add(TkSeverity.Info, TkFindingKind.PasswordRequired, "The database is encrypted: without its password, only the header was read and nothing was checked.");
                return;
            }

            var unlocked = await Task.Run(() => Storage.Pager.TryUnlock(first, second, bytes), cancellationToken).ConfigureAwait(false);
            if (unlocked is null)
            {
                Add(TkSeverity.Error, TkFindingKind.WrongPassword, "The password does not open the database, or the key data in its header is damaged.");
                return;
            }

            protector = unlocked;
        }

        try
        {
            for (var i = 0; i < FileHeader.SlotCount; i++)
            {
                var slot = new byte[FileHeader.SlotSize];
                await _device.ReadAsync(FileHeader.SlotOffset(i, pageSize), slot, cancellationToken).ConfigureAwait(false);
                if (FileHeader.TryDecodeSlot(slot, protector, out var state))
                {
                    Slots.Add(new TkRootSlot(i, true, state.TransactionId.Value, state.PageCount, state.Root.Value));
                    if (Current is null || state.TransactionId.Value > Current.TransactionId.Value)
                    {
                        Current = state;
                        CurrentSlot = i;
                    }
                }
                else
                {
                    Slots.Add(new TkRootSlot(i, false, 0, 0, 0));
                }
            }

            if (Current is null)
            {
                Add(TkSeverity.Error, TkFindingKind.NoValidRootSlot, "Neither root slot passes its check: nothing says where the database is.");
                return;
            }

            // Creating a database writes one slot and its first commit writes the other. From then on a
            // slot that fails its check is the one the last commit was writing, or damage: either way a
            // state is gone. Before any commit, the second slot was simply never written.
            foreach (var slot in Slots)
            {
                if (!slot.IsValid && Current.TransactionId.Value > 0)
                {
                    Findings.Add(new TkFinding(
                        TkSeverity.Warning, TkFindingKind.RootSlotDamaged, slot.Index, "header",
                        $"Root slot {slot.Index} does not pass its check. The database opens with slot {CurrentSlot}, as transaction {Current.TransactionId.Value} left it; a later commit may have been lost."));
                }
            }

            ReadablePages = Math.Min(Current.PageCount, FileLength / pageSize.Bytes);
            if ((long)Current.PageCount * pageSize.Bytes > FileLength)
            {
                Add(
                    TkSeverity.Error, TkFindingKind.Truncated,
                    $"The database has {Current.PageCount} pages but the file holds {FileLength / pageSize.Bytes}: it was cut short.");
            }

            Pager = Storage.Pager.ForInspection(_device, pageSize, protector, IsCompressed, Current, CurrentSlot);
        }
        finally
        {
            if (Pager is null)
            {
                protector.Dispose();
            }
        }
    }

    private static PageSize? TryReadPageSize(byte[] header, out (TkFindingKind Kind, string Message) problem)
    {
        var found = FileHeader.Check(header, out var size, out var message);
        problem = (
            found switch
            {
                FileHeader.Problem.NotADatabase => TkFindingKind.NotADatabase,
                FileHeader.Problem.UnsupportedFormat => TkFindingKind.UnsupportedFormat,
                _ => TkFindingKind.HeaderDamaged,
            },
            message);
        return found == FileHeader.Problem.None ? size : null;
    }

    private void Add(TkSeverity severity, TkFindingKind kind, string message) =>
        Findings.Add(new TkFinding(severity, kind, null, null, message));
}
