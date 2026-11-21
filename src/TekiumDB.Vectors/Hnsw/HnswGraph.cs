// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using TekiumDB.Indexing;
using TekiumDB.Storage;

namespace TekiumDB.Vectors.Hnsw;

/// <summary>
/// One operation on the graph of a vector index: a search, or the insertion of a vector. The graph is
/// kept in B+Trees of the database file, so it is copy-on-write, sees a snapshot, and is encrypted and
/// committed with everything else. Not thread-safe: one instance serves one operation at a time.
/// </summary>
/// <remarks>
/// Nodes tree: id (big-endian) to <c>[flags][level][vector][key]</c>. Links tree: id and layer to the
/// ids of the neighbours on that layer. A node is never moved or removed: a removed vector keeps its
/// node, marked, so the paths through it survive; it is only never returned nor chosen as a neighbour.
/// </remarks>
internal sealed class HnswGraph
{
    public const int NodeHeaderSize = 2;
    public const byte RemovedFlag = 1;

    private const int MaxLevel = 30;

    private readonly PriorityQueue<uint, float> _candidates = new();
    private readonly PriorityQueue<uint, float> _results = new();
    private readonly List<(uint Id, float Distance)> _found = [];
    private readonly List<(uint Id, float Distance)> _entries = [];
    private readonly List<uint> _selected = [];
    private readonly List<uint> _pool = [];
    private readonly byte[] _nodeKey = new byte[sizeof(uint)];
    private readonly byte[] _linkKey = new byte[sizeof(uint) + 1];
    private readonly float[] _query;
    private readonly float[] _base;
    private readonly float[] _decoded;
    private readonly float _scale;
    private readonly float _offset;
    private byte[] _record;
    private byte[] _links;
    private ulong[] _visited = [];
    private float[] _matrix = [];
    private float[] _distances = [];
    private int[] _order = [];
    private byte[] _flags = [];
    private int[] _selectedRanks = new int[64];
    private HnswHeader _header;
    private IPageSource _source = null!;
    private CancellationToken _cancellation;

    public HnswGraph(HnswHeader header)
    {
        _header = header;
        _query = new float[header.Dimensions];
        _base = new float[header.Dimensions];
        _decoded = new float[header.Dimensions];
        _record = new byte[NodeHeaderSize + header.VectorBytes + 64];
        _links = new byte[((2 * header.M) + 1) * sizeof(uint)];

        // A quantized component is one of 256 steps between the two ends of the range.
        _scale = (header.QuantizationMax - header.QuantizationMin) / 255f;
        _offset = header.QuantizationMin + (128f * _scale);
    }

    /// <summary>Gets the vector the operation in progress compares against: the query, or the vector being inserted.</summary>
    public float[] Query => _query;

    /// <summary>Gets the node record read last.</summary>
    public ReadOnlySpan<byte> Record => _record;

    /// <summary>The layer a node reaches, from its id alone: each layer holds about one in M of the one below.</summary>
    public static int LevelOf(uint id, int m)
    {
        // SplitMix64: the same id always gives the same level, on every machine.
        var z = (id + 1UL) * 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        var uniform = ((z >> 11) + 1) * (1.0 / (1UL << 53));
        return Math.Min((int)(-Math.Log(uniform) / Math.Log(m)), MaxLevel);
    }

    /// <summary>Starts an operation on the graph as <paramref name="header"/> describes it, read through <paramref name="source"/>.</summary>
    public void Begin(HnswHeader header, IPageSource source, CancellationToken cancellationToken)
    {
        _header = header;
        _source = source;
        _cancellation = cancellationToken;
    }

    /// <summary>Writes a vector the way the graph stores it.</summary>
    public void EncodeVector(ReadOnlySpan<float> vector, Span<byte> destination)
    {
        if (!_header.Quantized)
        {
            WriteFloats(vector, destination);
            return;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            var step = MathF.Round((Math.Clamp(vector[i], _header.QuantizationMin, _header.QuantizationMax) - _header.QuantizationMin) / _scale);
            destination[i] = (byte)(sbyte)((int)step - 128);
        }
    }

