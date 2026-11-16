// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace Tekium.Embedded.Storage;

/// <summary>
/// Layout of the two meta pages (pages 0 and 1), which are never encrypted: the key derivation
/// parameters must be readable before there is a key. Both start with the same fields. Records that
/// change are written alternately to one page and then the other, so a torn write cannot reach the copy
/// the database would fall back to.
/// </summary>
/// <remarks>
/// <code>
/// 0    8   magic
/// 8    u16 format version
/// 10   u16 page size
/// 12   u32 flags (bit 0: encrypted)
/// 16   16  key derivation salt
/// 32   12  Argon2id memory in KiB, iterations, lanes (u32 each)
/// 44   u16 root slot count
/// 64   48  root slot of this page
/// 128  60  wrapped file key: nonce 12, ciphertext 32, tag 16
/// 200  24  transaction id reservation: highest id that may have been used u64, check 16
/// </code>
/// Root slot: root u32, page count u32, transaction id u64, free-list head u32, 12 reserved, check 16.
/// The check of a slot or a reservation is an XxHash3 without encryption and a keyed MAC with it.
/// </remarks>
internal static class FileHeader
{
    /// <summary>Pages reserved for the header.</summary>
    public const uint MetaPages = 2;

    /// <summary>Bytes of each meta page that hold fields; the rest is reserved zeros.</summary>
    public const int FixedSize = 512;

    public const int SlotSize = 48;
    public const int SlotCount = 2;
    public const ushort FormatVersion = 2;

    public const int SaltOffset = 16;
    public const int SaltSize = 16;
    public const int KeyDerivationOffset = 32;

    /// <summary>Leading bytes that authenticate the key wrap: altering any of them invalidates the password.</summary>
    public const int AuthenticatedPrefixSize = 48;

    public const int KeyWrapOffset = 128;
    public const int KeyWrapSize = 60;
    public const int ReservationOffset = 200;
    public const int ReservationSize = 24;

    private const int VersionOffset = 8;
    private const int PageSizeOffset = 10;
    private const int FlagsOffset = 12;
    private const int SlotCountOffset = 44;
    private const int SlotsOffset = 64;
    private const int CheckSize = 16;
    private const int SlotCheckOffset = SlotSize - CheckSize;
    private const uint EncryptedFlag = 1;

    private static ReadOnlySpan<byte> Magic => "TekiumDB"u8;

    private static ReadOnlySpan<byte> ReservationLabel => "TKRESV01"u8;

    /// <summary>File offset of a root slot. Consecutive slots live in different meta pages.</summary>
    public static long SlotOffset(int slot, PageSize pageSize) =>
        ((slot % (int)MetaPages) * (long)pageSize.Bytes) + SlotsOffset + ((slot / (int)MetaPages) * SlotSize);

    /// <summary>File offset of one of the two copies of the transaction id reservation.</summary>
    public static long ReservationOffsetOf(int copy, PageSize pageSize) =>
        (copy * (long)pageSize.Bytes) + ReservationOffset;

