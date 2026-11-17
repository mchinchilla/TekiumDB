// Copyright (c) Tekium. Licensed under the MIT License.

using Dapper;
using Tekium.Embedded.Sql;

namespace Tekium.Embedded.Tests.Sql;

/// <summary>
/// Criterio de la Fase 12: Dapper corre contra el proveedor sin adaptador. Dapper sólo conoce ADO.NET:
/// crea comandos, les pone parámetros, lee con un <c>DbDataReader</c> y arma objetos por nombre de columna.
/// </summary>
public sealed class DapperTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"tekium-dapper-{Guid.NewGuid():N}");
    private TkConnection _connection = null!;

    private enum Tier
    {
        Basic = 1,
        Gold = 7,
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);

        // Por cadena de conexión, como lo haría una aplicación. Sin abrirla, Dapper la abre y la cierra en cada
        // llamada (así se crean las tablas); después se deja abierta, que es lo que pide una transacción.
        _connection = new TkConnection($"Data Source={Path.Combine(_directory, "shop.tkdb")};Cache Size=8");
        await _connection.ExecuteAsync("""
            CREATE TABLE customers (
                id UUID PRIMARY KEY, name TEXT NOT NULL, city TEXT NOT NULL, tier INT NOT NULL,
                credit DECIMAL NOT NULL, since TIMESTAMP NOT NULL, vip BOOLEAN NOT NULL, notes TEXT, photo BLOB)
            """);
        await _connection.ExecuteAsync("CREATE INDEX by_city ON customers (city)");
        await _connection.ExecuteAsync(
            "CREATE TABLE orders (id BIGINT PRIMARY KEY, customer UUID NOT NULL, total DECIMAL NOT NULL, placed TIMESTAMP NOT NULL, shipped DATETIME)");
        await _connection.ExecuteAsync("CREATE INDEX by_customer ON orders (customer)");
        Assert.Equal(System.Data.ConnectionState.Closed, _connection.State);
        await _connection.OpenAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Objects_go_in_as_parameters_and_come_back_as_objects()
    {
        var customers = Customers();

        // Una lista de objetos: Dapper ejecuta el comando una vez por cada uno.
        var inserted = await _connection.ExecuteAsync(
            "INSERT INTO customers (id, name, city, tier, credit, since, vip, notes, photo) VALUES (@Id, @Name, @City, @Tier, @Credit, @Since, @Vip, @Notes, @Photo)",
            customers);
        Assert.Equal(customers.Count, inserted);

        var all = (await _connection.QueryAsync<Customer>("SELECT * FROM customers ORDER BY name")).ToList();
        Assert.Equal(customers.OrderBy(static c => c.Name, StringComparer.Ordinal).Select(Describe), all.Select(Describe));

        // Parámetros de un objeto anónimo, de varios tipos.
        var since = customers[10].Since;
        var some = await _connection.QueryAsync<Customer>(
            "SELECT * FROM customers WHERE city = @city AND credit >= @least AND since >= @since AND vip = @vip ORDER BY name",
            new { city = "Lima", least = 100m, since, vip = true });
        Assert.Equal(
            customers.Where(c => c.City == "Lima" && c.Credit >= 100m && c.Since >= since && c.Vip).OrderBy(static c => c.Name, StringComparer.Ordinal).Select(Describe),
            some.Select(Describe));

        // Una enumeración como parámetro, una lista en un IN, un valor nulo.
        Assert.Equal(
            customers.Count(static c => c.Tier == Tier.Gold),
            await _connection.QuerySingleAsync<long>("SELECT COUNT(*) FROM customers WHERE tier = @tier", new { tier = Tier.Gold }));
        var ids = customers.Take(3).Select(static c => c.Id).ToArray();
        Assert.Equal(
            customers.Take(3).Select(static c => c.Name).Order(StringComparer.Ordinal),
            await _connection.QueryAsync<string>("SELECT name FROM customers WHERE id IN @ids ORDER BY name", new { ids }));
        Assert.Equal(customers.Count(static c => c.Notes is null), await _connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM customers WHERE notes IS NULL"));

        // Uno, ninguno, y un escalar con decimales.
        var one = await _connection.QuerySingleAsync<Customer>("SELECT * FROM customers WHERE id = @Id", new { customers[5].Id });
        Assert.Equal(Describe(customers[5]), Describe(one));
        Assert.Null(await _connection.QueryFirstOrDefaultAsync<Customer>("SELECT * FROM customers WHERE id = @id", new { id = Guid.NewGuid() }));
        Assert.Equal(customers.Sum(static c => c.Credit), await _connection.ExecuteScalarAsync<decimal>("SELECT SUM(credit) FROM customers"));

        // Filas sin tipo, y columnas con otro nombre.
        var rows = (await _connection.QueryAsync("SELECT city AS place, COUNT(*) AS people FROM customers GROUP BY city ORDER BY city")).ToList();
        Assert.Equal(
            customers.GroupBy(static c => c.City).OrderBy(static g => g.Key, StringComparer.Ordinal).Select(static g => $"{g.Key}:{g.Count()}"),
            rows.Select(static row => $"{row.place}:{row.people}"));
        var tuples = await _connection.QueryAsync<(string Name, decimal Credit)>("SELECT name, credit FROM customers WHERE credit > @c ORDER BY credit DESC LIMIT 3", new { c = 50m });
        Assert.Equal(
            customers.Where(static c => c.Credit > 50m).OrderByDescending(static c => c.Credit).Take(3).Select(static c => (c.Name, c.Credit)),
            tuples);
    }

    [Fact]
    public async Task Updates_deletes_transactions_and_joins_work_as_with_any_other_provider()
    {
        var customers = Customers();
        await _connection.ExecuteAsync(
            "INSERT INTO customers (id, name, city, tier, credit, since, vip, notes, photo) VALUES (@Id, @Name, @City, @Tier, @Credit, @Since, @Vip, @Notes, @Photo)",
            customers);
        var orders = customers.SelectMany((c, i) => Enumerable.Range(0, i % 4).Select(n => new Order
        {
            Id = (i * 10) + n,
            Customer = c.Id,
            Total = 10m + (i * n),
            Placed = c.Since.AddDays(n),
            Shipped = n == 0 ? null : new DateTime(2026, 4, 1 + n, 0, 0, 0, DateTimeKind.Utc),
        })).ToList();

        // En una transacción: nada se ve desde fuera hasta confirmar, y deshacerla no deja nada.
        await using (var transaction = await _connection.BeginTransactionAsync(Ct))
        {
            await _connection.ExecuteAsync("INSERT INTO orders (id, customer, total, placed, shipped) VALUES (@Id, @Customer, @Total, @Placed, @Shipped)", orders, transaction);
            Assert.Equal(orders.Count, await _connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM orders", transaction: transaction));
            await transaction.RollbackAsync(Ct);
        }

        Assert.Equal(0, await _connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM orders"));
        await using (var transaction = await _connection.BeginTransactionAsync(Ct))
        {
            await _connection.ExecuteAsync("INSERT INTO orders (id, customer, total, placed, shipped) VALUES (@Id, @Customer, @Total, @Placed, @Shipped)", orders, transaction);
            await transaction.CommitAsync(Ct);
        }

        var stored = (await _connection.QueryAsync<Order>("SELECT * FROM orders ORDER BY id")).ToList();
        Assert.Equal(orders.Select(static o => (o.Id, o.Customer, o.Total, o.Placed, o.Shipped)), stored.Select(static o => (o.Id, o.Customer, o.Total, o.Placed, o.Shipped)));

        // Un join armado en dos objetos por Dapper.
        var joined = (await _connection.QueryAsync<Order, Customer, (long Order, string Name)>(
            "SELECT o.id, o.total, c.id, c.name FROM orders o JOIN customers c ON c.id = o.customer WHERE o.total > @least ORDER BY o.id",
            static (order, customer) => (order.Id, customer.Name),
            new { least = 30m },
            splitOn: "id")).ToList();
        Assert.Equal(
            orders.Where(static o => o.Total > 30m).OrderBy(static o => o.Id).Select(o => (o.Id, customers.Single(c => c.Id == o.Customer).Name)),
            joined);

        // UPDATE y DELETE dicen cuántas filas tocaron.
        var limaIds = customers.Where(static c => c.City == "Lima").Select(static c => c.Id).ToHashSet();
        Assert.Equal(limaIds.Count, await _connection.ExecuteAsync("UPDATE customers SET credit = credit + @bonus, notes = @note WHERE city = @city", new { bonus = 5m, note = "bonus", city = "Lima" }));
        Assert.Equal(
            customers.Where(c => limaIds.Contains(c.Id)).Sum(static c => c.Credit + 5m),
            await _connection.ExecuteScalarAsync<decimal>("SELECT SUM(credit) FROM customers WHERE notes = 'bonus'"));
        var gone = orders.Count(static o => o.Shipped is null);
        Assert.Equal(gone, await _connection.ExecuteAsync("DELETE FROM orders WHERE shipped IS NULL"));
        Assert.Equal(orders.Count - gone, await _connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM orders"));

        // El plan, leído con Dapper como cualquier otra consulta.
        var plan = (await _connection.QueryAsync<(string Plan, long? Estimated, long Actual)>("EXPLAIN SELECT * FROM customers WHERE city = 'Lima'")).ToList();
        Assert.Contains(plan, line => line.Plan.Contains("Index by_city of customers (city = 'Lima')", StringComparison.Ordinal) && line.Actual == limaIds.Count && line.Estimated > 0);
    }

    private static string Describe(Customer c) =>
        $"{c.Id}|{c.Name}|{c.City}|{c.Tier}|{c.Credit}|{c.Since:O}|{c.Vip}|{c.Notes}|{(c.Photo is null ? "-" : Convert.ToHexString(c.Photo))}";

    private static List<Customer> Customers()
    {
        var random = new Random(5);
        var cities = new[] { "Lima", "Quito", "Bogotá", "San José" };
        return [.. Enumerable.Range(0, 40).Select(i => new Customer
        {
            Id = Guid.CreateVersion7(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i)),
            Name = $"Customer {i:D2}",
            City = cities[random.Next(cities.Length)],
            Tier = random.Next(3) == 0 ? Tier.Gold : Tier.Basic,
            Credit = random.Next(50_000) / 100m,
            Since = new DateTimeOffset(2025, 1, 1, 9, 0, 0, TimeSpan.FromHours(-5)).AddDays(i * 3),
            Vip = random.Next(2) == 0,
            Notes = random.Next(3) == 0 ? null : $"note {i}",
            Photo = random.Next(4) == 0 ? [(byte)i, 1, 2, 3] : null,
        })];
    }

    private sealed class Customer
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string City { get; set; } = string.Empty;

        public Tier Tier { get; set; }

        public decimal Credit { get; set; }

        public DateTimeOffset Since { get; set; }

        public bool Vip { get; set; }

        public string? Notes { get; set; }

        public byte[]? Photo { get; set; }
    }

    private sealed class Order
    {
        public long Id { get; set; }

        public Guid Customer { get; set; }

        public decimal Total { get; set; }

        public DateTimeOffset Placed { get; set; }

        public DateTime? Shipped { get; set; }
    }
}
