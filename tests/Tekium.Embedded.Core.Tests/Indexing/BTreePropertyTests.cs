// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using CsCheck;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Storage;
using Tekium.Embedded.Tests.Storage;

namespace Tekium.Embedded.Tests.Indexing;

/// <summary>
/// El árbol contra un modelo: la misma secuencia de operaciones se aplica al B+Tree y a un
/// <see cref="SortedDictionary{TKey,TValue}"/>, y deben coincidir siempre. Es lo que encuentra los
/// errores de split y merge que ninguna prueba de ejemplo imagina.
/// </summary>
public sealed class BTreePropertyTests
{
    // Páginas chicas y valores de hasta 900 bytes: pocas entradas por nodo, árboles profundos,
    // splits y merges constantes. Uno de cada doce valores es grande y sale a páginas de overflow.
    // Caché de 32 páginas para que el desalojo participe.
    private static readonly PagerOptions Options = new() { PageSize = PageSize.FromBytes(4096), CachePages = 32 };

    private static readonly Comparer<byte[]> ByteOrder =
        Comparer<byte[]>.Create(static (a, b) => a.AsSpan().SequenceCompareTo(b));

    private static readonly Gen<Operation> AnyOperation =
        Gen.Select(Gen.Int[0, 99], Gen.Int[0, 399], Gen.Int[0, 900], Gen.Byte, Gen.Int[0, 11])
            .Select(static (roll, key, length, fill, large) =>
                new Operation(KindOf(roll), key, large == 0 ? 1000 + (length * 12) : length, fill));

    private enum Kind
    {
        Put,
        Delete,
        Commit,
        Rollback,
        Reopen,
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Secuencias aleatorias cortas. Si una falla, CsCheck la reduce a la secuencia mínima que
    /// reproduce el error y la imprime con su semilla.
    /// </summary>
    [Fact]
    public async Task Any_sequence_of_operations_matches_the_model()
    {
        await AnyOperation.Array[1, 300].SampleAsync(
            async operations =>
            {
                var harness = await Harness.CreateAsync();
                await using (harness)
                {
                    foreach (var operation in operations)
                    {
                        await harness.ApplyAsync(operation);
                    }

                    await harness.AssertEverythingAsync();
                }
            },
            iter: 400,
            threads: 1);
    }

    /// <summary>
    /// Criterio de salida de la Fase 3: un millón de operaciones aleatorias contra el modelo, en una sola
    /// base que crece, se vacía a medias y se reabre, verificando contenido, estructura y que no se pierda
    /// ninguna página.
    /// </summary>
    [Fact]
    public async Task One_million_random_operations_match_the_model()
    {
        const int Operations = 1_000_000;
        var random = new Random(20261116);
        var harness = await Harness.CreateAsync();
        await using (harness)
        {
            for (var i = 0; i < Operations; i++)
            {
                // El espacio de claves respira: a ratos dominan las inserciones y a ratos los borrados,
                // para que el árbol crezca varios niveles y vuelva a encogerse.
                var growing = (i / 100_000) % 2 == 0;
                var roll = random.Next(1000);
                var kind = roll switch
                {
                    < 2 => Kind.Commit,
                    < 3 => Kind.Rollback,
                    _ => random.Next(100) < (growing ? 70 : 30) ? Kind.Put : Kind.Delete,
                };
                await harness.ApplyAsync(
                    new Operation(
                        kind,
                        random.Next(20_000),
                        random.Next(40) == 0 ? random.Next(1000, 9000) : random.Next(0, 300),
                        (byte)random.Next(256)));

                if (i % 100_000 == 99_999)
                {
                    await harness.ApplyAsync(new Operation(Kind.Reopen, 0, 0, 0));
                    await harness.AssertEverythingAsync();
                }
            }

            await harness.AssertEverythingAsync();
            Assert.True(harness.Deletes > 300_000 && harness.Puts > 300_000);
        }
    }

    private static Kind KindOf(int roll) => roll switch
    {
        < 45 => Kind.Put,
        < 86 => Kind.Delete,
        < 93 => Kind.Commit,
        < 97 => Kind.Rollback,
        _ => Kind.Reopen,
    };

    // Claves de longitud variable (4 a 16 bytes) cuyo orden de bytes no es el orden del entero:
    // los prefijos y las longitudes distintas ejercitan la comparación.
    private static byte[] KeyOf(int key)
    {
        var bytes = new byte[4 + (key % 13)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, key * 7919);
        bytes.AsSpan(4).Fill((byte)key);
        return bytes;
    }

