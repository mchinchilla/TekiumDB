// Copyright (c) Tekium. Licensed under the MIT License.

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Data.Sqlite;

namespace Tekium.Embedded.Benchmarks;

/// <summary>Una factura sin índices: lo que cuesta escribir y consultar cuando sólo existe la tabla.</summary>
[TkTable("invoices_plain")]
public sealed partial class InvoicePlain
{
    [TkKey]
    public Guid Id { get; set; }

    public int Customer { get; set; }

    public DateTimeOffset IssuedAt { get; set; }

    public decimal Total { get; set; }

    public bool Paid { get; set; }

    public string Notes { get; set; } = string.Empty;
}

/// <summary>La misma factura con tres índices: de columna, de fecha y parcial sobre las impagas.</summary>
[TkTable("invoices_indexed")]
[TkIndex(nameof(Total), Name = "unpaid", Where = nameof(IsUnpaid))]
public sealed partial class InvoiceIndexed
{
    [TkKey]
    public Guid Id { get; set; }

    [TkIndex]
    public int Customer { get; set; }

    [TkIndex]
    public DateTimeOffset IssuedAt { get; set; }

    public decimal Total { get; set; }

    public bool Paid { get; set; }

    public string Notes { get; set; } = string.Empty;

    public bool IsUnpaid => !Paid;
}

/// <summary>
/// Índices secundarios: cuánto encarecen la escritura y cuánto abaratan la consulta, contra recorrer la
/// tabla y contra SQLite con los mismos índices. 100 000 facturas de 1 000 clientes, una de cada diez
/// impaga, con la caché caliente.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 2, iterationCount: 9, invocationCount: 1)]
public class IndexBenchmarks
{
    private const int Rows = 100_000;
    private const int InsertRows = 10_000;
    private const int Customers = 1000;
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private string _directory = string.Empty;
    private int _run;
    private TekiumDb? _database;
    private TekiumDb? _insertInto;
    private SqliteConnection? _sqlite;
    private SqliteConnection? _sqliteInsertInto;
    private Table<InvoicePlain> _plain = null!;
    private Table<InvoiceIndexed> _indexed = null!;
    private SqliteCommand _byCustomer = null!;
    private SqliteCommand _byDate = null!;
    private SqliteCommand _unpaid = null!;

