// Copyright (c) Tekium. Licensed under the MIT License.

using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;

namespace TekiumDB.Benchmarks;

/// <summary>Una fila con columnas de verdad, como la escribiría quien usa la librería.</summary>
[TkTable("customers")]
public sealed partial class Customer
{
    [TkKey]
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Email { get; set; }

    public int Orders { get; set; }

    public double Balance { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// La API tipada de punta a punta contra SQLite haciendo lo mismo: leer una fila por su clave y construir
/// el objeto, sobre 100 000 filas con la caché caliente. Mide lo que cuestan el catálogo y el serializador
/// generado encima del árbol.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class TypedTableBenchmarks
{
    private const int Rows = 100_000;
    private const int Lookups = 1000;
    private const int LookupPool = 1 << 14;

    private string _directory = string.Empty;
    private Guid[] _lookups = [];
    private byte[][] _lookupBytes = [];
    private int _position;
    private TekiumDb? _database;
    private Table<Customer> _customers = null!;
    private SqliteConnection? _sqlite;
    private SqliteCommand _select = null!;
    private SqliteParameter _id = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _directory = Workload.NewDirectory();
        var rows = Rowset(Rows);

        _database = await TekiumDb.OpenAsync(new DbOptions { Path = Path.Combine(_directory, "typed.tkdb") });
        _customers = _database.Table<Customer>();
        await using (var writer = await _database.BeginWriteAsync())
        {
            foreach (var row in rows)
            {
                await _customers.InsertAsync(writer, row);
            }

            await writer.CommitAsync();
        }

        _sqlite = Workload.OpenSqlite(Path.Combine(_directory, "typed.sqlite"));
        Workload.Execute(
            _sqlite,
            "CREATE TABLE customers (id BLOB PRIMARY KEY, name TEXT NOT NULL, email TEXT, orders INTEGER NOT NULL, balance REAL NOT NULL, created INTEGER NOT NULL) WITHOUT ROWID;");
        using (var transaction = _sqlite.BeginTransaction())
        using (var insert = _sqlite.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO customers VALUES ($id, $name, $email, $orders, $balance, $created);";
            var id = insert.Parameters.Add("$id", SqliteType.Blob);
            var name = insert.Parameters.Add("$name", SqliteType.Text);
            var email = insert.Parameters.Add("$email", SqliteType.Text);
            var orders = insert.Parameters.Add("$orders", SqliteType.Integer);
            var balance = insert.Parameters.Add("$balance", SqliteType.Real);
            var created = insert.Parameters.Add("$created", SqliteType.Integer);
            foreach (var row in rows)
            {
                id.Value = row.Id.ToByteArray(bigEndian: true);
                name.Value = row.Name;
                email.Value = (object?)row.Email ?? DBNull.Value;
                orders.Value = row.Orders;
                balance.Value = row.Balance;
                created.Value = row.CreatedAt.Ticks;
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        _select = _sqlite.CreateCommand();
        _select.CommandText = "SELECT name, email, orders, balance, created FROM customers WHERE id = $id;";
        _id = _select.Parameters.Add("$id", SqliteType.Blob);
        _select.Prepare();

        var random = new Random(42);
        _lookups = new Guid[LookupPool];
        _lookupBytes = new byte[LookupPool][];
        for (var i = 0; i < LookupPool; i++)
        {
            _lookups[i] = rows[random.Next(Rows)].Id;
            _lookupBytes[i] = _lookups[i].ToByteArray(bigEndian: true);
        }

        // Una pasada por cada base para dejar las cachés calientes.
        await Tekium();
        Sqlite();
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        _select.Dispose();
        _sqlite?.Dispose();
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        Directory.Delete(_directory, recursive: true);
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Lookups)]
    public long Sqlite()
    {
        long total = 0;
        for (var i = 0; i < Lookups; i++)
        {
            var index = _position++ & (LookupPool - 1);
            _id.Value = _lookupBytes[index];
            using var reader = _select.ExecuteReader();
            reader.Read();
            var row = new Customer
            {
                Id = _lookups[index],
                Name = reader.GetString(0),
                Email = reader.IsDBNull(1) ? null : reader.GetString(1),
                Orders = reader.GetInt32(2),
                Balance = reader.GetDouble(3),
                CreatedAt = new DateTime(reader.GetInt64(4), DateTimeKind.Utc),
            };
            total += row.Orders;
        }

        return total;
    }

    [Benchmark(OperationsPerInvoke = Lookups)]
    public async ValueTask<long> Tekium()
    {
        long total = 0;
        for (var i = 0; i < Lookups; i++)
        {
            var row = await _customers.GetAsync(_lookups[_position++ & (LookupPool - 1)]);
            total += row!.Orders;
        }

        return total;
    }

    private static Customer[] Rowset(int count)
    {
        var epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var rows = new Customer[count];
        for (var i = 0; i < count; i++)
        {
            rows[i] = new Customer
            {
                Id = Guid.CreateVersion7(epoch.AddMilliseconds(i)),
                Name = $"Customer number {i:D7}",
                Email = i % 4 == 0 ? null : $"customer{i}@example.com",
                Orders = i % 97,
                Balance = i * 1.25,
                CreatedAt = epoch.UtcDateTime.AddMinutes(i),
            };
        }

        return rows;
    }
}
