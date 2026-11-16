// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Tekium.Embedded.Security;

namespace Tekium.Embedded.Storage;

/// <summary>
/// Owns the file as an array of fixed-size pages. Committed pages are never overwritten while anything
/// can still reach them: a write transaction puts its pages where nothing lives and the commit publishes
/// them by writing a root slot.
/// </summary>
internal sealed class Pager : IPageSource, IAsyncDisposable
{
    private const long GrowthStepLimit = 64L * 1024 * 1024;
    private const int CommitBufferBytes = 1 << 20;
    private const int MaxSpareBuffers = 1024;

    /// <summary>Transaction ids reserved on disk at a time when encrypting. One header write per this many transactions.</summary>
    private const ulong ReservationBlock = 1UL << 20;

    /// <summary>Bits of the random distance a session jumps ahead of the ids used before it.</summary>
    private const int SessionGapBits = 40;

    private readonly IStorageDevice _device;
    private readonly IPageProtector _protector;
    private readonly PagerOptions _options;
    private readonly PageCache _cache;
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private readonly byte[] _recordBuffer = new byte[FileHeader.SlotSize];

    // State of the single writer, reused from one transaction to the next.
    private readonly Stack<byte[]> _spareBuffers = new();
    private readonly Dictionary<uint, byte[]> _dirty = [];
    private readonly List<uint> _freed = [];
    private readonly List<SnapshotState> _retained = [];

    // States kept readable without a reader on them: every state from the floor on, and those pinned one by one.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, long> _pins = new();
    private long _retentionFloor = long.MaxValue;
    private long _pinCount;
    private long _lastPin;
    private SnapshotState _current;
    private int _currentSlot;
    private ulong _lastIssued;
    private ulong _reservedThrough;
    private int _reservationCopy;
    private bool _sessionReserved;
    private Exception? _commitInDoubt;

    private Pager(
        IStorageDevice device,
        PageSize pageSize,
        PagerOptions options,
        IPageProtector protector,
        bool compressed,
        Snapshot current,
        int slot)
    {
        _device = device;

        // Compression goes in front of the checksum or the encryption: a page is compressed, then protected.
        _protector = compressed ? new CompressedPageProtector(protector, options.CompressionLevel, pageSize) : protector;
        _options = options;
        _cache = new PageCache(options.CachePages);
        _current = new SnapshotState(current);
        _currentSlot = slot;
        _lastIssued = current.TransactionId.Value;
        PageSize = pageSize;
        FreeList = FreeList.Empty(pageSize);
    }

    public PageSize PageSize { get; }

    /// <summary>Gets a value indicating whether pages are encrypted on disk.</summary>
    public bool IsEncrypted => _protector.Encrypts;

    /// <summary>Gets a value indicating whether pages are compressed on disk.</summary>
    public bool IsCompressed => _protector is CompressedPageProtector;

    /// <summary>Gets the latest committed state. To read from it safely, hold a <see cref="BeginRead"/>.</summary>
    public Snapshot Current => Volatile.Read(ref _current).Snapshot;

    /// <summary>Gets the root slot that holds the latest committed state. The next commit writes the other one.</summary>
    public int CurrentSlot => _currentSlot;

    /// <summary>Gets the number of free pages in the file, reusable or not yet.</summary>
    public int FreePageCount => FreeList.Count;

    /// <summary>Gets the number of pages the free-list itself occupies.</summary>
    public int FreeListPageCount => FreeList.MetadataPageCount;

    internal FreeList FreeList { get; private set; }

    /// <summary>Creates an unencrypted database.</summary>
    public static ValueTask<Pager> CreateAsync(
        IStorageDevice device, PagerOptions options, CancellationToken cancellationToken) =>
        CreateCoreAsync(device, options, null, cancellationToken);

    /// <summary>Creates a database encrypted with a key derived from <paramref name="password"/>. An empty password means no encryption.</summary>
    public static ValueTask<Pager> CreateAsync(
        IStorageDevice device, PagerOptions options, ReadOnlySpan<char> password, CancellationToken cancellationToken) =>
        CreateCoreAsync(device, options, PasswordBytes.From(password), cancellationToken);

