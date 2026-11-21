// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using System.IO.Hashing;

namespace TekiumDB.Storage;

/// <summary>
/// The part of every page that belongs to the pager. Whatever lives above only sees the payload.
/// </summary>
/// <remarks>
/// <code>
/// 0   u8   page type
/// 4   u32  page number: a page read from the wrong position is detected
/// 8   u64  id of the transaction that wrote it
/// 16       payload
/// -16 u64  XxHash3 of everything before the trailer
/// -8       reserved: the trailer is 16 bytes so that an AES-GCM tag fits when encryption arrives
/// </code>
/// </remarks>
internal static class PageLayout
{
    public const int HeaderSize = 16;
    public const int TrailerSize = 16;
    public const int Overhead = HeaderSize + TrailerSize;

    private const int NumberOffset = 4;
    private const int TransactionOffset = 8;

    public static int PayloadSize(PageSize pageSize) => pageSize.Bytes - Overhead;

    public static Memory<byte> Payload(byte[] page) => page.AsMemory(HeaderSize, page.Length - Overhead);

    public static PageType Type(ReadOnlySpan<byte> page) => (PageType)page[0];

    public static PageNumber Number(ReadOnlySpan<byte> page) =>
        new(BinaryPrimitives.ReadUInt32LittleEndian(page[NumberOffset..]));

    public static TransactionId Transaction(ReadOnlySpan<byte> page) =>
        new(BinaryPrimitives.ReadUInt64LittleEndian(page[TransactionOffset..]));

    public static void Stamp(Span<byte> page, PageType type, PageNumber number, TransactionId transactionId)
    {
        page[..HeaderSize].Clear();
        page[0] = (byte)type;
        Restamp(page, number, transactionId);
    }

    /// <summary>Gives a copied page its own identity, keeping its type.</summary>
    public static void Restamp(Span<byte> page, PageNumber number, TransactionId transactionId)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(page[NumberOffset..], number.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(page[TransactionOffset..], transactionId.Value);
    }

    /// <summary>Writes the checksum. Called once, right before the page goes to disk.</summary>
    public static void Seal(Span<byte> page)
    {
        var trailer = page[^TrailerSize..];
        trailer.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(trailer, XxHash3.HashToUInt64(page[..^TrailerSize]));
    }

    /// <summary>Checks a page read from disk. A failure names the page: corruption is never silent.</summary>
    public static void Verify(ReadOnlySpan<byte> page, PageNumber expected)
    {
        if (BinaryPrimitives.ReadUInt64LittleEndian(page[^TrailerSize..]) != XxHash3.HashToUInt64(page[..^TrailerSize]))
        {
            throw new InvalidDataException($"Page {expected.Value} failed its checksum: the file is corrupt.");
        }

        if (Number(page) != expected)
        {
            throw new InvalidDataException(
                $"Page {expected.Value} holds the contents of page {Number(page).Value}: the file is corrupt.");
        }
    }
}
