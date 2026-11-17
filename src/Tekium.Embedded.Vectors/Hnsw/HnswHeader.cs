// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Vectors.Hnsw;

/// <summary>
/// The catalog entry of a vector index: how it was declared, where its trees are and where a search
/// enters the graph. File format: nothing here changes without a new format byte.
/// </summary>
internal sealed class HnswHeader
{
    public const int Size = 45;
    public const uint NoNode = uint.MaxValue;

    private const byte Format = 1;
    private const int DefinitionSize = 16;

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
        if (entry.Length != Size || entry[0] != Format)
        {
            throw new InvalidDataException("The catalog entry of a vector index is not one this version understands.");
        }

        return new HnswHeader
        {
            Metric = (VectorMetric)entry[1],
            Quantized = (entry[2] & 1) != 0,
            M = entry[3],
            Dimensions = BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]),
            QuantizationMin = BinaryPrimitives.ReadSingleLittleEndian(entry[8..]),
            QuantizationMax = BinaryPrimitives.ReadSingleLittleEndian(entry[12..]),
            NextId = BinaryPrimitives.ReadUInt32LittleEndian(entry[16..]),
            Count = BinaryPrimitives.ReadUInt32LittleEndian(entry[20..]),
            Entry = BinaryPrimitives.ReadUInt32LittleEndian(entry[24..]),
            MaxLevel = entry[28],
            Nodes = new PageNumber(BinaryPrimitives.ReadUInt32LittleEndian(entry[29..])),
            Links = new PageNumber(BinaryPrimitives.ReadUInt32LittleEndian(entry[33..])),
            Keys = new PageNumber(BinaryPrimitives.ReadUInt32LittleEndian(entry[37..])),
            Exact = new PageNumber(BinaryPrimitives.ReadUInt32LittleEndian(entry[41..])),
        };
    }

    public void Encode(Span<byte> entry)
    {
        entry[..Size].Clear();
        entry[0] = Format;
        entry[1] = (byte)Metric;
        entry[2] = (byte)(Quantized ? 1 : 0);
        entry[3] = (byte)M;
        BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], (ushort)Dimensions);
        BinaryPrimitives.WriteSingleLittleEndian(entry[8..], QuantizationMin);
        BinaryPrimitives.WriteSingleLittleEndian(entry[12..], QuantizationMax);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], NextId);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[20..], Count);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[24..], Entry);
        entry[28] = (byte)MaxLevel;
        BinaryPrimitives.WriteUInt32LittleEndian(entry[29..], Nodes.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[33..], Links.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[37..], Keys.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[41..], Exact.Value);
    }

    /// <summary>Whether the index was declared the way <paramref name="other"/> is: what cannot change once it holds vectors.</summary>
    public bool IsDeclaredAs(HnswHeader other)
    {
        Span<byte> mine = stackalloc byte[Size];
        Span<byte> theirs = stackalloc byte[Size];
        Encode(mine);
        other.Encode(theirs);
        return mine[..DefinitionSize].SequenceEqual(theirs[..DefinitionSize]);
    }
}
