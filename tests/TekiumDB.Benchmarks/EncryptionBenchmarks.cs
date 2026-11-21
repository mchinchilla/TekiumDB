// Copyright (c) Tekium. Licensed under the MIT License.

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using TekiumDB.Indexing;
using TekiumDB.Security;
using TekiumDB.Storage;

namespace TekiumDB.Benchmarks;

/// <summary>
/// Cifrado activado contra desactivado, en los dos caminos que lo pagan: escribir páginas en el commit
/// y leerlas de disco con la caché vacía. Con la caché caliente el cifrado no interviene.
/// La derivación de clave se deja al mínimo para medir AES-GCM y no Argon2id.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 7, invocationCount: 1)]
public class EncryptionBenchmarks
{
    private const int Rows = 1_000_000;
    private const int InsertRows = 100_000;
    private const string Password = "benchmark password";

    private static readonly PagerOptions Options =
        Workload.Options with { KeyDerivation = new Argon2Parameters(64, 1, 1) };

    private string _directory = string.Empty;
    private string _scanPath = string.Empty;
    private byte[][] _keys = [];
    private byte[] _payload = [];
    private int _run;
    private Pager? _pager;
    private BTree _tree = null!;

    [Params(false, true)]
    public bool Encrypted { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _directory = Workload.NewDirectory();
        _keys = Workload.Uuid7Keys(Rows);
        _payload = Workload.Payload(4);
        _scanPath = Path.Combine(_directory, "scan.tkdb");
        var pager = await CreateAsync(_scanPath);
        await using (pager)
        {
            await Workload.InsertTekiumAsync(pager, new BTree(pager), _keys, _payload, batchSize: 10_000);
        }
    }

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    [IterationSetup(Target = nameof(InsertOneTransaction))]
    public async Task SetupInsertAsync()
    {
        _pager = await CreateAsync(Path.Combine(_directory, $"insert-{_run++}.tkdb"));
        _tree = new BTree(_pager);
    }

    [IterationCleanup(Target = nameof(InsertOneTransaction))]
    public async Task CleanupInsertAsync()
    {
        if (_pager is not null)
        {
            await _pager.DisposeAsync();
        }
    }

    /// <summary>100 000 filas en un commit: unas 1 600 páginas cifradas y escritas.</summary>
    [Benchmark]
    public async ValueTask InsertOneTransaction()
    {
        await using var transaction = await _pager!.BeginWriteAsync(default);
        for (var i = 0; i < InsertRows; i++)
        {
            await _tree.PutAsync(transaction, _keys[i], _payload, default);
        }

        await transaction.CommitAsync(default);
    }

    /// <summary>Abrir y recorrer un millón de filas con la caché vacía: unas 16 000 páginas leídas y verificadas.</summary>
    [Benchmark]
    public async ValueTask<long> OpenAndScanEverything()
    {
        var device = FileStorageDevice.Open(_scanPath, FileMode.Open);
        var pager = Encrypted
            ? await Pager.OpenAsync(device, Options, Password, default)
            : await Pager.OpenAsync(device, Options, default);
        await using (pager)
        {
            long bytes = 0;
            await foreach (var entry in new BTree(pager).RangeAsync(default, default, default))
            {
                bytes += entry.Value.Length;
            }

            return bytes;
        }
    }

    private ValueTask<Pager> CreateAsync(string path)
    {
        var device = FileStorageDevice.Open(path, FileMode.CreateNew);
        return Encrypted
            ? Pager.CreateAsync(device, Options, Password, default)
            : Pager.CreateAsync(device, Options, default);
    }
}

/// <summary>Lo que cuesta abrir una base cifrada: una derivación Argon2id con los parámetros por defecto.</summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 7, invocationCount: 1)]
public class KeyDerivationBenchmarks
{
    private readonly byte[] _password = "benchmark password"u8.ToArray();
    private readonly byte[] _salt = new byte[16];
    private readonly byte[] _key = new byte[32];

    [Benchmark]
    public void Argon2idDefault() =>
        Argon2.Derive(Argon2Type.Argon2id, _password, _salt, default, default, Argon2Parameters.Default, _key);
}
