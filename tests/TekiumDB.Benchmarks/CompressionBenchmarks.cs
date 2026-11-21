// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using TekiumDB.Indexing;
using TekiumDB.Security;
using TekiumDB.Storage;

namespace TekiumDB.Benchmarks;

/// <summary>
/// Compresión Zstandard por página activada contra desactivada, con y sin cifrado, sobre filas de texto
/// parecidas a las de una aplicación (los bytes al azar del resto de los benchmarks no comprimen).
/// Mide los tres caminos que la pagan o la aprovechan: un commit grande, muchos commits chicos, y leer
/// todo de disco con la caché vacía.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 7, invocationCount: 1)]
public class CompressionBenchmarks
{
    public const int Rows = 300_000;
    private const int InsertRows = 100_000;
    private const string Password = "benchmark password";

    private string _directory = string.Empty;
    private string _scanPath = string.Empty;
    private byte[][] _keys = [];
    private byte[][] _values = [];
    private int _run;
    private Pager? _pager;
    private BTree _tree = null!;

    /// <summary>Nivel de Zstandard; 0 es sin comprimir.</summary>
    [Params(0, 1, 3)]
    public int Level { get; set; }

    [Params(false, true)]
    public bool Encrypted { get; set; }

    /// <summary>Filas con forma de registro de aplicación: campos que se repiten y valores que cambian.</summary>
    public static byte[][] TextRows(int count)
    {
        string[] cities = ["Tegucigalpa", "San Pedro Sula", "La Ceiba", "Comayagua", "Choluteca", "Santa Rosa de Copán"];
        string[] states = ["active", "suspended", "pending review", "closed"];
        var rows = new byte[count][];
        var random = new Random(77);
        for (var i = 0; i < count; i++)
        {
            rows[i] = Encoding.UTF8.GetBytes(string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"name\":\"Customer {random.Next(1_000_000):D7}\",\"city\":\"{cities[random.Next(cities.Length)]}\",\"status\":\"{states[random.Next(states.Length)]}\",\"balance\":{random.Next(10_000_000) / 100.0:F2},\"orders\":{random.Next(500)}}}"));
        }

        return rows;
    }

    internal static PagerOptions OptionsFor(int level) =>
        Workload.Options with { KeyDerivation = new Argon2Parameters(64, 1, 1), Compression = level > 0, CompressionLevel = Math.Max(level, 1) };

    internal static async Task FillAsync(Pager pager, byte[][] keys, byte[][] values, int count, int batchSize)
    {
        var tree = new BTree(pager);
        for (var start = 0; start < count; start += batchSize)
        {
            await using var transaction = await pager.BeginWriteAsync(default);
            for (var i = start; i < Math.Min(start + batchSize, count); i++)
            {
                await tree.PutAsync(transaction, keys[i], values[i], default);
            }

            await transaction.CommitAsync(default);
        }
    }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _directory = Workload.NewDirectory();
        _keys = Workload.Uuid7Keys(Rows);
        _values = TextRows(Rows);
        _scanPath = Path.Combine(_directory, "scan.tkdb");
        var pager = await CreateAsync(_scanPath);
        await using (pager)
        {
            await FillAsync(pager, _keys, _values, Rows, batchSize: 10_000);
        }
    }

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    [IterationSetup(Targets = [nameof(InsertOneTransaction), nameof(InsertInSmallTransactions)])]
    public async Task SetupInsertAsync()
    {
        _pager = await CreateAsync(Path.Combine(_directory, $"insert-{_run++}.tkdb"));
        _tree = new BTree(_pager);
    }

    [IterationCleanup(Targets = [nameof(InsertOneTransaction), nameof(InsertInSmallTransactions)])]
    public async Task CleanupInsertAsync()
    {
        if (_pager is not null)
        {
            await _pager.DisposeAsync();
        }
    }

    /// <summary>100 000 filas en un commit.</summary>
    [Benchmark]
    public ValueTask InsertOneTransaction() => new(FillAsync(_pager!, _keys, _values, InsertRows, InsertRows));

    /// <summary>100 000 filas en cien commits de mil: el costo del commit durable domina.</summary>
    [Benchmark]
    public ValueTask InsertInSmallTransactions() => new(FillAsync(_pager!, _keys, _values, InsertRows, 1000));

    /// <summary>Abrir y recorrer 300 000 filas con la caché vacía: cada página se lee, verifica y descomprime.</summary>
    [Benchmark]
    public async ValueTask<long> OpenAndScanEverything()
    {
        var device = FileStorageDevice.Open(_scanPath, FileMode.Open);
        var options = OptionsFor(0);
        var pager = Encrypted
            ? await Pager.OpenAsync(device, options, Password, default)
            : await Pager.OpenAsync(device, options, default);
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
            ? Pager.CreateAsync(device, OptionsFor(Level), Password, default)
            : Pager.CreateAsync(device, OptionsFor(Level), default);
    }
}

/// <summary>
/// El ratio: cuántos bytes ocupan de verdad las páginas dentro de sus casillas, con cada nivel de
/// compresión. No es un benchmark de tiempo: se corre con <c>-- ratio</c> y deja los archivos para
/// poder mirar con <c>du</c> cuánto disco ocupan.
/// </summary>
internal static class CompressionRatio
{
    public static async Task RunAsync()
    {
        var directory = Workload.NewDirectory();
        var keys = Workload.Uuid7Keys(CompressionBenchmarks.Rows);
        var values = CompressionBenchmarks.TextRows(CompressionBenchmarks.Rows);
        var raw = values.Sum(static value => (long)value.Length + 16);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{CompressionBenchmarks.Rows:N0} filas de texto, {raw / 1048576.0:F1} MiB de claves y valores. Archivos en {directory}"));
        Console.WriteLine("| Archivo | Páginas | Tamaño del archivo | Bytes guardados en las páginas | Ratio | Páginas que comprimieron |");
        Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: |");
        foreach (var level in new[] { 0, 1, 3, 9, 19 })
        {
            var options = CompressionBenchmarks.OptionsFor(level);
            var path = Path.Combine(directory, level == 0 ? "plain.tkdb" : $"zstd-{level}.tkdb");
            uint pageCount;
            var pager = await Pager.CreateAsync(FileStorageDevice.Open(path, FileMode.CreateNew), options, default);
            await using (pager)
            {
                await CompressionBenchmarks.FillAsync(pager, keys, values, keys.Length, batchSize: 10_000);
                pageCount = pager.Current.PageCount;
            }

            // Lo guardado de cada página: su cabecera dice cuánto mide el cuerpo comprimido, o cero.
            var bytes = await File.ReadAllBytesAsync(path);
            var pageBytes = options.PageSize.Bytes;
            long stored = 0;
            var compressed = 0;
            for (var page = 2; page < pageCount; page++)
            {
                var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((page * pageBytes) + 2));
                stored += length == 0 ? pageBytes : length + 32;
                compressed += length == 0 ? 0 : 1;
            }

            var full = (long)(pageCount - 2) * pageBytes;
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {(level == 0 ? "Sin comprimir" : $"Zstandard nivel {level}")} | {pageCount:N0} | {bytes.Length / 1048576.0:F1} MiB | {stored / 1048576.0:F1} MiB | {(double)full / stored:F2}× | {compressed:N0} |"));
        }
    }
}
