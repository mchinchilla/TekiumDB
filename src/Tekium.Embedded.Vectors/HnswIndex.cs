// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics.Tensors;
using Tekium.Embedded.Catalog;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Storage;
using Tekium.Embedded.Vectors.Hnsw;

namespace Tekium.Embedded.Vectors;

/// <summary>
/// A vector index kept as an HNSW graph in the database file: a layered graph where each vector is
/// linked to its nearest ones, searched from the top layer down. Get one with
/// <see cref="VectorDatabaseExtensions.VectorIndex{TKey}(TekiumDb, string, VectorIndexOptions)"/>.
/// </summary>
/// <typeparam name="TKey">The type of the keys: <see cref="Guid"/>, <see cref="int"/>, <see cref="long"/> or <see cref="string"/>.</typeparam>
public sealed class HnswIndex<TKey> : IVectorIndex<TKey>
    where TKey : notnull
{
    private readonly ConcurrentBag<HnswGraph> _graphs = [];
    private readonly TekiumDb _database;
    private readonly string _name;
    private readonly byte[] _catalogKey;
    private readonly VectorIndexOptions _options;
    private readonly HnswHeader _declared;

    internal HnswIndex(TekiumDb database, string name, VectorIndexOptions options)
    {
        _database = database;
        _name = name;
        _catalogKey = CatalogStore.VectorIndexKey(name);
        _options = options;
        _declared = HnswHeader.Create(options);
    }

    /// <inheritdoc/>
    public async ValueTask UpsertAsync(
        TkWriteTransaction transaction, TKey key, ReadOnlyMemory<float> vector, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var stored = Prepare(vector.Span, nameof(vector));
        var keyBytes = EncodeKey(key);
        var state = await StateAsync(transaction, anyDeclaration: false, cancellationToken).ConfigureAwait(false);
        var header = state.Header;
        if (header.NextId == HnswHeader.NoNode)
        {
            throw new InvalidOperationException($"The vector index '{_name}' cannot take more vectors.");
        }

        var inner = transaction.Inner;
        var tree = _database.Tree;
        var graph = state.Graph;
        graph.Begin(header, inner, cancellationToken);
        try
        {
            state.Dirty = true;
            if (await RemoveCoreAsync(transaction, state, keyBytes, cancellationToken).ConfigureAwait(false))
            {
                header.Count--;
            }

            var id = header.NextId++;
            var level = HnswGraph.LevelOf(id, header.M);
            var record = new byte[HnswGraph.NodeHeaderSize + header.VectorBytes + keyBytes.Length];
            record[1] = (byte)level;
            graph.EncodeVector(stored, record.AsSpan(HnswGraph.NodeHeaderSize));
            keyBytes.CopyTo(record.AsSpan(HnswGraph.NodeHeaderSize + header.VectorBytes));
            var idKey = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32BigEndian(idKey, id);
            var idValue = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(idValue, id);
            header.Nodes = await tree.PutAsync(inner, header.Nodes, idKey, record, cancellationToken).ConfigureAwait(false);
            header.Keys = await tree.PutAsync(inner, header.Keys, keyBytes, idValue, cancellationToken).ConfigureAwait(false);
            if (header.Quantized)
            {
                var exact = new byte[stored.Length * sizeof(float)];
                HnswGraph.WriteFloats(stored, exact);
                header.Exact = await tree.PutAsync(inner, header.Exact, idKey, exact, cancellationToken).ConfigureAwait(false);
            }

            stored.CopyTo(graph.Query, 0);
            await graph.LinkAsync(inner, tree, id, level, _options.EfConstruction).ConfigureAwait(false);
            header.Count++;
        }
        catch
        {
            // Several trees changed and may no longer agree: the transaction can only be rolled back.
            transaction.Fail();
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask<bool> RemoveAsync(TkWriteTransaction transaction, TKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var keyBytes = EncodeKey(key);
        var state = await StateAsync(transaction, anyDeclaration: false, cancellationToken).ConfigureAwait(false);
        state.Graph.Begin(state.Header, transaction.Inner, cancellationToken);
        try
        {
            if (!await RemoveCoreAsync(transaction, state, keyBytes, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            state.Header.Count--;
            state.Dirty = true;
            return true;
        }
        catch
        {
            transaction.Fail();
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DropAsync(TkWriteTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var state = await StateAsync(transaction, anyDeclaration: true, cancellationToken).ConfigureAwait(false);
        var header = state.Header;
        var inner = transaction.Inner;
        try
        {
            state.Dirty = true;
            await BTree.DropAsync(inner, header.Nodes, cancellationToken).ConfigureAwait(false);
            header.Nodes = PageNumber.None;
            await BTree.DropAsync(inner, header.Links, cancellationToken).ConfigureAwait(false);
            header.Links = PageNumber.None;
            await BTree.DropAsync(inner, header.Keys, cancellationToken).ConfigureAwait(false);
            header.Keys = PageNumber.None;
            await BTree.DropAsync(inner, header.Exact, cancellationToken).ConfigureAwait(false);

            // An index that was never written: what this transaction writes to it next starts it again.
            state.Reset(HnswHeader.Create(_options));
        }
        catch
        {
            transaction.Fail();
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<VectorHit<TKey>>> SearchAsync(
        ReadOnlyMemory<float> query, int topK, int efSearch = 64, CancellationToken cancellationToken = default)
    {
        var read = _database.BeginRead();
        await using (read.ConfigureAwait(false))
        {
            return await SearchAsync(read, query, topK, efSearch, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<VectorHit<TKey>>> SearchAsync(
        TkTransaction transaction, ReadOnlyMemory<float> query, int topK, int efSearch = 64, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentOutOfRangeException.ThrowIfLessThan(topK, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(efSearch, 1);
        var prepared = Prepare(query.Span, nameof(query));
        if (transaction is TkWriteTransaction writer && writer.TryGetParticipant(ParticipantName, out var participant))
        {
            // The writer's own view: the graph as its transaction has it, not yet in the catalog.
            var state = (State)participant;
            EnsureDeclared(state.Header);
            return await SearchAsync(state.Graph, state.Header, transaction.Source, prepared, topK, efSearch, cancellationToken)
                .ConfigureAwait(false);
        }

        var header = await LoadAsync(transaction.Source, transaction.CatalogRoot, anyDeclaration: false, cancellationToken).ConfigureAwait(false);
        if (header is null)
        {
            return [];
        }

        if (!_graphs.TryTake(out var graph))
        {
            graph = new HnswGraph(header);
        }

        try
        {
            return await SearchAsync(graph, header, transaction.Source, prepared, topK, efSearch, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _graphs.Add(graph);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<long> CountAsync(TkTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction is TkWriteTransaction writer && writer.TryGetParticipant(ParticipantName, out var participant))
        {
            return ((State)participant).Header.Count;
        }

        var header = await LoadAsync(transaction.Source, transaction.CatalogRoot, anyDeclaration: false, cancellationToken).ConfigureAwait(false);
        return header?.Count ?? 0;
    }

    private string ParticipantName => "vector:" + _name;

    private static byte[] EncodeKey(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return VectorKey<TKey>.Encode(key);
    }

    private static async ValueTask<IReadOnlyList<VectorHit<TKey>>> SearchAsync(
        HnswGraph graph, HnswHeader header, IPageSource source, float[] query, int topK, int efSearch, CancellationToken cancellationToken)
    {
        graph.Begin(header, source, cancellationToken);
        query.CopyTo(graph.Query, 0);
        var found = await graph.SearchAsync(Math.Max(efSearch, topK)).ConfigureAwait(false);
        var ranked = found.ToArray();
        if (header.Quantized)
        {
            // The graph compared bytes: the candidates it kept are ranked again with the vectors as they were given.
            var exact = new byte[header.Dimensions * sizeof(float)];
            var original = new float[header.Dimensions];
            var idKey = new byte[sizeof(uint)];
            for (var i = 0; i < ranked.Length; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(idKey, ranked[i].Id);
                var (buffer, length) = await BTree.GetRentedAsync(source, header.Exact, idKey, exact.Length, cancellationToken).ConfigureAwait(false);
                if (buffer is null)
                {
                    continue;
                }

                try
                {
                    HnswGraph.ReadFloats(buffer.AsSpan(0, length), original);
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }

                ranked[i] = (ranked[i].Id, graph.Distance(query, original));
            }

            Array.Sort(ranked, static (a, b) => a.Distance.CompareTo(b.Distance));
        }

        var hits = new VectorHit<TKey>[Math.Min(topK, ranked.Length)];
        for (var i = 0; i < hits.Length; i++)
        {
            var length = await graph.ReadNodeAsync(ranked[i].Id).ConfigureAwait(false);
            var key = graph.Record[(HnswGraph.NodeHeaderSize + header.VectorBytes)..length];
            hits[i] = new VectorHit<TKey>(VectorKey<TKey>.Decode(key), ranked[i].Distance);
        }

        return hits;
    }

    /// <summary>Marks the node of a key as removed and forgets the key. False if the key has no vector.</summary>
    private async ValueTask<bool> RemoveCoreAsync(TkWriteTransaction transaction, State state, byte[] keyBytes, CancellationToken cancellationToken)
    {
        var header = state.Header;
        var inner = transaction.Inner;
        var idValue = new byte[sizeof(uint)];
        if (await BTree.GetAsync(inner, header.Keys, keyBytes, idValue, cancellationToken).ConfigureAwait(false) != sizeof(uint))
        {
            return false;
        }

        var id = BinaryPrimitives.ReadUInt32LittleEndian(idValue);
        var length = await state.Graph.ReadNodeAsync(id).ConfigureAwait(false);
        var record = state.Graph.Record[..length].ToArray();
        record[0] |= HnswGraph.RemovedFlag;
        var idKey = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(idKey, id);
        var tree = _database.Tree;
        header.Nodes = await tree.PutAsync(inner, header.Nodes, idKey, record, cancellationToken).ConfigureAwait(false);
        (_, header.Keys) = await tree.DeleteAsync(inner, header.Keys, keyBytes, cancellationToken).ConfigureAwait(false);
        if (header.Quantized)
        {
            (_, header.Exact) = await tree.DeleteAsync(inner, header.Exact, idKey, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Checks a vector and returns it the way the index compares it: a copy, normalized for cosine.</summary>
    private float[] Prepare(ReadOnlySpan<float> vector, string parameter)
    {
        if (vector.Length != _options.Dimensions)
        {
            throw new ArgumentException($"The vector has {vector.Length} components; the index '{_name}' holds vectors of {_options.Dimensions}.", parameter);
        }

        var copy = vector.ToArray();
        var norm = TensorPrimitives.Norm(vector);
        if (!float.IsFinite(norm))
        {
            throw new ArgumentException("Every component of the vector must be a finite number.", parameter);
        }

        if (_options.Metric == VectorMetric.Cosine)
        {
            if (norm == 0)
            {
                throw new ArgumentException("A vector of zeros has no direction: the cosine metric cannot place it.", parameter);
            }

            TensorPrimitives.Divide(copy, norm, copy);
        }

        return copy;
    }

    private async ValueTask<HnswHeader?> LoadAsync(IPageSource source, PageNumber catalogRoot, bool anyDeclaration, CancellationToken cancellationToken)
    {
        var entry = await CatalogStore.ReadAsync(source, catalogRoot, _catalogKey, cancellationToken).ConfigureAwait(false);
        if (entry.Length == 0)
        {
            return null;
        }

        var header = HnswHeader.Decode(entry);
        if (!anyDeclaration)
        {
            EnsureDeclared(header);
        }

        return header;
    }

    private void EnsureDeclared(HnswHeader header)
    {
        if (!header.IsDeclaredAs(_declared))
        {
            throw new InvalidOperationException(
                $"The vector index '{_name}' was created with other dimensions, metric, M or quantization than the ones given now.");
        }
    }

    private async ValueTask<State> StateAsync(TkWriteTransaction transaction, bool anyDeclaration, CancellationToken cancellationToken)
    {
        if (transaction.TryGetParticipant(ParticipantName, out var existing))
        {
            var known = (State)existing;
            if (!anyDeclaration)
            {
                EnsureDeclared(known.Header);
            }

            return known;
        }

        var header = await LoadAsync(transaction.Inner, transaction.CatalogRoot, anyDeclaration, cancellationToken).ConfigureAwait(false)
            ?? HnswHeader.Create(_options);
        var state = new State(_catalogKey, header);
        transaction.AddParticipant(ParticipantName, state);
        return state;
    }

    /// <summary>What a write transaction knows about the index: its header, changed in memory and written at commit.</summary>
    private sealed class State(byte[] catalogKey, HnswHeader header) : ITkCommitParticipant
    {
        public HnswHeader Header { get; private set; } = header;

        public HnswGraph Graph { get; private set; } = new(header);

        public bool Dirty { get; set; }

        /// <summary>Starts over from an empty index.</summary>
        public void Reset(HnswHeader empty)
        {
            Header = empty;
            Graph = new HnswGraph(empty);
        }

        public async ValueTask FlushAsync(TkWriteTransaction transaction, CancellationToken cancellationToken)
        {
            if (!Dirty)
            {
                return;
            }

            Dirty = false;
            if (Header.NextId == 0)
            {
                // Dropped, or never held anything: no entry at all.
                await transaction.Database.Tree.DeleteAsync(transaction.Inner, catalogKey, cancellationToken).ConfigureAwait(false);
                return;
            }

            var entry = new byte[HnswHeader.Size];
            Header.Encode(entry);
            await transaction.Database.Tree.PutAsync(transaction.Inner, catalogKey, entry, cancellationToken).ConfigureAwait(false);
        }
    }
}