    /// <summary>Opens an unencrypted database.</summary>
    public static ValueTask<Pager> OpenAsync(
        IStorageDevice device, PagerOptions options, CancellationToken cancellationToken) =>
        OpenCoreAsync(device, options, null, cancellationToken);

    /// <summary>Opens a database, deriving its key from <paramref name="password"/> if it is encrypted.</summary>
    public static ValueTask<Pager> OpenAsync(
        IStorageDevice device, PagerOptions options, ReadOnlySpan<char> password, CancellationToken cancellationToken) =>
        OpenCoreAsync(device, options, PasswordBytes.From(password), cancellationToken);

    /// <summary>
    /// Pins the latest committed state. Never blocks and never allocates: while the reader holds it,
    /// the writer will not reuse any page that state reaches.
    /// </summary>
    public ReadSnapshot BeginRead()
    {
        while (true)
        {
            var state = Volatile.Read(ref _current);
            state.AddReader();

            // Registered before re-checking: either the writer already saw this reader, or the state was
            // still current after registering, and then nothing it reaches has been released yet.
            if (ReferenceEquals(state, Volatile.Read(ref _current)))
            {
                return new ReadSnapshot(state);
            }

            state.RemoveReader();
        }
    }

    /// <summary>
    /// Keeps every state from this transaction on readable: no page they reach is reused, whether or not
    /// anybody is reading them. <see langword="null"/> keeps none. Called by the writer, or before the first one.
    /// </summary>
    public void SetRetentionFloor(ulong? transaction) =>
        Volatile.Write(ref _retentionFloor, transaction is { } floor ? checked((long)floor) : long.MaxValue);

    /// <summary>
    /// Keeps the state of a transaction readable until <see cref="Release"/>, however far the floor
    /// moves meanwhile. Fails if the state is already below the floor: its pages may be gone.
    /// </summary>
    public bool TryRetain(ulong transaction, out long pin)
    {
        pin = Interlocked.Increment(ref _lastPin);
        _pins[pin] = checked((long)transaction);
        Interlocked.Increment(ref _pinCount);

        // Pinned before checking: a writer that moves the floor past this state afterwards will see the pin.
        if ((long)transaction >= Volatile.Read(ref _retentionFloor))
        {
            return true;
        }

        Release(pin);
        return false;
    }

    /// <summary>Lets go of a state kept with <see cref="TryRetain"/>.</summary>
    public void Release(long pin)
    {
        if (_pins.TryRemove(pin, out _))
        {
            Interlocked.Decrement(ref _pinCount);
        }
    }

    public bool TryPin(PageNumber number, out PageLease lease) => _cache.TryPin(number, out lease);

    public ValueTask<PageLease> PinAsync(PageNumber number, CancellationToken cancellationToken) =>
        _cache.TryPin(number, out var lease)
            ? new ValueTask<PageLease>(lease)
            : LoadAsync(number, cancellationToken);

