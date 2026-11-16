// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace Tekium.Embedded.Storage;

/// <summary>
/// Layout of page 0. Fixed fields first, then a ring of root slots written alternately:
/// a commit is one write of the slot that does not hold the current state.
/// </summary>
/// <remarks>
/// Phase 0.5 spike: two slots, format version 0, and the slot check is FNV-1a instead of a MAC.
/// Everything a commit rewrites sits inside the first 512 bytes, so the write never spans a sector.
/// </remarks>
internal static class FileHeader
{
    /// <summary>Bytes that hold every header field; the rest of page 0 is reserved zeros.</summary>
    public const int FixedSize = 512;

    public const int SlotSize = 48;
    public const int SlotCount = 2;
    public const ushort FormatVersion = 0;

    private const int VersionOffset = 8;
    private const int PageSizeOffset = 10;
    private const int SlotCountOffset = 44;
    private const int SlotsOffset = 48;
    private const int SlotChecksumOffset = 32;

    private static ReadOnlySpan<byte> Magic => "TekiumDB"u8;

    public static long SlotOffset(TransactionId transactionId) =>
        SlotsOffset + (SlotSize * (long)(transactionId.Value % SlotCount));

    public static void Write(Span<byte> header, PageSize pageSize, Snapshot initial)
    {
        header[..FixedSize].Clear();
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[VersionOffset..], FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header[PageSizeOffset..], (ushort)pageSize.Bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(header[SlotCountOffset..], SlotCount);
        EncodeSlot(header.Slice((int)SlotOffset(initial.TransactionId), SlotSize), initial);
    }

    public static PageSize ReadPageSize(ReadOnlySpan<byte> header)
    {
        if (!header[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException("The file is not a TekiumDB database.");
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(header[VersionOffset..]);
        if (version > FormatVersion)
        {
            throw new InvalidDataException(
                $"The file uses format version {version}; this build reads up to version {FormatVersion}.");
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(header[SlotCountOffset..]) != SlotCount
            || !PageSize.TryFromBytes(BinaryPrimitives.ReadUInt16LittleEndian(header[PageSizeOffset..]), out var size))
        {
            throw new InvalidDataException("The database header is corrupt.");
        }

        return size;
    }

    /// <summary>Returns the newest root slot that passes its check.</summary>
    public static Snapshot ReadCurrentSnapshot(ReadOnlySpan<byte> header)
    {
        Snapshot? newest = null;
        for (var i = 0; i < SlotCount; i++)
        {
            if (TryDecodeSlot(header.Slice(SlotsOffset + (i * SlotSize), SlotSize), out var candidate)
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
        BinaryPrimitives.WriteUInt64LittleEndian(slot[SlotChecksumOffset..], Checksum(slot[..SlotChecksumOffset]));
    }

    public static bool TryDecodeSlot(ReadOnlySpan<byte> slot, [NotNullWhen(true)] out Snapshot? snapshot)
    {
        if (BinaryPrimitives.ReadUInt64LittleEndian(slot[SlotChecksumOffset..]) != Checksum(slot[..SlotChecksumOffset]))
        {
            snapshot = null;
            return false;
        }

        snapshot = new Snapshot(
            new PageNumber(BinaryPrimitives.ReadUInt32LittleEndian(slot)),
            BinaryPrimitives.ReadUInt32LittleEndian(slot[4..]),
            new TransactionId(BinaryPrimitives.ReadUInt64LittleEndian(slot[8..])));
        return true;
    }

    // FNV-1a 64. An all-zero slot does not hash to zero, so a never-written slot is invalid.
    private static ulong Checksum(ReadOnlySpan<byte> data)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in data)
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return hash;
    }
}