    private static (Guid Id, int Customer, DateTimeOffset IssuedAt, decimal Total, bool Paid, string Notes) Row(int i)
    {
        // Un generador congruente, para que cada fila sea la misma en todas las bases.
        var mixed = unchecked((uint)i * 2654435761u);
        return (
            Guid.CreateVersion7(Epoch.AddSeconds(i)),
            (int)(mixed % Customers),
            Epoch.AddMinutes(mixed % 525_600),
            (mixed % 1_000_000) / 100m,
            mixed % 10 != 0,
            "Invoice number " + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _directory = Workload.NewDirectory();
        _database = await TekiumDb.OpenAsync(new DbOptions { Path = Path.Combine(_directory, "query.tkdb") });
        _plain = _database.Table<InvoicePlain>();
        _indexed = _database.Table<InvoiceIndexed>();
        await InsertPlainAsync(_database, Rows);
        await InsertIndexedAsync(_database, Rows);

        _sqlite = OpenSqliteWithSchema(Path.Combine(_directory, "query.sqlite"));
        InsertSqlite(_sqlite, Rows);
        _byCustomer = Prepare("SELECT id, customer, issued, total, paid, notes FROM invoices WHERE customer = $a;");
        _byDate = Prepare("SELECT id, customer, issued, total, paid, notes FROM invoices WHERE issued BETWEEN $a AND $b;");
        _unpaid = Prepare("SELECT id, customer, issued, total, paid, notes FROM invoices WHERE paid = 0 AND total >= $a;");

        // Una pasada para dejar las cachés calientes.
        await ByCustomerScan();
        await ByCustomerIndex();
        ByCustomerSqlite();
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        _byCustomer.Dispose();
        _byDate.Dispose();
        _unpaid.Dispose();
        _sqlite?.Dispose();
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [IterationSetup(Targets = [nameof(InsertWithoutIndexes), nameof(InsertWithThreeIndexes)])]
    public async Task SetupInsertAsync() =>
        _insertInto = await TekiumDb.OpenAsync(new DbOptions { Path = Path.Combine(_directory, $"insert-{_run++}.tkdb") });

    [IterationCleanup(Targets = [nameof(InsertWithoutIndexes), nameof(InsertWithThreeIndexes)])]
    public async Task CleanupInsertAsync()
    {
        if (_insertInto is not null)
        {
            await _insertInto.DisposeAsync();
        }
    }

    [IterationSetup(Target = nameof(InsertSqliteWithThreeIndexes))]
    public void SetupSqliteInsert() =>
        _sqliteInsertInto = OpenSqliteWithSchema(Path.Combine(_directory, $"insert-{_run++}.sqlite"));

    [IterationCleanup(Target = nameof(InsertSqliteWithThreeIndexes))]
    public void CleanupSqliteInsert() => _sqliteInsertInto?.Dispose();

    /// <summary>10 000 filas en un commit, sin índices.</summary>
    [Benchmark]
    public Task InsertWithoutIndexes() => InsertPlainAsync(_insertInto!, InsertRows);

    /// <summary>10 000 filas en un commit, manteniendo tres índices.</summary>
    [Benchmark]
    public Task InsertWithThreeIndexes() => InsertIndexedAsync(_insertInto!, InsertRows);

    [Benchmark]
    public void InsertSqliteWithThreeIndexes() => InsertSqlite(_sqliteInsertInto!, InsertRows);

    /// <summary>Las facturas de un cliente (unas 100 de 100 000) recorriendo la tabla entera.</summary>
    [Benchmark]
    public async Task<int> ByCustomerScan()
    {
        var count = 0;
        await foreach (var row in _plain.QueryAsync(InvoicePlain.Fields.Customer.EqualTo(417)))
        {
            count += row.Paid ? 1 : 2;
        }

        return count;
    }

    [Benchmark]
    public async Task<int> ByCustomerIndex()
    {
        var count = 0;
        await foreach (var row in _indexed.QueryAsync(InvoiceIndexed.Fields.Customer.EqualTo(417)))
        {
            count += row.Paid ? 1 : 2;
        }

        return count;
    }

    [Benchmark]
    public int ByCustomerSqlite() => Run(_byCustomer, 417, null);

    /// <summary>Las facturas de un día (unas 270) por el índice de fecha.</summary>
    [Benchmark]
    public async Task<int> ByDateIndex()
    {
        var count = 0;
        var filter = InvoiceIndexed.Fields.IssuedAt.Between(Epoch.AddDays(100), Epoch.AddDays(101));
        await foreach (var row in _indexed.QueryAsync(filter))
        {
            count += row.Paid ? 1 : 2;
        }

        return count;
    }

    [Benchmark]
    public int ByDateSqlite() => Run(_byDate, Epoch.AddDays(100).UtcTicks, Epoch.AddDays(101).UtcTicks);

    /// <summary>Las impagas de más de 9 900 (unas 100) por el índice parcial.</summary>
    [Benchmark]
    public async Task<int> UnpaidPartialIndex()
    {
        var count = 0;
        var filter = InvoiceIndexed.Fields.Paid.EqualTo(false).And(InvoiceIndexed.Fields.Total.AtLeast(9900m));
        await foreach (var row in _indexed.QueryAsync(filter))
        {
            count += row.Paid ? 1 : 2;
        }

        return count;
    }

    [Benchmark]
    public int UnpaidSqlite() => Run(_unpaid, 9900.0, null);

    private static async Task InsertPlainAsync(TekiumDb database, int count)
    {
        var table = database.Table<InvoicePlain>();
        await using var writer = await database.BeginWriteAsync();
        for (var i = 0; i < count; i++)
        {
            var (id, customer, issuedAt, total, paid, notes) = Row(i);
            await table.InsertAsync(writer, new InvoicePlain { Id = id, Customer = customer, IssuedAt = issuedAt, Total = total, Paid = paid, Notes = notes });
        }

        await writer.CommitAsync();
    }

    private static async Task InsertIndexedAsync(TekiumDb database, int count)
    {
        var table = database.Table<InvoiceIndexed>();
        await using var writer = await database.BeginWriteAsync();
        for (var i = 0; i < count; i++)
        {
            var (id, customer, issuedAt, total, paid, notes) = Row(i);
            await table.InsertAsync(writer, new InvoiceIndexed { Id = id, Customer = customer, IssuedAt = issuedAt, Total = total, Paid = paid, Notes = notes });
        }

        await writer.CommitAsync();
    }

    private static SqliteConnection OpenSqliteWithSchema(string path)
    {
        var connection = Workload.OpenSqlite(path);
        Workload.Execute(
            connection,
            "CREATE TABLE invoices (id BLOB PRIMARY KEY, customer INTEGER NOT NULL, issued INTEGER NOT NULL, total REAL NOT NULL, paid INTEGER NOT NULL, notes TEXT NOT NULL) WITHOUT ROWID;"
            + "CREATE INDEX ix_customer ON invoices (customer);"
            + "CREATE INDEX ix_issued ON invoices (issued);"
            + "CREATE INDEX ix_unpaid ON invoices (total) WHERE paid = 0;");
        return connection;
    }

    private static void InsertSqlite(SqliteConnection connection, int count)
    {
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO invoices VALUES ($id, $customer, $issued, $total, $paid, $notes);";
        var id = insert.Parameters.Add("$id", SqliteType.Blob);
        var customer = insert.Parameters.Add("$customer", SqliteType.Integer);
        var issued = insert.Parameters.Add("$issued", SqliteType.Integer);
        var total = insert.Parameters.Add("$total", SqliteType.Real);
        var paid = insert.Parameters.Add("$paid", SqliteType.Integer);
        var notes = insert.Parameters.Add("$notes", SqliteType.Text);
        for (var i = 0; i < count; i++)
        {
            var row = Row(i);
            id.Value = row.Id.ToByteArray(bigEndian: true);
            customer.Value = row.Customer;
            issued.Value = row.IssuedAt.UtcTicks;
            total.Value = (double)row.Total;
            paid.Value = row.Paid ? 1 : 0;
            notes.Value = row.Notes;
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static int Run(SqliteCommand command, object first, object? second)
    {
        command.Parameters[0].Value = first;
        if (second is not null)
        {
            command.Parameters[1].Value = second;
        }

        // Construye el objeto entero, como hace la consulta tipada.
        var count = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new InvoicePlain
            {
                Id = new Guid(reader.GetFieldValue<byte[]>(0), bigEndian: true),
                Customer = reader.GetInt32(1),
                IssuedAt = new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero),
                Total = (decimal)reader.GetDouble(3),
                Paid = reader.GetInt64(4) != 0,
                Notes = reader.GetString(5),
            };
            count += row.Paid ? 1 : 2;
        }

        return count;
    }

    private SqliteCommand Prepare(string sql)
    {
        var command = _sqlite!.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add("$a", SqliteType.Integer);
        if (sql.Contains("$b", StringComparison.Ordinal))
        {
            command.Parameters.Add("$b", SqliteType.Integer);
        }

        command.Prepare();
        return command;
    }
}
