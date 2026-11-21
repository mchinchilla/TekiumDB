// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using TekiumDB.Storage;

namespace TekiumDB.Catalog;

/// <summary>What the catalog says about a table as of one commit: where its rows are and which schema version writes use.</summary>
internal readonly record struct TableInfo(PageNumber Root, byte SchemaVersion)
{
    public const int EncodedSize = 6;

    private const byte Format = 1;

    public void Encode(Span<byte> destination)
    {
        destination[0] = Format;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[1..], Root.Value);
        destination[5] = SchemaVersion;
    }

    public static TableInfo Decode(ReadOnlySpan<byte> source) =>
        source.Length == EncodedSize && source[0] == Format && source[5] != 0
            ? new TableInfo(new PageNumber(BinaryPrimitives.ReadUInt32LittleEndian(source[1..])), source[5])
            : throw new InvalidDataException("A table entry in the catalog is damaged.");
}
