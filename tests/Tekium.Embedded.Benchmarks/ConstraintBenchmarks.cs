// Copyright (c) Tekium. Licensed under the MIT License.

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Data.Sqlite;

namespace Tekium.Embedded.Benchmarks;

[TkTable("clients")]
public sealed partial class Client
{
    [TkKey]
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>Una factura con una restricción de cada clase: CHECK, UNIQUE y FOREIGN KEY.</summary>
[TkTable("invoices_checked")]
public sealed partial class InvoiceChecked
{
    [TkKey]
    public Guid Id { get; set; }

    [TkForeignKey(typeof(Client))]
    public int Customer { get; set; }

    [TkIndex(Unique = true)]
    public int Number { get; set; }

    public DateTimeOffset IssuedAt { get; set; }

    public decimal Total { get; set; }

    public bool Paid { get; set; }

    public string Notes { get; set; } = string.Empty;

    [TkCheck]
    public bool TotalIsNotNegative => Total >= 0;
}

/// <summary>
/// Lo que cuestan las restricciones al escribir: 10 000 facturas en un commit con CHECK, UNIQUE y
/// FOREIGN KEY (que traen dos índices) contra la misma tabla sin nada, y contra SQLite con las mismas
/// restricciones. Aparte, muchas transacciones de una fila en memoria: lo que cuesta empezar una.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 2, iterationCount: 9, invocationCount: 1)]
public class ConstraintBenchmarks
{
    private const int InsertRows = 10_000;
    private const int Clients = 1000;
    private const int SmallTransactions = 2000;
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private string _directory = string.Empty;
    private int _run;
    private TekiumDb? _database;
    private SqliteConnection? _sqlite;

    [GlobalSetup]
    public void Setup() => _directory = Workload.NewDirectory();

    [GlobalCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [IterationSetup(Targets = [nameof(InsertWithoutConstraints), nameof(InsertWithConstraints)])]
    public async Task SetupInsertAsync()
    {
        _database = await TekiumDb.OpenAsync(new DbOptions { Path = Path.Combine(_directory, $"insert-{_run++}.tkdb") });
        await using var writer = await _database.BeginWriteAsync();
        for (var i = 0; i < Clients; i++)
        {
            await _database.Table<Client>().InsertAsync(writer, new Client { Id = i, Name = "Client" });
        }

        await writer.CommitAsync();
    }

    [IterationSetup(Target = nameof(SmallTransactionsInMemory))]
    public async Task SetupMemoryAsync() => _database = await TekiumDb.OpenAsync(new DbOptions { InMemory = true });

    [IterationCleanup(Targets = [nameof(InsertWithoutConstraints), nameof(InsertWithConstraints), nameof(SmallTransactionsInMemory)])]
    public async Task CleanupInsertAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [IterationSetup(Target = nameof(InsertSqliteWithConstraints))]
    public void SetupSqlite()
    {
        _sqlite = Workload.OpenSqlite(Path.Combine(_directory, $"insert-{_run++}.sqlite"));
        Workload.Execute(
            _sqlite,
            "PRAGMA foreign_keys=ON;"
            + "CREATE TABLE clients (id INTEGER PRIMARY KEY, name TEXT NOT NULL);"
            + "CREATE TABLE invoices (id BLOB PRIMARY KEY, customer INTEGER NOT NULL REFERENCES clients (id), number INTEGER NOT NULL UNIQUE, issued INTEGER NOT NULL, total REAL NOT NULL CHECK (total >= 0), paid INTEGER NOT NULL, notes TEXT NOT NULL) WITHOUT ROWID;"
            + "CREATE INDEX ix_customer ON invoices (customer);");
        using var transaction = _sqlite.BeginTransaction();
        using var insert = _sqlite.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO clients VALUES ($id, 'Client');";
        var id = insert.Parameters.Add("$id", SqliteType.Integer);
        for (var i = 0; i < Clients; i++)
        {
            id.Value = i;
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    [IterationCleanup(Target = nameof(InsertSqliteWithConstraints))]
    public void CleanupSqlite() => _sqlite?.Dispose();

    [Benchmark]
    public async Task InsertWithoutConstraints()
    {
        var table = _database!.Table<InvoicePlain>();
        await using var writer = await _database.BeginWriteAsync();
        for (var i = 0; i < InsertRows; i++)
        {
            var mixed = unchecked((uint)i * 2654435761u);
            await table.InsertAsync(writer, new InvoicePlain
            {
                Id = Guid.CreateVersion7(Epoch.AddSeconds(i)),
                Customer = (int)(mixed % Clients),
                IssuedAt = Epoch.AddMinutes(mixed % 525_600),
                Total = (mixed % 1_000_000) / 100m,
                Paid = mixed % 10 != 0,
                Notes = "Invoice number " + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
        }

        await writer.CommitAsync();
    }

    [Benchmark]
    public async Task InsertWithConstraints()
    {
        var table = _database!.Table<InvoiceChecked>();
        await using var writer = await _database.BeginWriteAsync();
        for (var i = 0; i < InsertRows; i++)
        {
            var mixed = unchecked((uint)i * 2654435761u);
            await table.InsertAsync(writer, new InvoiceChecked
            {
                Id = Guid.CreateVersion7(Epoch.AddSeconds(i)),
                Customer = (int)(mixed % Clients),
                Number = (int)mixed,
                IssuedAt = Epoch.AddMinutes(mixed % 525_600),
                Total = (mixed % 1_000_000) / 100m,
                Paid = mixed % 10 != 0,
                Notes = "Invoice number " + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
        }

        await writer.CommitAsync();
    }

    [Benchmark]
    public void InsertSqliteWithConstraints()
    {
        using var transaction = _sqlite!.BeginTransaction();
        using var insert = _sqlite.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO invoices VALUES ($id, $customer, $number, $issued, $total, $paid, $notes);";
        var id = insert.Parameters.Add("$id", SqliteType.Blob);
        var customer = insert.Parameters.Add("$customer", SqliteType.Integer);
        var number = insert.Parameters.Add("$number", SqliteType.Integer);
        var issued = insert.Parameters.Add("$issued", SqliteType.Integer);
        var total = insert.Parameters.Add("$total", SqliteType.Real);
        var paid = insert.Parameters.Add("$paid", SqliteType.Integer);
        var notes = insert.Parameters.Add("$notes", SqliteType.Text);
        for (var i = 0; i < InsertRows; i++)
        {
            var mixed = unchecked((uint)i * 2654435761u);
            id.Value = Guid.CreateVersion7(Epoch.AddSeconds(i)).ToByteArray(bigEndian: true);
            customer.Value = (int)(mixed % Clients);
            number.Value = (int)mixed;
            issued.Value = Epoch.AddMinutes(mixed % 525_600).UtcTicks;
            total.Value = (mixed % 1_000_000) / 100.0;
            paid.Value = mixed % 10 != 0 ? 1 : 0;
            notes.Value = "Invoice number " + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>2 000 transacciones de una fila sobre una tabla sin índices ni restricciones, en memoria.</summary>
    [Benchmark]
    public async Task SmallTransactionsInMemory()
    {
        var table = _database!.Table<InvoicePlain>();
        for (var i = 0; i < SmallTransactions; i++)
        {
            await using var writer = await _database.BeginWriteAsync();
            await table.InsertAsync(writer, new InvoicePlain { Id = Guid.CreateVersion7(Epoch.AddSeconds(i)), Customer = i, Notes = "Small" });
            await writer.CommitAsync();
        }
    }
}
