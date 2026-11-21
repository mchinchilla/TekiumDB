// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers;
using System.Buffers.Binary;
using TekiumDB.Storage;

namespace TekiumDB.Indexing;

/// <summary>
/// Byte layout of a B+Tree node, read and written in place over the page payload. Nothing is deserialized.
/// </summary>
/// <remarks>
/// <code>
/// 0   u8   type (1 leaf, 2 internal)
/// 2   u16  entry count
/// 4   u16  cell start: offset of the lowest cell
/// 6   u16  garbage: bytes of dead cells that compaction reclaims
/// 8   u32  rightmost child (internal only)
/// 12  u16 × count   slot directory: cell offsets in key order, growing up
/// ...               free space
/// cell start        cells, growing down
/// </code>
/// Leaf cell: key length u16, value length u16, key, value. The top bit of the value length says the
/// value is too large for a leaf: what the cell holds then is an overflow reference, its total length
/// (u32) and the first page of the chain that holds it (u32).
/// Internal cell: child u32, key length u16, key. Child <c>i</c> holds the keys below key <c>i</c> and not below
/// key <c>i - 1</c>; the rightmost child holds the keys not below the last key.
/// Offsets are relative to the payload: the page header and checksum belong to the pager.
/// Not yet: prefix compression, zone maps.
/// </remarks>
internal static class BTreeNode
{
    public const int HeaderSize = 12;

    /// <summary>What a leaf keeps of a value that lives in overflow pages.</summary>
    public const int OverflowReferenceSize = 8;

    // A page is at most 32 KiB, so a length stored in a cell never needs the top bit.
    private const int OverflowFlag = 0x8000;
    private const int LengthMask = 0x7FFF;

    private const byte LeafType = 1;
    private const byte InternalType = 2;
    private const int SlotSize = 2;
    private const int CountOffset = 2;
    private const int CellStartOffset = 4;
    private const int GarbageOffset = 6;
    private const int RightChildOffset = 8;
    private const int LeafCellHeader = 4;
    private const int InternalCellHeader = 6;

    public static void Initialize(Span<byte> page, bool leaf)
    {
        page[..HeaderSize].Clear();
        page[0] = leaf ? LeafType : InternalType;
        WriteU16(page, CellStartOffset, page.Length);
    }

    public static bool IsLeaf(ReadOnlySpan<byte> page) => page[0] == LeafType;

    public static int Count(ReadOnlySpan<byte> page) => ReadU16(page, CountOffset);

    public static int Garbage(ReadOnlySpan<byte> page) => ReadU16(page, GarbageOffset);

    /// <summary>Contiguous free bytes between the slot directory and the cells.</summary>
    public static int FreeSpace(ReadOnlySpan<byte> page) =>
        CellStart(page) - HeaderSize - (Count(page) * SlotSize);

    /// <summary>Bytes available for entries in a node of this size.</summary>
    public static int Capacity(ReadOnlySpan<byte> page) => page.Length - HeaderSize;

    /// <summary>Bytes taken by live entries, slots included. Garbage does not count: compaction reclaims it.</summary>
    public static int UsedSpace(ReadOnlySpan<byte> page) => Capacity(page) - FreeSpace(page) - Garbage(page);

    public static int LeafEntrySize(int keyLength, int valueLength) =>
        LeafCellHeader + keyLength + valueLength + SlotSize;

    public static ReadOnlySpan<byte> Key(ReadOnlySpan<byte> page, int index)
    {
        var cell = CellOffset(page, index);
        return IsLeaf(page)
            ? page.Slice(cell + LeafCellHeader, ReadU16(page, cell))
            : page.Slice(cell + InternalCellHeader, ReadU16(page, cell + 4));
    }

    public static ReadOnlySpan<byte> LeafValue(ReadOnlySpan<byte> page, int index)
    {
        LeafValueRange(page, index, out var offset, out var length);
        return page.Slice(offset, length);
    }

    public static void LeafKeyRange(ReadOnlySpan<byte> page, int index, out int offset, out int length)
    {
        var cell = CellOffset(page, index);
        offset = cell + LeafCellHeader;
        length = ReadU16(page, cell);
    }

    public static void LeafValueRange(ReadOnlySpan<byte> page, int index, out int offset, out int length)
    {
        var cell = CellOffset(page, index);
        offset = cell + LeafCellHeader + ReadU16(page, cell);
        length = ReadU16(page, cell + 2) & LengthMask;
    }

    /// <summary>Whether what the leaf holds for this entry is a reference to overflow pages, not the value.</summary>
    public static bool IsOverflow(ReadOnlySpan<byte> page, int index) =>
        (ReadU16(page, CellOffset(page, index) + 2) & OverflowFlag) != 0;