    /// <summary>Waits for the single writer slot. Dispose the transaction to release it.</summary>
    public async ValueTask<WriteTransaction> BeginWriteAsync(CancellationToken cancellationToken)
    {
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfCommitInDoubt();
            var state = _current;

            // An id is never issued twice, not even after a commit that failed halfway: its pages may
            // already be on disk, and when encrypting the id is half of the nonce that encrypted them.
            var id = checked(Math.Max(_lastIssued, state.Snapshot.TransactionId.Value) + 1);
            if (_protector.Encrypts && (!_sessionReserved || id > _reservedThrough))
            {
                id = await ReserveTransactionIdsAsync(id, cancellationToken).ConfigureAwait(false);
            }

            _lastIssued = id;

            // A page freed by transaction F belongs to every state older than F. It may be reused only when
            // (a) no reader holds a state older than F, and (b) F is not the latest commit, so that the
            // previous state stays intact on disk as the fallback of the alternate root slot.
            var horizon = checked((long)state.Snapshot.TransactionId.Value) - 1;
            for (var i = _retained.Count - 1; i >= 0; i--)
            {
                // A state that is no longer current and has no readers can never get one again.
                if (_retained[i].Readers == 0)
                {
                    _retained.RemoveAt(i);
                }
                else
                {
                    horizon = Math.Min(horizon, (long)_retained[i].Snapshot.TransactionId.Value);
                }
            }

            // The floor is read before the pins: whoever pins checks the floor afterwards, so one of the two sees the other.
            horizon = Math.Min(horizon, Volatile.Read(ref _retentionFloor));
            if (Interlocked.Read(ref _pinCount) > 0)
            {
                foreach (var pinned in _pins.Values)
                {
                    horizon = Math.Min(horizon, pinned);
                }
            }

            return new WriteTransaction(this, state.Snapshot, new TransactionId(id), horizon, _dirty, _freed);
        }
        catch
        {
            _writerGate.Release();
            throw;
        }
    }

    /// <summary>
    /// Re-wraps the file key under a new password. Only the header is rewritten, one copy and then the
    /// other: if it is interrupted in between, the database opens with either password.
    /// </summary>
    public ValueTask ChangePasswordAsync(ReadOnlySpan<char> newPassword, CancellationToken cancellationToken)
    {
        if (((_protector as CompressedPageProtector)?.Inner ?? _protector) is not AesGcmPageProtector protector)
        {
            return ValueTask.FromException(
                new InvalidOperationException("The database is not encrypted: there is no password to change."));
        }

        // The core takes ownership of the password bytes and wipes them.
        return newPassword.IsEmpty
            ? ValueTask.FromException(new ArgumentException("The new password must not be empty.", nameof(newPassword)))
            : ChangePasswordCoreAsync(protector, PasswordBytes.From(newPassword)!, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _writerGate.Dispose();
        _protector.Dispose();
        await _device.DisposeAsync().ConfigureAwait(false);
    }

    internal byte[] RentBuffer() =>
        _spareBuffers.TryPop(out var buffer) ? buffer : GC.AllocateUninitializedArray<byte>(PageSize.Bytes);

    internal void ReturnBuffer(byte[] buffer)
    {
        if (_spareBuffers.Count < MaxSpareBuffers)
        {
            _spareBuffers.Push(buffer);
        }
    }

    internal void EndWrite(WriteTransaction transaction)
    {
        foreach (var page in transaction.Dirty.Values)
        {
            ReturnBuffer(page);
        }

        _dirty.Clear();
        _freed.Clear();
        _writerGate.Release();
    }

    /// <summary>
    /// The commit: new pages, flush, root slot, flush. The second flush is the durability point.
    /// </summary>
    /// <remarks>
    /// A failure or a cancellation before the root slot is touched leaves the previous state intact on
    /// disk and in memory, and the writer goes on. Once the root slot write starts there is no way back
    /// and no cancelling: if it or its flush fails, nobody knows which state the disk holds. The new one
    /// may be there, reaching pages the next transaction would be free to overwrite, so the writer stops
    /// for good and the database has to be reopened. Readers keep the previous state, which is intact.
    /// </remarks>
    internal async ValueTask CommitAsync(WriteTransaction transaction, CancellationToken cancellationToken)
    {
        var state = _current;
        var current = state.Snapshot;
        var freeList = FreeList.Prepare(transaction);
        var dirty = transaction.Dirty;
        if (dirty.Count == 0 && freeList is null && transaction.Root == current.Root)
        {
            return;
        }

        var next = new Snapshot(
            transaction.Root,
            transaction.PageCount,
            transaction.Id,
            freeList is null ? current.FreeList : freeList.Head);

        if (dirty.Count > 0)
        {
            await WritePagesAsync(dirty, next.PageCount, cancellationToken).ConfigureAwait(false);
            await _device.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        // Last point where giving up costs nothing.
        cancellationToken.ThrowIfCancellationRequested();

        // The slot that does not hold the current state, whatever the transaction id is.
        var slot = (_currentSlot + 1) % FileHeader.SlotCount;
        FileHeader.EncodeSlot(_recordBuffer, next, _protector);
        try
        {
            await _device.WriteAsync(
                FileHeader.SlotOffset(slot, PageSize), _recordBuffer.AsMemory(0, FileHeader.SlotSize), CancellationToken.None)
                .ConfigureAwait(false);
            await _device.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            _commitInDoubt = failure;
            throw new IOException(
                "The commit failed while its root slot was being made durable, so it may or may not have happened. " +
                "The database accepts no more writes: reopen it to find out which state survived.",
                failure);
        }

        // Durable. From here on nothing can fail: publish to readers.
        _currentSlot = slot;
        foreach (var (number, page) in dirty)
        {
            if (_cache.TryClaim(out var frame))
            {
                var replaced = frame.Buffer;
                frame.Buffer = page;
                _cache.PublishCommitted(frame, new PageNumber(number));
                if (replaced is not null)
                {
                    ReturnBuffer(replaced);
                }
            }
            else
            {
                // No room: at least make sure no stale copy of a reused page number stays cached.
                _cache.Invalidate(new PageNumber(number));
                ReturnBuffer(page);
            }
        }

        dirty.Clear();
        if (freeList is not null)
        {
            FreeList.Apply(freeList);
        }

        _retained.Add(state);
        Volatile.Write(ref _current, new SnapshotState(next));
    }

    private static async ValueTask<Pager> CreateCoreAsync(
        IStorageDevice device, PagerOptions options, PasswordBytes? password, CancellationToken cancellationToken)
    {
        using (password)
        {
            if (device.Length != 0)
            {
                throw new IOException("Cannot create a database over a file that already has data.");
            }

            var pageBytes = options.PageSize.Bytes;
            var meta = new byte[pageBytes * (int)FileHeader.MetaPages];
            FileHeader.WriteFixed(meta, options.PageSize, encrypted: password is not null, options.Compression);

            IPageProtector protector = ChecksumPageProtector.Instance;
            if (password is not null)
            {
                // Checked here so that a bad option is an argument error, not a "corrupt file" one.
                ArgumentOutOfRangeException.ThrowIfGreaterThan(
                    options.KeyDerivation.MemoryKiB, KeyEnvelope.MaxMemoryKiB, nameof(options));

                // The key derivation is deliberately expensive: it runs off the caller's thread.
                protector = await Task.Run(
                    () =>
                    {
                        Span<byte> fileKey = stackalloc byte[AesGcmPageProtector.KeySize];
                        RandomNumberGenerator.Fill(fileKey);
                        try
                        {
                            var first = meta.AsSpan(0, FileHeader.FixedSize);
                            KeyEnvelope.Seal(first, password.Span, options.KeyDerivation, fileKey);
                            FileHeader.CopyKeyEnvelope(first, meta.AsSpan(pageBytes, FileHeader.FixedSize));
                            return new AesGcmPageProtector(fileKey);
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(fileKey);
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var initial = new Snapshot(PageNumber.None, FileHeader.MetaPages, new TransactionId(0), PageNumber.None);
                FileHeader.EncodeSlot(meta.AsSpan((int)FileHeader.SlotOffset(0, options.PageSize)), initial, protector);
                await device.WriteAsync(0, meta, cancellationToken).ConfigureAwait(false);
                await device.FlushAsync(cancellationToken).ConfigureAwait(false);
                return new Pager(device, options.PageSize, options, protector, options.Compression, initial, slot: 0);
            }
            catch
            {
                protector.Dispose();
                throw;
            }
        }
    }

    private static async ValueTask<Pager> OpenCoreAsync(
        IStorageDevice device, PagerOptions options, PasswordBytes? password, CancellationToken cancellationToken)
    {
        using (password)
        {
            if (device.Length < FileHeader.FixedSize)
            {
                throw new InvalidDataException("The file is not a TekiumDB database.");
            }

            var first = new byte[FileHeader.FixedSize];
            var second = new byte[FileHeader.FixedSize];
            await device.ReadAsync(0, first, cancellationToken).ConfigureAwait(false);
            var pageSize = FileHeader.ReadPageSize(first);
            if (device.Length < pageSize.Bytes * (long)FileHeader.MetaPages)
            {
                throw new InvalidDataException("The database header is truncated.");
            }

            await device.ReadAsync(pageSize.Bytes, second, cancellationToken).ConfigureAwait(false);

            IPageProtector protector = ChecksumPageProtector.Instance;
            if (FileHeader.IsEncrypted(first))
            {
                if (password is null)
                {
                    throw new CryptographicException("The database is encrypted: a password is required to open it.");
                }

                protector = await Task.Run(() => Unlock(first, second, password), cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (password is not null)
            {
                throw new CryptographicException("A password was given, but the database is not encrypted.");
            }

            Pager pager;
            try
            {
                var current = FileHeader.ReadCurrentSnapshot(first, second, protector, out var slot);
                if (current.PageCount < FileHeader.MetaPages || (long)current.PageCount * pageSize.Bytes > device.Length)
                {
                    throw new InvalidDataException("The database header points past the end of the file.");
                }

                pager = new Pager(device, pageSize, options, protector, FileHeader.IsCompressed(first), current, slot);

                // Take the larger copy as the truth and overwrite the other one first.
                var reservedFirst = FileHeader.ReadReservation(first, protector);
                var reservedSecond = FileHeader.ReadReservation(second, protector);
                pager._reservedThrough = Math.Max(reservedFirst, reservedSecond);
                pager._reservationCopy = reservedFirst <= reservedSecond ? 0 : 1;
            }
            catch
            {
                protector.Dispose();
                throw;
            }

            pager.FreeList = await FreeList.LoadAsync(pager, pager.PageSize, pager.Current.FreeList, cancellationToken)
                .ConfigureAwait(false);
            return pager;
        }
    }

    /// <summary>Recovers the file key from either copy of the header.</summary>
    private static AesGcmPageProtector Unlock(byte[] first, byte[] second, PasswordBytes password)
    {
        Span<byte> fileKey = stackalloc byte[AesGcmPageProtector.KeySize];
        try
        {
            // The copies differ only if a password change was interrupted between writing one and the other.
            if (KeyEnvelope.TryOpen(first, password.Span, fileKey)
                || (!FileHeader.HaveSameKeyEnvelope(first, second) && KeyEnvelope.TryOpen(second, password.Span, fileKey)))
            {
                return new AesGcmPageProtector(fileKey);
            }

            throw new CryptographicException(
                "The password is incorrect, or the key data in the database header is damaged.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileKey);
        }
    }

    private void ThrowIfCommitInDoubt()
    {
        if (_commitInDoubt is not null)
        {
            throw new IOException(
                "An earlier commit failed while its root slot was being made durable. " +
                "The database accepts no more writes: reopen it to find out which state survived.",
                _commitInDoubt);
        }
    }

    private static long GrowTo(long length, long required)
    {
        length = Math.Max(length, 1);
        while (length < required)
        {
            length += Math.Min(length, GrowthStepLimit);
        }

        return length;
    }

    /// <summary>
    /// Makes sure the next ids this session issues were never used before, and records on disk how far
    /// they may go before any page is written with them.
    /// </summary>
    /// <remarks>
    /// Copy-on-write alone does not make (page, transaction id) unique. A commit that crashes after
    /// writing pages leaves ciphertext on disk under an id that was never committed, and the next session
    /// would start from the same id. And two copies of one file, each written on its own, would both
    /// continue from the same id with the same key. So: ids are reserved durably in blocks, a session
    /// never starts inside a block an earlier session reserved, and it starts a random distance ahead,
    /// which keeps independent copies of the file apart.
    /// </remarks>
    private async ValueTask<ulong> ReserveTransactionIdsAsync(ulong id, CancellationToken cancellationToken)
    {
        if (!_sessionReserved)
        {
            Span<byte> random = stackalloc byte[sizeof(ulong)];
            RandomNumberGenerator.Fill(random);
            var gap = BinaryPrimitives.ReadUInt64LittleEndian(random) >> (64 - SessionGapBits);
            id = checked(Math.Max(id, _reservedThrough + 1) + gap);
        }

        var through = checked(id + ReservationBlock);
        FileHeader.EncodeReservation(_recordBuffer, through, _protector);
        await _device.WriteAsync(
            FileHeader.ReservationOffsetOf(_reservationCopy, PageSize),
            _recordBuffer.AsMemory(0, FileHeader.ReservationSize),
            cancellationToken).ConfigureAwait(false);
        await _device.FlushAsync(cancellationToken).ConfigureAwait(false);

        _reservationCopy ^= 1;
        _reservedThrough = through;
        _sessionReserved = true;
        return id;
    }

    private async ValueTask ChangePasswordCoreAsync(
        AesGcmPageProtector protector, PasswordBytes password, CancellationToken cancellationToken)
    {
        using (password)
        {
            // The writer slot: commits write the same pages of the file.
            await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfCommitInDoubt();
                var sealedHeader = new byte[FileHeader.FixedSize];
                var copy = new byte[FileHeader.FixedSize];
                await _device.ReadAsync(0, sealedHeader, cancellationToken).ConfigureAwait(false);
                await Task.Run(
                    () => protector.SealEnvelope(sealedHeader, password.Span, _options.KeyDerivation),
                    cancellationToken).ConfigureAwait(false);

                for (var page = 0; page < FileHeader.MetaPages; page++)
                {
                    // Each copy keeps its own root slot and reservation; only the envelope changes.
                    var offset = page * (long)PageSize.Bytes;
                    await _device.ReadAsync(offset, copy, cancellationToken).ConfigureAwait(false);
                    FileHeader.CopyKeyEnvelope(sealedHeader, copy);
                    await _device.WriteAsync(offset, copy, cancellationToken).ConfigureAwait(false);
                    await _device.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _writerGate.Release();
            }
        }
    }

    /// <summary>Protects and writes the pages in file order, one write per run of consecutive pages.</summary>
    private async ValueTask WritePagesAsync(
        Dictionary<uint, byte[]> pages, uint pageCount, CancellationToken cancellationToken)
    {
        var pageBytes = PageSize.Bytes;
        var required = (long)pageCount * pageBytes;
        if (_device.Length < required)
        {
            _device.SetLength(GrowTo(_device.Length, required));
        }

        var count = pages.Count;
        var numbers = ArrayPool<uint>.Shared.Rent(count);
        var buffer = ArrayPool<byte>.Shared.Rent(
            (int)Math.Max(pageBytes, Math.Min(CommitBufferBytes, (long)count * pageBytes)));
        try
        {
            pages.Keys.CopyTo(numbers, 0);
            Array.Sort(numbers, 0, count);
            var perWrite = buffer.Length / pageBytes;
            for (var i = 0; i < count;)
            {
                // A run of consecutive pages goes out in one write. A page stored shorter than its slot,
                // because it was compressed, ends the run: what follows it in the file is not ours to write.
                var first = numbers[i];
                var filled = 0;
                int stored;
                do
                {
                    stored = _protector.Protect(pages[numbers[i]], buffer.AsSpan(filled * pageBytes, pageBytes));
                    filled++;
                    i++;
                }
                while (stored == pageBytes && i < count && filled < perWrite && numbers[i] == numbers[i - 1] + 1);

                await _device.WriteAsync(
                    first * (long)pageBytes, buffer.AsMemory(0, ((filled - 1) * pageBytes) + stored), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(numbers);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask<PageLease> LoadAsync(PageNumber number, CancellationToken cancellationToken)
    {
        if (number.Value < FileHeader.MetaPages || number.Value >= Current.PageCount)
        {
            throw new InvalidDataException($"Page {number.Value} is referenced but is not part of the database.");
        }

        var frame = _cache.ClaimForLoad();
        try
        {
            frame.Buffer ??= GC.AllocateUninitializedArray<byte>(PageSize.Bytes);
            await _device.ReadAsync(number.Value * (long)PageSize.Bytes, frame.Buffer, cancellationToken)
                .ConfigureAwait(false);
            _protector.Unprotect(frame.Buffer, number);
        }
        catch
        {
            PageCache.Abandon(frame);
            throw;
        }

        return _cache.PublishLoaded(frame, number);
    }
}