    /// <summary>Writes the fields that never change into both meta pages of a new database.</summary>
    public static void WriteFixed(Span<byte> metaPages, PageSize pageSize, bool encrypted)
    {
        metaPages.Clear();
        for (var page = 0; page < MetaPages; page++)
        {
            var header = metaPages.Slice(page * pageSize.Bytes, FixedSize);
            Magic.CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header[VersionOffset..], FormatVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(header[PageSizeOffset..], (ushort)pageSize.Bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(header[FlagsOffset..], encrypted ? EncryptedFlag : 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header[SlotCountOffset..], SlotCount);
        }
    }

    public static PageSize ReadPageSize(ReadOnlySpan<byte> header)
    {
        if (!header[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException("The file is not a TekiumDB database.");
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(header[VersionOffset..]);
        if (version != FormatVersion)
        {
            throw new InvalidDataException(
                $"The file uses format version {version}; this build reads version {FormatVersion}.");
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(header[SlotCountOffset..]) != SlotCount
            || !PageSize.TryFromBytes(BinaryPrimitives.ReadUInt16LittleEndian(header[PageSizeOffset..]), out var size))
        {
            throw new InvalidDataException("The database header is corrupt.");
        }

        return size;
    }

    public static bool IsEncrypted(ReadOnlySpan<byte> header) =>
        (BinaryPrimitives.ReadUInt32LittleEndian(header[FlagsOffset..]) & EncryptedFlag) != 0;

    /// <summary>Copies the salt, derivation parameters and wrapped key from one header copy to another.</summary>
    public static void CopyKeyEnvelope(ReadOnlySpan<byte> from, Span<byte> to)
    {
        from[SaltOffset..AuthenticatedPrefixSize].CopyTo(to[SaltOffset..]);
        from.Slice(KeyWrapOffset, KeyWrapSize).CopyTo(to[KeyWrapOffset..]);
    }

    public static bool HaveSameKeyEnvelope(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second) =>
        first[SaltOffset..AuthenticatedPrefixSize].SequenceEqual(second[SaltOffset..AuthenticatedPrefixSize])
        && first.Slice(KeyWrapOffset, KeyWrapSize).SequenceEqual(second.Slice(KeyWrapOffset, KeyWrapSize));

    /// <summary>Returns the newest root slot that passes its check, and which slot it is.</summary>
    public static Snapshot ReadCurrentSnapshot(
        ReadOnlySpan<byte> firstMeta, ReadOnlySpan<byte> secondMeta, IPageProtector protector, out int slot)
    {
        Snapshot? newest = null;
        slot = -1;
        for (var i = 0; i < SlotCount; i++)
        {
            var page = i % (int)MetaPages == 0 ? firstMeta : secondMeta;
            var bytes = page.Slice(SlotsOffset + ((i / (int)MetaPages) * SlotSize), SlotSize);
            if (TryDecodeSlot(bytes, protector, out var candidate)
                && (newest is null || candidate.TransactionId.Value > newest.TransactionId.Value))
            {
                newest = candidate;
                slot = i;
            }
        }

        return newest ?? throw new InvalidDataException("The database header has no valid root slot.");
    }

    public static void EncodeSlot(Span<byte> slot, Snapshot snapshot, IPageProtector protector)
    {
        slot[..SlotSize].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(slot, snapshot.Root.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(slot[4..], snapshot.PageCount);
        BinaryPrimitives.WriteUInt64LittleEndian(slot[8..], snapshot.TransactionId.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(slot[16..], snapshot.FreeList.Value);
        protector.Authenticate(slot[..SlotCheckOffset], slot.Slice(SlotCheckOffset, CheckSize));
    }

    public static bool TryDecodeSlot(
        ReadOnlySpan<byte> slot, IPageProtector protector, [NotNullWhen(true)] out Snapshot? snapshot)
    {
        // A never-written slot is all zeros and no check of 32 zero bytes is zero: it reads as invalid.
        Span<byte> check = stackalloc byte[CheckSize];
        protector.Authenticate(slot[..SlotCheckOffset], check);
        if (!check.SequenceEqual(slot.Slice(SlotCheckOffset, CheckSize)))
        {
            snapshot = null;
            return false;
        }

        snapshot = new Snapshot(
            new PageNumber(BinaryPrimitives.ReadUInt32LittleEndian(slot)),
            BinaryPrimitives.ReadUInt32LittleEndian(slot[4..]),
            new TransactionId(BinaryPrimitives.ReadUInt64LittleEndian(slot[8..])),
            new PageNumber(BinaryPrimitives.ReadUInt32LittleEndian(slot[16..])));
        return true;
    }

    public static void EncodeReservation(Span<byte> record, ulong through, IPageProtector protector)
    {
        Span<byte> data = stackalloc byte[sizeof(ulong) + 8];
        BinaryPrimitives.WriteUInt64LittleEndian(data, through);
        ReservationLabel.CopyTo(data[sizeof(ulong)..]);
        BinaryPrimitives.WriteUInt64LittleEndian(record, through);
        protector.Authenticate(data, record.Slice(sizeof(ulong), CheckSize));
    }

    /// <summary>Reads one copy of the reservation. An invalid or never-written copy reads as zero.</summary>
    public static ulong ReadReservation(ReadOnlySpan<byte> header, IPageProtector protector)
    {
        var record = header.Slice(ReservationOffset, ReservationSize);
        Span<byte> expected = stackalloc byte[ReservationSize];
        var through = BinaryPrimitives.ReadUInt64LittleEndian(record);
        EncodeReservation(expected, through, protector);
        return expected.SequenceEqual(record) ? through : 0;
    }
}
