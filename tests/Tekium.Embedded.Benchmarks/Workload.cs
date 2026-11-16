// Copyright (c) Tekium. Licensed under the MIT License.

using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using Tekium.Embedded.Indexing;
using Tekium.Embedded.Storage;

namespace Tekium.Embedded.Benchmarks;

/// <summary>Datos y preparación comunes: las dos bases reciben exactamente las mismas filas.</summary>
internal static class Workload
{
    public const int PayloadSize = 100;

    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tekium-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>UUID v7 ascendentes, en orden de red: uno por milisegundo.</summary>
    public static byte[][] Uuid7Keys(int count)
    {
        var keys = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            keys[i] = Guid.CreateVersion7(Epoch.AddMilliseconds(i)).ToByteArray(bigEndian: true);
        }

        return keys;
    }

    public static long Ticks(int row) => Epoch.AddSeconds(row).UtcTicks;

    /// <summary>Ticks UTC big-endian con el bit de signo invertido: el orden de bytes es el orden temporal.</summary>
    public static byte[] TimestampKey(long ticks)
    {
        var key = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(key, ticks ^ long.MinValue);
        return key;
    }

    public static byte[] Payload(int seed)
    {
        var payload = new byte[PayloadSize];
        new Random(seed).NextBytes(payload);
        return payload;
    }

    /// <summary>SQLite con durabilidad equivalente a la nuestra: WAL, sync completo, y F_FULLFSYNC en macOS.</summary>
    public static SqliteConnection OpenSqlite(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA fullfsync=ON; PRAGMA cache_size=-262144;");
        return connection;
    }

    public static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static async Task FillTekiumAsync(string path, byte[][] keys, byte[] payload, int batchSize)
    {
        var pager = await Pager.CreateAsync(FileStorageDevice.Open(path, FileMode.CreateNew), PageSize.Default, default);
        await using (pager)
        {
            await InsertTekiumAsync(pager, new BTree(pager), keys, payload, batchSize);
        }
    }

    public static async ValueTask InsertTekiumAsync(Pager pager, BTree tree, byte[][] keys, byte[] payload, int batchSize)
    {
        for (var start = 0; start < keys.Length; start += batchSize)
        {
            var end = Math.Min(start + batchSize, keys.Length);
            await using var transaction = await pager.BeginWriteAsync(default);
            for (var i = start; i < end; i++)
            {
                await tree.PutAsync(transaction, keys[i], payload, default);
            }

            await transaction.CommitAsync(default);
        }
    }

    /// <summary>Abre la base y recorre todo el árbol para dejar la caché caliente.</summary>
    public static async Task<(Pager Pager, BTree Tree)> OpenTekiumWarmAsync(string path)
    {
        var pager = await Pager.OpenAsync(FileStorageDevice.Open(path, FileMode.Open), default);
        var tree = new BTree(pager);
        long bytes = 0;
        await foreach (var entry in tree.RangeAsync(default, default, default))
        {
            bytes += entry.Value.Length;
        }

        return bytes > 0 ? (pager, tree) : throw new InvalidOperationException("The benchmark database is empty.");
    }
}
