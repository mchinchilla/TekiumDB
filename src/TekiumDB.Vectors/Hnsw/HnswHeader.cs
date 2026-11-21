// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using TekiumDB.Storage;

namespace TekiumDB.Vectors.Hnsw;

/// <summary>
/// The catalog entry of a vector index: how it was declared, where its trees are and where a search
/// enters the graph. File format: nothing here changes without a new format byte.
/// </summary>
internal sealed class HnswHeader
{
    public const int Size = 45;
    public const uint NoNode = uint.MaxValue;

    private const byte Format = 1;
    private const byte Trees = 4;
    private const int DefinitionOffset = 18;
    private const int DefinitionSize = 14;

    public VectorMetric Metric { get; private init; }

    public bool Quantized { get; private init; }

    public int M { get; private init; }

    public int Dimensions { get; private init; }

    public float QuantizationMin { get; private init; }

    public float QuantizationMax { get; private init; }

    /// <summary>Gets or sets the id the next vector takes. Ids are never reused.</summary>
    public uint NextId { get; set; }

    /// <summary>Gets or sets the number of keys with a vector.</summary>
    public uint Count { get; set; }

    /// <summary>Gets or sets the node a search starts from: one of those on the highest layer.</summary>
    public uint Entry { get; set; } = NoNode;

    public int MaxLevel { get; set; }

    /// <summary>Gets or sets the tree of nodes: id to flags, level, vector as the graph compares it, and key.</summary>
    public PageNumber Nodes { get; set; }

    /// <summary>Gets or sets the tree of neighbour lists: id and layer to ids.</summary>
    public PageNumber Links { get; set; }

    /// <summary>Gets or sets the tree from key to id.</summary>
    public PageNumber Keys { get; set; }

    /// <summary>Gets or sets the tree of original vectors of a quantized index: id to components.</summary>
    public PageNumber Exact { get; set; }

    /// <summary>Gets the bytes a vector takes in the graph.</summary>
    public int VectorBytes => Quantized ? Dimensions : Dimensions * sizeof(float);

    public static HnswHeader Create(VectorIndexOptions options) => new()
    {
        Metric = options.Metric,
        Quantized = options.Quantize,
        M = options.M,
        Dimensions = options.Dimensions,
        QuantizationMin = options.Quantize ? options.QuantizationMin : 0,
        QuantizationMax = options.Quantize ? options.QuantizationMax : 0,
    };

    public static HnswHeader Decode(ReadOnlySpan<byte> entry)
    {
        if (entry.Length != Size || entry[0] != Format || entry[1] != Trees)
        {
            throw new InvalidDataException("The catalog entry of a vector index is not one this version understands.");
        }

        return new HnswHeader
        {
            Nodes = new PageNumber(BinaryPrimitives.ReadUInt32LittleEndian(entry[2..])),
            Links = new PageNumber(BinaryPrimitives.ReadUInt32LittleEndian(entry[6..])),
            Keys = new PageNumber(BinaryPrimitives.ReadUInt32LittleEndian(entry[10..])),
            Exact = new PageNumber(BinaryPrimitives.ReadUInt32LittleEndian(entry[14..])),
            Metric = (VectorMetric)entry[DefinitionOffset],
            Quantized = (entry[DefinitionOffset + 1] & 1) != 0,
            M = entry[DefinitionOffset + 2],
            Dimensions = BinaryPrimitives.ReadUInt16LittleEndian(entry[(DefinitionOffset + 4)..]),
            QuantizationMin = BinaryPrimitives.ReadSingleLittleEndian(entry[(DefinitionOffset + 6)..]),
            QuantizationMax = BinaryPrimitives.ReadSingleLittleEndian(entry[(DefinitionOffset + 10)..]),
            NextId = BinaryPrimitives.ReadUInt32LittleEndian(entry[32..]),
            Count = BinaryPrimitives.ReadUInt32LittleEndian(entry[36..]),
            Entry = BinaryPrimitives.ReadUInt32LittleEndian(entry[40..]),
            MaxLevel = entry[44],
        };
    }

    public void Encode(Span<byte> entry)
    {
        // The format byte, the number of trees and their roots come first: the engine follows them
        // without knowing the rest (CatalogStore.TryReadVectorRoots).
        entry[..Size].Clear();
        entry[0] = Format;
        entry[1] = Trees;
        BinaryPrimitives.WriteUInt32LittleEndian(entry[2..], Nodes.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[6..], Links.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[10..], Keys.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[14..], Exact.Value);
        entry[DefinitionOffset] = (byte)Metric;
        entry[DefinitionOffset + 1] = (byte)(Quantized ? 1 : 0);
        entry[DefinitionOffset + 2] = (byte)M;
        BinaryPrimitives.WriteUInt16LittleEndian(entry[(DefinitionOffset + 4)..], (ushort)Dimensions);
        BinaryPrimitives.WriteSingleLittleEndian(entry[(DefinitionOffset + 6)..], QuantizationMin);
        BinaryPrimitives.WriteSingleLittleEndian(entry[(DefinitionOffset + 10)..], QuantizationMax);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[32..], NextId);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[36..], Count);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[40..], Entry);
        entry[44] = (byte)MaxLevel;
    }

    /// <summary>Whether the index was declared the way <paramref name="other"/> is: what cannot change once it holds vectors.</summary>
    public bool IsDeclaredAs(HnswHeader other)
    {
        Span<byte> mine = stackalloc byte[Size];
        Span<byte> theirs = stackalloc byte[Size];
        Encode(mine);
        other.Encode(theirs);
        return mine.Slice(DefinitionOffset, DefinitionSize).SequenceEqual(theirs.Slice(DefinitionOffset, DefinitionSize));
    }
}