    private readonly record struct Operation(Kind Kind, int Key, int Length, byte Fill);

    /// <summary>La base y el modelo, lado a lado.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly TestDevice _device = TestDevice.Memory();
        private readonly byte[] _buffer = new byte[16_384];
        private SortedDictionary<byte[], byte[]> _committed = new(ByteOrder);
        private SortedDictionary<byte[], byte[]> _working = new(ByteOrder);
        private Pager _pager = null!;
        private BTree _tree = null!;
        private WriteTransaction? _transaction;

        public int Puts { get; private set; }

        public int Deletes { get; private set; }

        public static async Task<Harness> CreateAsync()
        {
            var harness = new Harness();
            harness._pager = await Pager.CreateAsync(harness._device, Options, Ct);
            harness._tree = new BTree(harness._pager);
            return harness;
        }

        public async Task ApplyAsync(Operation operation)
        {
            switch (operation.Kind)
            {
                case Kind.Put:
                {
                    var key = KeyOf(operation.Key);
                    var value = new byte[operation.Length];
                    value.AsSpan().Fill(operation.Fill);
                    await _tree.PutAsync(await WriterAsync(), key, value, Ct);
                    _working[key] = value;
                    Puts++;
                    break;
                }

                case Kind.Delete:
                {
                    var key = KeyOf(operation.Key);
                    var removed = await _tree.DeleteAsync(await WriterAsync(), key, Ct);
                    Assert.Equal(_working.Remove(key), removed);
                    Deletes++;
                    break;
                }

                case Kind.Commit:
                    if (_transaction is not null)
                    {
                        await _transaction.CommitAsync(Ct);
                        await _transaction.DisposeAsync();
                        _transaction = null;
                        _committed = new SortedDictionary<byte[], byte[]>(_working, ByteOrder);
                    }

                    break;

                case Kind.Rollback:
                    await RollbackAsync();
                    break;

                case Kind.Reopen:
                    await RollbackAsync();
                    await _pager.DisposeAsync();
                    _pager = await Pager.OpenAsync(_device, Options, Ct);
                    _tree = new BTree(_pager);
                    break;

                default:
                    throw new InvalidOperationException();
            }
        }

        /// <summary>Contenido, orden, estructura y contabilidad de páginas, dentro y fuera de la transacción.</summary>
        public async Task AssertEverythingAsync()
        {
            if (_transaction is not null)
            {
                await AssertMatchesAsync(_transaction, _transaction.Root, _working);
                await BTreeVerifier.VerifyAsync(_transaction, _transaction.Root, Ct);
            }

            using (var read = _pager.BeginRead())
            {
                await AssertMatchesAsync(_pager, read.Root, _committed);
            }

            await BTreeVerifier.AssertNoLeaksAsync(_pager, Ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (_transaction is not null)
            {
                await _transaction.DisposeAsync();
            }

            await _pager.DisposeAsync();
            await _device.DestroyAsync();
        }

        private async Task<WriteTransaction> WriterAsync() => _transaction ??= await _pager.BeginWriteAsync(Ct);

        private async Task RollbackAsync()
        {
            if (_transaction is not null)
            {
                await _transaction.DisposeAsync();
                _transaction = null;
            }

            _working = new SortedDictionary<byte[], byte[]>(_committed, ByteOrder);
        }

        private async Task AssertMatchesAsync(IPageSource source, PageNumber root, SortedDictionary<byte[], byte[]> model)
        {
            using var expected = model.GetEnumerator();
            var count = 0;
            await foreach (var entry in BTree.RangeAsync(source, root, default, default, Ct))
            {
                Assert.True(expected.MoveNext(), "The tree has more entries than the model.");
                Assert.True(entry.Key.Span.SequenceEqual(expected.Current.Key), $"Entry {count}: the key differs from the model.");
                Assert.True(entry.Value.Span.SequenceEqual(expected.Current.Value), $"Entry {count}: the value differs from the model.");
                count++;
            }

            Assert.False(expected.MoveNext(), "The model has more entries than the tree.");

            // Las búsquedas puntuales siguen otro camino de código que el escaneo.
            var step = Math.Max(1, model.Count / 500);
            var index = 0;
            foreach (var (key, value) in model)
            {
                if (index++ % step == 0)
                {
                    var length = await BTree.GetAsync(source, root, key, _buffer, Ct);
                    Assert.True(length >= 0 && value.AsSpan().SequenceEqual(_buffer.AsSpan(0, length)), "A point lookup differs from the model.");
                }
            }
        }
    }
}
