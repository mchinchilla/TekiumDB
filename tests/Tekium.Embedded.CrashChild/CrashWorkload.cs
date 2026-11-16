// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Security;
using Tekium.Embedded.Storage;

namespace Tekium.Embedded.CrashChild;

/// <summary>
/// Una secuencia fija de transacciones que depende sólo de la semilla, y el contenido exacto que la base
/// debe tener después de cada una. El proceso que muere y el que verifica construyen la misma.
/// </summary>
internal sealed class CrashWorkload
{
    private const int KeySpace = 240;
    private const int OperationsPerTransaction = 40;

    private static readonly Comparer<byte[]> ByteOrder =
        Comparer<byte[]>.Create(static (a, b) => a.AsSpan().SequenceCompareTo(b));

    private readonly int _seed;
    private readonly string? _password;
    private readonly List<SortedDictionary<byte[], byte[]>> _states = [];

    public CrashWorkload(int seed, int transactions, string? password)
    {
        _seed = seed;
        _password = password;
        Transactions = transactions;

        var model = new SortedDictionary<byte[], byte[]>(ByteOrder);
        _states.Add(new SortedDictionary<byte[], byte[]>(ByteOrder));
        for (var transaction = 0; transaction < transactions; transaction++)
        {
            foreach (var (key, value) in Operations(transaction))
            {
                if (value is null)
                {
                    model.Remove(key);
                }
                else
                {
                    model[key] = value;
                }
            }

            _states.Add(new SortedDictionary<byte[], byte[]>(model, ByteOrder));
        }
    }

    public int Transactions { get; }

    // Páginas chicas y caché chica: el árbol toma varios niveles y la free-list entra en juego pronto.
    // Argon2id al mínimo: aquí no se prueba el costo de derivar la clave.
    public PagerOptions Options { get; } = new()
    {
        PageSize = PageSize.FromBytes(4096),
        CachePages = 64,
        KeyDerivation = new Argon2Parameters(MemoryKiB: 64, Iterations: 1, Parallelism: 1),
    };

    public ValueTask<Pager> CreateAsync(IStorageDevice device, CancellationToken cancellationToken) =>
        Pager.CreateAsync(device, Options, _password, cancellationToken);

    public ValueTask<Pager> OpenAsync(IStorageDevice device, CancellationToken cancellationToken) =>
        Pager.OpenAsync(device, Options, _password, cancellationToken);

    /// <summary>Ejecuta las transacciones desde <paramref name="from"/>, avisando después de cada commit confirmado.</summary>
    public async ValueTask RunAsync(Pager pager, int from, Action<int>? committed, CancellationToken cancellationToken)
    {
        var tree = new BTree(pager);
        for (var transaction = from; transaction < Transactions; transaction++)
        {
            await using (var writer = await pager.BeginWriteAsync(cancellationToken))
            {
                foreach (var (key, value) in Operations(transaction))
                {
                    if (value is null)
                    {
                        await tree.DeleteAsync(writer, key, cancellationToken);
                    }
                    else
                    {
                        await tree.PutAsync(writer, key, value, cancellationToken);
                    }
                }

                await writer.CommitAsync(cancellationToken);
            }

            committed?.Invoke(transaction + 1);
        }
    }

    /// <summary>
    /// Dice después de cuántas transacciones la base tendría exactamente el contenido que tiene ahora,
    /// o -1 si no corresponde a ninguna: un estado intermedio o dañado.
    /// </summary>
    public async ValueTask<int> MatchAsync(Pager pager, CancellationToken cancellationToken)
    {
        using var read = pager.BeginRead();
        return await MatchAsync(pager, read.Root, cancellationToken);
    }

    /// <summary>Lo mismo, para el árbol que ve una transacción. Quien llama mantiene vivas sus páginas.</summary>
    public async ValueTask<int> MatchAsync(IPageSource source, PageNumber root, CancellationToken cancellationToken)
    {
        var entries = new List<(byte[] Key, byte[] Value)>();
        await foreach (var entry in BTree.RangeAsync(source, root, default, default, cancellationToken))
        {
            entries.Add((entry.Key.ToArray(), entry.Value.ToArray()));
        }

        for (var state = _states.Count - 1; state >= 0; state--)
        {
            if (Same(_states[state], entries))
            {
                return state;
            }
        }

        return -1;
    }

    private static bool Same(SortedDictionary<byte[], byte[]> expected, List<(byte[] Key, byte[] Value)> actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        var index = 0;
        foreach (var (key, value) in expected)
        {
            if (!key.AsSpan().SequenceEqual(actual[index].Key) || !value.AsSpan().SequenceEqual(actual[index].Value))
            {
                return false;
            }

            index++;
        }

        return true;
    }

    /// <summary>Las operaciones de una transacción: un valor nulo es un borrado.</summary>
    private IEnumerable<(byte[] Key, byte[]? Value)> Operations(int transaction)
    {
        var random = new Random((_seed * 7919) + transaction);
        for (var i = 0; i < OperationsPerTransaction; i++)
        {
            var key = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(key, random.Next(KeySpace) * 2654435761L);
            if (random.Next(4) == 0)
            {
                yield return (key, null);
                continue;
            }

            // Uno de cada ocho valores no cabe en una hoja y va a páginas de overflow.
            var value = new byte[random.Next(8) == 0 ? random.Next(2000, 9000) : random.Next(40, 700)];
            value.AsSpan().Fill((byte)random.Next(256));
            yield return (key, value);
        }
    }
}