    public static void WriteOverflowReference(Span<byte> destination, int length, PageNumber first)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], first.Value);
    }

    public static void ReadOverflowReference(ReadOnlySpan<byte> reference, out int length, out PageNumber first)
    {
        var stored = reference.Length == OverflowReferenceSize ? BinaryPrimitives.ReadUInt32LittleEndian(reference) : 0;
        if (stored == 0 || stored > int.MaxValue)
        {
            throw new InvalidDataException("A B+Tree leaf holds a damaged reference to a large value.");
        }

        length = (int)stored;
        first = new PageNumber(BinaryPrimitives.ReadUInt32LittleEndian(reference[4..]));
    }

    /// <summary>Child <paramref name="index"/>; an index equal to the count is the rightmost child.</summary>
    public static PageNumber Child(ReadOnlySpan<byte> page, int index) =>
        new(BinaryPrimitives.ReadUInt32LittleEndian(
            page[(index == Count(page) ? RightChildOffset : CellOffset(page, index))..]));

    public static void SetChild(Span<byte> page, int index, PageNumber child) =>
        BinaryPrimitives.WriteUInt32LittleEndian(
            page[(index == Count(page) ? RightChildOffset : CellOffset(page, index))..], child.Value);

    /// <summary>Index of the first key not below <paramref name="key"/>.</summary>
    public static int LowerBound(ReadOnlySpan<byte> page, ReadOnlySpan<byte> key, out bool found)
    {
        var leaf = IsLeaf(page);
        var lengthAt = leaf ? 0 : 4;
        var keyAt = leaf ? LeafCellHeader : InternalCellHeader;
        var low = 0;
        var high = Count(page) - 1;
        while (low <= high)
        {
            var middle = (int)(((uint)low + (uint)high) >> 1);
            var cell = CellOffset(page, middle);
            var order = page.Slice(cell + keyAt, ReadU16(page, cell + lengthAt)).SequenceCompareTo(key);
            if (order == 0)
            {
                found = true;
                return middle;
            }

            if (order < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        found = false;
        return low;
    }

    /// <summary>Index of the child of an internal node whose range contains <paramref name="key"/>.</summary>
    public static int FindChildIndex(ReadOnlySpan<byte> page, ReadOnlySpan<byte> key)
    {
        var index = LowerBound(page, key, out var found);
        return found ? index + 1 : index;
    }

    public static bool TryInsertLeaf(
        Span<byte> page, int index, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, bool overflow = false)
    {
        var size = LeafCellHeader + key.Length + value.Length;
        if (FreeSpace(page) < size + SlotSize)
        {
            return false;
        }

        var cell = CellStart(page) - size;
        WriteU16(page, cell, key.Length);
        WriteU16(page, cell + 2, overflow ? value.Length | OverflowFlag : value.Length);
        key.CopyTo(page[(cell + LeafCellHeader)..]);
        value.CopyTo(page[(cell + LeafCellHeader + key.Length)..]);
        InsertSlot(page, index, cell);
        return true;
    }

    public static bool TryInsertInternal(Span<byte> page, int index, PageNumber child, ReadOnlySpan<byte> key)
    {
        var size = InternalCellHeader + key.Length;
        if (FreeSpace(page) < size + SlotSize)
        {
            return false;
        }

        var cell = CellStart(page) - size;
        BinaryPrimitives.WriteUInt32LittleEndian(page[cell..], child.Value);
        WriteU16(page, cell + 4, key.Length);
        key.CopyTo(page[(cell + InternalCellHeader)..]);
        InsertSlot(page, index, cell);
        return true;
    }

    /// <summary>Drops an entry. Its cell stays as garbage until <see cref="Compact"/>.</summary>
    public static void RemoveSlot(Span<byte> page, int index)
    {
        var count = Count(page);
        WriteU16(page, GarbageOffset, Garbage(page) + CellSize(page, CellOffset(page, index)));
        var slot = HeaderSize + (index * SlotSize);
        page.Slice(slot + SlotSize, (count - index - 1) * SlotSize).CopyTo(page[slot..]);
        WriteU16(page, CountOffset, count - 1);
    }

    /// <summary>Rewrites the live cells contiguously, turning garbage back into free space.</summary>
    public static void Compact(Span<byte> page)
    {
        var scratch = ArrayPool<byte>.Shared.Rent(page.Length);
        try
        {
            var copy = scratch.AsSpan(0, page.Length);
            page.CopyTo(copy);
            var end = page.Length;
            var count = Count(page);
            for (var i = 0; i < count; i++)
            {
                var cell = CellOffset(copy, i);
                var size = CellSize(copy, cell);
                end -= size;
                copy.Slice(cell, size).CopyTo(page[end..]);
                WriteU16(page, HeaderSize + (i * SlotSize), end);
            }

            WriteU16(page, CellStartOffset, end);
            WriteU16(page, GarbageOffset, 0);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>Entry index that splits the node into two halves of similar byte size, never leaving one empty.</summary>
    public static int ChooseSplitIndex(ReadOnlySpan<byte> page)
    {
        var count = Count(page);
        var total = 0;
        for (var i = 0; i < count; i++)
        {
            total += CellSize(page, CellOffset(page, i)) + SlotSize;
        }

        var accumulated = 0;
        var index = 0;
        while (index < count && accumulated < total / 2)
        {
            accumulated += CellSize(page, CellOffset(page, index)) + SlotSize;
            index++;
        }

        return Math.Clamp(index, 1, count - 1);
    }

    /// <summary>Moves the entries from <paramref name="index"/> on into the empty leaf <paramref name="right"/>.</summary>
    public static void SplitLeaf(Span<byte> left, Span<byte> right, int index)
    {
        var count = Count(left);
        for (var i = index; i < count; i++)
        {
            if (!TryInsertLeaf(right, i - index, Key(left, i), LeafValue(left, i), IsOverflow(left, i)))
            {
                throw new InvalidDataException("A B+Tree leaf holds more than a page can contain.");
            }
        }

        WriteU16(left, CountOffset, index);
        Compact(left);
    }

    /// <summary>
    /// Splits an internal node around key <paramref name="index"/>, which moves up to the parent:
    /// it is copied to <paramref name="separator"/> and its length returned.
    /// </summary>
    public static int SplitInternal(Span<byte> left, Span<byte> right, int index, Span<byte> separator)
    {
        var count = Count(left);
        var key = Key(left, index);
        key.CopyTo(separator);
        var length = key.Length;

        for (var i = index + 1; i < count; i++)
        {
            if (!TryInsertInternal(right, i - index - 1, Child(left, i), Key(left, i)))
            {
                throw new InvalidDataException("A B+Tree node holds more than a page can contain.");
            }
        }

        SetChild(right, Count(right), Child(left, count));
        var middleChild = Child(left, index);
        WriteU16(left, CountOffset, index);
        SetChild(left, index, middleChild);
        Compact(left);
        return length;
    }

    /// <summary>
    /// Writes into <paramref name="target"/> the node that results from joining two siblings, if it fits
    /// in one page. For internal nodes <paramref name="separator"/> is the parent key between them, which
    /// comes down to sit between the last child of the left node and the first of the right one.
    /// </summary>
    /// <remarks><paramref name="target"/> must not overlap either input.</remarks>
    public static bool TryMerge(
        Span<byte> target, ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, ReadOnlySpan<byte> separator)
    {
        var leaf = IsLeaf(left);
        var needed = UsedSpace(left) + UsedSpace(right)
            + (leaf ? 0 : InternalCellHeader + separator.Length + SlotSize);
        if (needed > Capacity(left))
        {
            return false;
        }

        Initialize(target, leaf);
        var leftCount = Count(left);
        var rightCount = Count(right);
        var fits = true;
        if (leaf)
        {
            for (var i = 0; i < leftCount; i++)
            {
                fits &= TryInsertLeaf(target, i, Key(left, i), LeafValue(left, i), IsOverflow(left, i));
            }

            for (var i = 0; i < rightCount; i++)
            {
                fits &= TryInsertLeaf(target, leftCount + i, Key(right, i), LeafValue(right, i), IsOverflow(right, i));
            }
        }
        else
        {
            for (var i = 0; i < leftCount; i++)
            {
                fits &= TryInsertInternal(target, i, Child(left, i), Key(left, i));
            }

            fits &= TryInsertInternal(target, leftCount, Child(left, leftCount), separator);
            for (var i = 0; i < rightCount; i++)
            {
                fits &= TryInsertInternal(target, leftCount + 1 + i, Child(right, i), Key(right, i));
            }

            SetChild(target, Count(target), Child(right, rightCount));
        }

        return fits ? true : throw new InvalidDataException("A B+Tree node reports less used space than it holds.");
    }

    private static int CellStart(ReadOnlySpan<byte> page) => ReadU16(page, CellStartOffset);

    private static int CellOffset(ReadOnlySpan<byte> page, int index) =>
        ReadU16(page, HeaderSize + (index * SlotSize));

    private static int CellSize(ReadOnlySpan<byte> page, int cell) =>
        IsLeaf(page)
            ? LeafCellHeader + ReadU16(page, cell) + (ReadU16(page, cell + 2) & LengthMask)
            : InternalCellHeader + ReadU16(page, cell + 4);

    private static void InsertSlot(Span<byte> page, int index, int cell)
    {
        var count = Count(page);
        var slot = HeaderSize + (index * SlotSize);
        page.Slice(slot, (count - index) * SlotSize).CopyTo(page[(slot + SlotSize)..]);
        WriteU16(page, slot, cell);
        WriteU16(page, CellStartOffset, cell);
        WriteU16(page, CountOffset, count + 1);
    }

    private static int ReadU16(ReadOnlySpan<byte> page, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(page[offset..]);

    private static void WriteU16(Span<byte> page, int offset, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(page[offset..], (ushort)value);
}