    public static void WriteFloats(ReadOnlySpan<float> vector, Span<byte> destination)
    {
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.AsBytes(vector).CopyTo(destination);
            return;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(destination[(i * sizeof(float))..], vector[i]);
        }
    }

    public static void ReadFloats(ReadOnlySpan<byte> source, Span<float> destination)
    {
        if (BitConverter.IsLittleEndian)
        {
            source[..(destination.Length * sizeof(float))].CopyTo(MemoryMarshal.AsBytes(destination));
            return;
        }

        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = BinaryPrimitives.ReadSingleLittleEndian(source[(i * sizeof(float))..]);
        }
    }

    /// <summary>The distance between two vectors by the metric of the index. Cosine assumes both are normalized.</summary>
    public float Distance(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => _header.Metric switch
    {
        VectorMetric.Euclidean => TensorPrimitives.Distance(a, b),
        VectorMetric.InnerProduct => -TensorPrimitives.Dot(a, b),
        _ => 1f - TensorPrimitives.Dot(a, b),
    };

    /// <summary>
    /// Finds the nodes nearest to <see cref="Query"/> on the lowest layer, nearest first, removed ones left
    /// out. Empty if the graph is.
    /// </summary>
    public async ValueTask<List<(uint Id, float Distance)>> SearchAsync(int ef)
    {
        _found.Clear();
        if (_header.Entry == HnswHeader.NoNode)
        {
            return _found;
        }

        var (entry, distance) = await DescendAsync(0).ConfigureAwait(false);
        _entries.Clear();
        _entries.Add((entry, distance));
        await SearchLayerAsync(_entries, ef, 0).ConfigureAwait(false);
        return _found;
    }

    /// <summary>
    /// Links the node <paramref name="id"/>, whose vector is in <see cref="Query"/> and whose record is
    /// already written, into the graph. The trees of the header are updated in place.
    /// </summary>
    public async ValueTask LinkAsync(WriteTransaction transaction, BTree tree, uint id, int level, int efConstruction)
    {
        if (_header.Entry == HnswHeader.NoNode)
        {
            _header.Entry = id;
            _header.MaxLevel = level;
            return;
        }

        var (entry, distance) = await DescendAsync(level).ConfigureAwait(false);
        _entries.Clear();
        _entries.Add((entry, distance));
        for (var layer = Math.Min(level, _header.MaxLevel); layer >= 0; layer--)
        {
            await SearchLayerAsync(_entries, efConstruction, layer).ConfigureAwait(false);
            if (_found.Count == 0)
            {
                // Everything near was removed: the node keeps the way in it came by, and no neighbours here.
                continue;
            }

            _pool.Clear();
            foreach (var (found, _) in _found)
            {
                _pool.Add(found);
            }

            _entries.Clear();
            _entries.AddRange(_found);
            Array.Copy(_query, _base, _query.Length);
            await SelectAsync(_header.M).ConfigureAwait(false);
            var neighbours = _selected.ToArray();
            await WriteLinksAsync(transaction, tree, id, layer, neighbours).ConfigureAwait(false);

            var most = layer == 0 ? 2 * _header.M : _header.M;
            foreach (var neighbour in neighbours)
            {
                var count = await ReadLinksAsync(neighbour, layer).ConfigureAwait(false);
                _pool.Clear();
                for (var i = 0; i < count; i++)
                {
                    _pool.Add(BinaryPrimitives.ReadUInt32LittleEndian(_links.AsSpan(i * sizeof(uint))));
                }

                _pool.Add(id);
                if (_pool.Count <= most)
                {
                    await WriteLinksAsync(transaction, tree, neighbour, layer, _pool).ConfigureAwait(false);
                    continue;
                }

                // Full: the neighbour keeps the ones that serve it best, which may not include the new node.
                if (!await LoadVectorAsync(neighbour, _base).ConfigureAwait(false))
                {
                    continue;
                }

                await SelectAsync(most).ConfigureAwait(false);
                await WriteLinksAsync(transaction, tree, neighbour, layer, _selected).ConfigureAwait(false);
            }
        }

        if (level > _header.MaxLevel)
        {
            _header.Entry = id;
            _header.MaxLevel = level;
        }
    }

    /// <summary>Reads the record of a node into <see cref="Record"/>. Returns its length, or -1 if there is no such node.</summary>
    public async ValueTask<int> ReadNodeAsync(uint id)
    {
        BinaryPrimitives.WriteUInt32BigEndian(_nodeKey, id);
        while (true)
        {
            var length = await BTree.GetAsync(_source, _header.Nodes, _nodeKey, _record, _cancellation).ConfigureAwait(false);
            if (!BTree.IsTooSmall(length, out var required))
            {
                return length;
            }

            _record = new byte[required];
        }
    }

    /// <summary>From the entry of the graph, moves to the nearest node on each layer above <paramref name="stopAbove"/>.</summary>
    private async ValueTask<(uint Id, float Distance)> DescendAsync(int stopAbove)
    {
        var current = _header.Entry;
        var best = await DistanceToAsync(current).ConfigureAwait(false);
        for (var layer = _header.MaxLevel; layer > stopAbove; layer--)
        {
            for (var moved = true; moved;)
            {
                moved = false;
                var count = await ReadLinksAsync(current, layer).ConfigureAwait(false);
                var ids = _links.AsMemory(0, count * sizeof(uint)).ToArray();
                for (var i = 0; i < count; i++)
                {
                    var neighbour = BinaryPrimitives.ReadUInt32LittleEndian(ids.AsSpan(i * sizeof(uint)));
                    var distance = await DistanceToAsync(neighbour).ConfigureAwait(false);
                    if (distance < best)
                    {
                        best = distance;
                        current = neighbour;
                        moved = true;
                    }
                }
            }
        }

        return (current, best);
    }

    /// <summary>
    /// The best-first search of one layer. Leaves in <see cref="_found"/> the nearest nodes that were not
    /// removed, nearest first, at most <paramref name="ef"/>. Removed nodes are walked through, not kept.
    /// </summary>
    private async ValueTask SearchLayerAsync(List<(uint Id, float Distance)> entries, int ef, int layer)
    {
        var words = (int)((_header.NextId >> 6) + 1);
        if (_visited.Length < words)
        {
            _visited = new ulong[Math.Max(words, _visited.Length * 2)];
        }
        else
        {
            Array.Clear(_visited, 0, words);
        }

        _candidates.Clear();
        _results.Clear();
        foreach (var (id, distance) in entries)
        {
            _visited[id >> 6] |= 1UL << (int)(id & 63);
            _candidates.Enqueue(id, distance);
            if (!await IsRemovedAsync(id).ConfigureAwait(false))
            {
                _results.Enqueue(id, -distance);
            }
        }

        while (_candidates.TryDequeue(out var current, out var currentDistance))
        {
            if (_results.Count >= ef && _results.TryPeek(out _, out var worst) && currentDistance > -worst)
            {
                break;
            }

            var count = await ReadLinksAsync(current, layer).ConfigureAwait(false);
            for (var i = 0; i < count; i++)
            {
                var neighbour = BinaryPrimitives.ReadUInt32LittleEndian(_links.AsSpan(i * sizeof(uint)));
                ref var word = ref _visited[neighbour >> 6];
                var bit = 1UL << (int)(neighbour & 63);
                if ((word & bit) != 0)
                {
                    continue;
                }

                word |= bit;
                var length = await ReadNodeAsync(neighbour).ConfigureAwait(false);
                if (length < 0)
                {
                    continue;
                }

                var distance = DistanceToRecord();
                var full = _results.Count >= ef;
                if (full && _results.TryPeek(out _, out var limit) && distance >= -limit)
                {
                    continue;
                }

                _candidates.Enqueue(neighbour, distance);
                if ((_record[0] & RemovedFlag) == 0)
                {
                    _results.Enqueue(neighbour, -distance);
                    if (full)
                    {
                        _ = _results.Dequeue();
                    }
                }
            }
        }

        _found.Clear();
        while (_results.TryDequeue(out var id, out var negated))
        {
            _found.Add((id, -negated));
        }

        _found.Reverse();
    }

    /// <summary>
    /// Chooses up to <paramref name="most"/> neighbours for the vector in <see cref="_base"/> among the
    /// nodes of <see cref="_pool"/>, into <see cref="_selected"/>: nearest first, skipping any that is
    /// nearer to one already chosen than to the vector itself. That keeps links pointing in different
    /// directions instead of all into the same cluster. What is left of the quota goes to the nearest skipped.
    /// </summary>
    private async ValueTask SelectAsync(int most)
    {
        var dimensions = _header.Dimensions;
        var count = _pool.Count;
        if (_matrix.Length < count * dimensions)
        {
            _matrix = new float[count * dimensions];
        }

        if (_distances.Length < count)
        {
            _distances = new float[count];
            _order = new int[count];
            _flags = new byte[count];
        }

        for (var i = 0; i < count; i++)
        {
            var usable = await LoadVectorAsync(_pool[i], _matrix.AsMemory(i * dimensions, dimensions)).ConfigureAwait(false);
            _flags[i] = (byte)(usable ? 1 : 0);
            _distances[i] = usable ? Distance(_base, _matrix.AsSpan(i * dimensions, dimensions)) : float.PositiveInfinity;
            _order[i] = i;
        }

        Array.Sort(_distances, _order, 0, count);
        _selected.Clear();
        var chosen = 0;
        for (var rank = 0; rank < count && chosen < most; rank++)
        {
            var candidate = _order[rank];
            if (_flags[candidate] == 0)
            {
                continue;
            }

            var row = _matrix.AsSpan(candidate * dimensions, dimensions);
            var keep = true;
            for (var i = 0; i < chosen && keep; i++)
            {
                keep = Distance(row, _matrix.AsSpan(_order[_selectedRanks[i]] * dimensions, dimensions)) >= _distances[rank];
            }

            if (keep)
            {
                if (_selectedRanks.Length == chosen)
                {
                    Array.Resize(ref _selectedRanks, chosen * 2);
                }

                _selectedRanks[chosen++] = rank;
                _selected.Add(_pool[candidate]);
            }
        }

        // Places left over go to the nearest of those skipped. Measured: without them, queries far from
        // the stored vectors find fewer of their true neighbours (docs/benchmarks/fase-9.md).
        for (var rank = 0; rank < count && _selected.Count < most; rank++)
        {
            var candidate = _order[rank];
            if (_flags[candidate] != 0 && !_selected.Contains(_pool[candidate]))
            {
                _selected.Add(_pool[candidate]);
            }
        }
    }

    /// <summary>Reads the vector of a node as real numbers. False if the node is missing or was removed.</summary>
    private async ValueTask<bool> LoadVectorAsync(uint id, Memory<float> destination)
    {
        var length = await ReadNodeAsync(id).ConfigureAwait(false);
        if (length < 0 || (_record[0] & RemovedFlag) != 0)
        {
            return false;
        }

        Decode(_record.AsSpan(NodeHeaderSize), destination.Span);
        return true;
    }

    private async ValueTask<bool> IsRemovedAsync(uint id)
    {
        var length = await ReadNodeAsync(id).ConfigureAwait(false);
        return length < 0 || (_record[0] & RemovedFlag) != 0;
    }

    private async ValueTask<float> DistanceToAsync(uint id)
    {
        var length = await ReadNodeAsync(id).ConfigureAwait(false);
        return length < 0 ? float.PositiveInfinity : DistanceToRecord();
    }

    private float DistanceToRecord()
    {
        var stored = _record.AsSpan(NodeHeaderSize, _header.VectorBytes);
        if (!_header.Quantized && BitConverter.IsLittleEndian)
        {
            // The stored bytes are the components themselves: compared where they are, without a copy.
            return Distance(_query, MemoryMarshal.Cast<byte, float>(stored));
        }

        Decode(stored, _decoded);
        return Distance(_query, _decoded);
    }

    private void Decode(ReadOnlySpan<byte> stored, Span<float> destination)
    {
        if (!_header.Quantized)
        {
            ReadFloats(stored, destination);
            return;
        }

        TensorPrimitives.ConvertChecked<sbyte, float>(MemoryMarshal.Cast<byte, sbyte>(stored[..destination.Length]), destination);
        TensorPrimitives.Multiply(destination, _scale, destination);
        TensorPrimitives.Add(destination, _offset, destination);
    }

    /// <summary>Reads the neighbours of a node on a layer into <see cref="_links"/>. Returns how many.</summary>
    private async ValueTask<int> ReadLinksAsync(uint id, int layer)
    {
        BinaryPrimitives.WriteUInt32BigEndian(_linkKey, id);
        _linkKey[sizeof(uint)] = (byte)layer;
        while (true)
        {
            var length = await BTree.GetAsync(_source, _header.Links, _linkKey, _links, _cancellation).ConfigureAwait(false);
            if (!BTree.IsTooSmall(length, out var required))
            {
                return Math.Max(length, 0) / sizeof(uint);
            }

            _links = new byte[required];
        }
    }

    private async ValueTask WriteLinksAsync(WriteTransaction transaction, BTree tree, uint id, int layer, IReadOnlyList<uint> neighbours)
    {
        var key = new byte[sizeof(uint) + 1];
        BinaryPrimitives.WriteUInt32BigEndian(key, id);
        key[sizeof(uint)] = (byte)layer;
        var value = new byte[neighbours.Count * sizeof(uint)];
        for (var i = 0; i < neighbours.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(i * sizeof(uint)), neighbours[i]);
        }

        _header.Links = await tree.PutAsync(transaction, _header.Links, key, value, _cancellation).ConfigureAwait(false);
    }
}
