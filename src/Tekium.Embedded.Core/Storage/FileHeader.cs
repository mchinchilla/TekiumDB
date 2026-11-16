// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Hashing;

namespace Tekium.Embedded.Storage;

/// <summary>
/// Layout of the two meta pages (pages 0 and 1). Both start with the same fixed fields, written once
/// at creation. Root slots are written alternately, each commit to the page that does not hold the
/// current state, so a torn write of the new slot cannot reach the slot the database would fall back to.
/// </summary>
/// <remarks>
/// <code>
/// 0   8   magic
/// 8   u16 format version
/// 10  u16 page size
/// 12  u32 flags (encryption, compression: none yet)
/// 16  16  KDF salt (reserved)
/// 32  12  Argon2id parameters (reserved)
/// 44  u16 root slot count
/// 64      root slots of this page, 48 bytes each
/// </code>
/// Root slot: root u32, page count u32, transaction id u64, free-list head u32, 12 reserved,
/// XxHash3 of those 32 bytes, 8 reserved. Encryption replaces the hash with a MAC.
/// </remarks>
internal static class FileHeader
{
    /// <summary>Pages reserved for the header.</summary>
    public const uint MetaPages = 2;

    /// <summary>Bytes of each meta page that hold fields; the rest is reserved zeros.</summary>
    public const int FixedSize = 512;

    public const int SlotSize = 48;
    public const int SlotCount = 2;
    public const ushort FormatVersion = 1;

    private const int VersionOffset = 8;
    private const int PageSizeOffset = 10;
    private const int SlotCountOffset = 44;
    private const int SlotsOffset = 64;
    private const int SlotChecksumOffset = 32;

    private static ReadOnlySpan<byte> Magic => "TekiumDB"u8;

    /// <summary>File offset of the slot a transaction commits to.</summary>
    public static long SlotOffset(TransactionId transactionId, PageSize pageSize)
    {
        var slot = (int)(transactionId.Value % SlotCount);
        return ((slot % (int)MetaPages) * (long)pageSize.Bytes) + SlotsOffset + ((slot / (int)MetaPages) * SlotSize);
    }

    /// <summary>Fills both meta pages of a new database.</summary>
    public static void Write(Span<byte> metaPages, PageSize pageSize, Snapshot initial)
    {
        metaPages.Clear();
        for (var page = 0; page < MetaPages; page++)
        {
            var header = metaPages.Slice(page * pageSize.Bytes, FixedSize);
            Magic.CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header[VersionOffset..], FormatVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(header[PageSizeOffset..], (ushort)pageSize.Bytes);
            BinaryPrimitives.WriteUInt16LittleEndian(header[SlotCountOffset..], SlotCount);
        }

        EncodeSlot(metaPages.Slice((int)SlotOffset(initial.TransactionId, pageSize), SlotSize), initial);
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

    /// <summary>Returns the newest root slot that passes its check, looking at the fixed part of both meta pages.</summary>
    public static Snapshot ReadCurrentSnapshot(ReadOnlySpan<byte> firstMeta, ReadOnlySpan<byte> secondMeta)
    {
        Snapshot? newest = null;
        for (var slot = 0; slot < SlotCount; slot++)
        {
            var page = slot % (int)MetaPages == 0 ? firstMeta : secondMeta;
            if (TryDecodeSlot(page.Slice(SlotsOffset + ((slot / (int)MetaPages) * SlotSize), SlotSize), out var candidate)
                && (newest is null || candidate.TransactionId.Value > newest.TransactionId.Value))
            {
                newest = candidate;
            }
        }

        return newest ?? throw new InvalidDataException("The database header has no valid root slot.");
    }

    public static void EncodeSlot(Span<byte> slot, Snapshot snapshot)
    {
        slot[..SlotSize].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(slot, snapshot.Root.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(slot[4..], snapshot.PageCount);
        BinaryPrimitives.WriteUInt64LittleEndian(slot[8..], snapshot.TransactionId.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(slot[16..], snapshot.FreeList.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(
            slot[SlotChecksumOffset..], XxHash3.HashToUInt64(slot[..SlotChecksumOffset]));
    }

    public static bool TryDecodeSlot(ReadOnlySpan<byte> slot, [NotNullWhen(true)] out Snapshot? snapshot)
    {
        // A never-written slot is all zeros, and XxHash3 of 32 zero bytes is not zero: it reads as invalid.
        if (BinaryPrimitives.ReadUInt64LittleEndian(slot[SlotChecksumOffset..])
            != XxHash3.HashToUInt64(slot[..SlotChecksumOffset]))
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
}
