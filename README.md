# TekiumDB

**An embedded database for .NET: a single file, encrypted, async end to end, and with no native binaries.**

[![English](https://img.shields.io/badge/lang-English-blue)](README.md)
[![Español](https://img.shields.io/badge/lang-Espa%C3%B1ol-success)](README.es.md)

![.NET 11](https://img.shields.io/badge/.NET-11-512BD4)
![Native AOT](https://img.shields.io/badge/Native%20AOT-compatible-blue)
![MIT License](https://img.shields.io/badge/license-MIT-green)
![Status: pre-alpha](https://img.shields.io/badge/status-pre--alpha-orange)

TekiumDB keeps everything in a single `.tkdb` file and is 100 % managed code. Its engine is a copy-on-write B+Tree with MVCC: readers never block or wait, and a commit is atomic with no WAL and no journal. On top of it there are typed tables without reflection, secondary indexes, constraints, SQL with an ADO.NET provider, LINQ, vector search, a change feed, hot backup, and tools to inspect and repair files.

> [!NOTE]
> **Pre-alpha.** There is no published NuGet package yet, and the file format may change between versions. Verified on macOS and Linux (arm64); Windows has not been verified.

## Contents

- [Why TekiumDB](#why-tekiumdb)
- [Features](#features)
- [Quick start](#quick-start)
- [API guide](#api-guide)
- [Tools](#tools)
- [Performance](#performance)
- [Architecture](#architecture)
- [Reliability](#reliability)
- [Limits](#limits)
- [Building and testing](#building-and-testing)
- [License](#license)

## Why TekiumDB

| | SQLite on .NET | TekiumDB |
| --- | --- | --- |
| Native binaries | A `.so` / `.dll` / `.dylib` per platform | None: managed code, compatible with Native AOT, single-file and mobile |
| Async | The async API is synchronous underneath | Asynchronous I/O end to end |
| Encryption | Commercial SEE, or SQLCipher | Authenticated AES-256-GCM, built in |
| Vector search | A separate native extension | HNSW in the same file and the same transaction |
| UUID v7 and dates | A `BLOB` with an index on top | First-class types, with ranges by creation time and zone maps |
| Data model | SQL over untyped rows | C# classes with serializers generated at compile time; optional SQL and LINQ over the same tables |

What TekiumDB does **not** try to do: beat SQLite at complex SQL (subqueries, outer joins, cost-based optimization). Its SQL is a subset.

## Features

**Storage**

- A single `.tkdb` file with fixed-size pages (8 KiB by default; 4, 16 or 32 KiB).
- Copy-on-write B+Tree with MVCC: one writer, unlimited readers that never block, each on an immutable snapshot.
- Atomic commit through alternating root records. If the process dies in the middle of a commit, the database opens in the previous state, with no recovery step.
- Rows of up to 256 MiB (overflow pages, transparent to the caller).
- Schema evolution without migrations: every row remembers the schema version it was written with.
- In-memory mode for tests (`InMemory = true`), and optional per-page Zstandard compression.

**Security**

- AES-256-GCM per page, using .NET's own cryptography. The authentication tag is also the checksum: corruption and tampering are detected on read.
- Key derivation with Argon2id. Changing the password rewrites only the header.
- Without encryption, every page still carries its checksum.

**Indexes and queries**

- Secondary indexes over one or several columns, over expressions, partial and unique, maintained in the same transaction as the row.
- Zone maps for date ranges over time series.
- UUID v7 as a key: full leaves, and queries by creation time with no extra column or index.
- Vector: HNSW with cosine, Euclidean and inner-product metrics, and optional `int8` quantization.
- `EXPLAIN` of the chosen plan, in the typed API and in SQL.

**Integrity**

- `CHECK`, `UNIQUE` and `FOREIGN KEY` constraints declared with attributes.
- Snapshot isolation; a transaction rolls back when its scope ends without a commit.

**Operations**

- Hot backup, integrity verification while the database is in use, and reading past states (time travel).
- Change feed: what each commit changed, for auditing, caches and your own replication.
- Bulk load with bounded memory.
- Optional counters through `System.Diagnostics.Metrics`.
- ASP.NET Core adapters: distributed cache, Data Protection keys and health checks, each in its own package.
- `tkdb`: inspect, compact, repair and change the password from the command line.

**Ecosystem**

- SQL with an ADO.NET provider (works with Dapper), LINQ and vectors, each in its own package.
- Functions and procedures written in C#, called from SQL, and procedures stored in the file (PL/Tk), off by default.
- No reflection at run time: everything that depends on the user's types is written by a source generator at compile time. Compatible with Native AOT and trimming.

## Quick start

| Package | What for |
| --- | --- |
| `Tekium.Embedded.Core` | The engine and the typed API. No run-time dependencies. Includes the source generator |
| `Tekium.Embedded.Sql` | SQL and the ADO.NET provider |
| `Tekium.Embedded.Linq` | Queries with C# predicates |
| `Tekium.Embedded.Vectors` | Vector search |
| `Tekium.Embedded.Caching` | `IDistributedCache` in a local file |
| `Tekium.Embedded.DataProtection` | ASP.NET Core Data Protection keys in the database |
| `Tekium.Embedded.HealthChecks` | The state of the database in the application's health checks |

Until packages are published, reference the projects under `src/` (see [Building and testing](#building-and-testing)).

```csharp
using Tekium.Embedded;

[TkTable("documents")]
public sealed partial class Document
{
    [TkKey]
    public Guid Id { get; set; }

    public string Title { get; set; } = "";

    public string? Summary { get; set; }      // nullable: it may have no value

    public DateTimeOffset CreatedAt { get; set; }
}

await using var db = await TekiumDb.OpenAsync(new DbOptions
{
    Path = "app.tkdb",                        // or InMemory = true, for tests
    Password = password.AsMemory(),           // optional: without a password nothing is encrypted
});

var docs = db.Table<Document>();

await using (var tx = await db.BeginWriteAsync())
{
    await docs.InsertAsync(tx, new Document { Id = Guid.CreateVersion7(), Title = "Hello", CreatedAt = DateTimeOffset.UtcNow });
    await tx.CommitAsync();                   // without a commit, leaving the scope rolls back
}

var one = await docs.GetAsync(id);            // null if it does not exist

await foreach (var doc in docs.ScanAsync())   // in key order
    Process(doc);

await using (var read = db.BeginRead())       // a view that does not change while others write
{
    var before = await docs.GetAsync(read, id);
}
```

The source generator writes the serializer of `Document` at compile time: there is no reflection at run time, which is why it works under Native AOT. If the class changes (columns added, removed or reordered) the rows already written are not touched: each one remembers the schema version it was written with.

| What | How |
| --- | --- |
| Column types | `bool`, signed and unsigned integers of 8 to 64 bits, `float`, `double`, `decimal`, `char`, `string`, `byte[]`, `Guid`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `DateOnly`, `TimeOnly`, enums, and the nullable form of all of them |
| Key | One property with `[TkKey]`: `Guid`, `int`, `long`, `uint`, `ulong` or `string` |
| Operations | `InsertAsync`, `UpdateAsync`, `UpsertAsync`, `DeleteAsync`, `GetAsync`, `ScanAsync`, `QueryAsync`, `ExplainAsync`, `BulkInsertAsync` |
| Other attributes | `[TkColumn("name")]` stores the property under another name; `[TkIgnore]` leaves it out |
| Row size | Up to 256 MiB. A row that does not fit in a quarter of a page is stored in overflow pages, without the caller noticing |

Rules of the whole API:

- Every I/O operation returns `ValueTask` or `IAsyncEnumerable<T>` and accepts a `CancellationToken`. There are no synchronous methods other than `Dispose`.
- Transactions are `IAsyncDisposable`: leaving the scope without a commit rolls back.
- There is one writer at a time (`BeginWriteAsync` waits its turn) and any number of readers, which never wait.

## API guide

### Indexes and queries

An index is declared with `[TkIndex]` and the engine maintains it on every write, inside the same transaction:

```csharp
[TkTable("orders")]
[TkIndex(nameof(Customer), nameof(CreatedAt))]                 // over several columns
[TkIndex(nameof(CreatedAt), Name = "open", Where = nameof(IsOpen))]   // partial: only open rows
public sealed partial class Order
{
    [TkKey] public Guid Id { get; set; }

    [TkIndex] public string Customer { get; set; } = "";       // over one column

    public DateTimeOffset CreatedAt { get; set; }
    public decimal Total { get; set; }
    public bool Deleted { get; set; }

    [TkIndex] public int Month => (CreatedAt.Year * 12) + CreatedAt.Month;   // over an expression

    public bool IsOpen => !Deleted && Total > 0;               // the condition of the partial index
}

var orders = db.Table<Order>();

// The generator declares one field per column in Order.Fields; conditions are joined with And.
var recent = Order.Fields.Customer.EqualTo("ana")
    .And(Order.Fields.CreatedAt.Between(from, to));

await foreach (var order in orders.QueryAsync(recent))         // uses the index Customer_CreatedAt
    Process(order);

var plan = await orders.ExplainAsync(recent);                  // IndexScan, "Customer_CreatedAt"
```

| What | How |
| --- | --- |
| Conditions | `EqualTo`, `LessThan`, `AtMost`, `GreaterThan`, `AtLeast`, `Between`, `IsNull`, `IsNotNull`, joined with `And`. Any column can be filtered on; if no index helps, the table is scanned |
| Order of values | That of `CompareTo`, with two exceptions: text compares by Unicode code point (no culture), and a row with no value satisfies no comparison other than `IsNull` |
| Expression index | `[TkIndex]` on a computed property (no `set`). It must be deterministic and depend only on the columns of its row. If its code changes, the index rebuilds itself |
| Partial index | `Where` names a `bool` property whose body compares columns with constants joined by `&&`. It is used only when the query implies that condition; if the engine cannot prove it, the index is ignored |
| Changing indexes | Declaring a new one, changing it or removing it needs no migration: the first write with the new type builds, rebuilds or drops it. Until then queries scan the table |
| Limit | The indexed values of a row plus its key cannot exceed 1/16 of a page (512 bytes with 8 KiB pages): a row beyond that is rejected with `TkIndexKeyTooLargeException` |

### Queries with LINQ

In its own package, `Tekium.Embedded.Linq`. The predicate is translated into the same filter you would write with `Fields`, nothing is compiled at run time, and it works under Native AOT:

```csharp
using Tekium.Embedded.Linq;

var recent = await orders
    .Where(o => o.Customer == customerId && o.IssuedAt >= since && !o.Paid)
    .Take(50)
    .ToListAsync();

await foreach (var order in orders.Where(o => o.Total > 1000m))
    Process(order);
```

| | |
| --- | --- |
| Supported | Comparing a stored property with a value (`==`, `<`, `<=`, `>`, `>=`), joining with `&&`, asking about nulls, `bool` flags, enums. The value can be anything that does not depend on the row, and is computed once |
| Refused | `||`, `!=`, methods on a property, comparing two properties: `NotSupportedException` when the query is built, saying which part and why |
| Does not compile | Ordering, grouping, joining: the query is neither an `IQueryable` nor an `IAsyncEnumerable`, so the compiler rejects what the engine does not do, instead of quietly doing it in memory. To do it in memory you have to ask: `AsAsyncEnumerable()` |

[Measurements and details](docs/benchmarks/fase-11.md).

### SQL and ADO.NET

In its own package, `Tekium.Embedded.Sql`: an ADO.NET provider that Dapper uses with no adapter.

```csharp
using Dapper;
using Tekium.Embedded.Sql;

await using var connection = new TkConnection("Data Source=app.tkdb;Password=…");
await connection.OpenAsync();

await connection.ExecuteAsync("CREATE TABLE orders (id BIGINT PRIMARY KEY, customer UUID NOT NULL, total DECIMAL NOT NULL)");
await connection.ExecuteAsync("INSERT INTO orders (id, customer, total) VALUES (@Id, @Customer, @Total)", orders);
var big = await connection.QueryAsync<Order>(
    "SELECT * FROM orders WHERE customer = @c AND total > @t ORDER BY total DESC LIMIT 20", new { c, t });
```

| | |
| --- | --- |
| Statements | `SELECT` with `INNER JOIN`, `WHERE`, `GROUP BY`, `HAVING`, `ORDER BY`, `LIMIT`; `INSERT`, `UPDATE`, `DELETE`; `CREATE TABLE`, `CREATE INDEX`, `CREATE VIEW`, `CREATE PROCEDURE` and their `DROP`; `CALL`; `EXPLAIN` |
| The same tables | SQL reads the tables declared by types with `[TkTable]`, and what it writes is what the typed API would write, rows and indexes. A table with expression indexes, `CHECK` or foreign keys can be read with SQL but not written |
| Views | Stored queries, read-only; they can use other views, and one that would end up using itself is rejected when created |
| `EXPLAIN` | The chosen path, with the estimated rows (from the shape of each index's tree) and the rows that came out |
| Async only | `OpenAsync`, `ExecuteReaderAsync`, `BeginTransactionAsync`…; ADO.NET's synchronous methods throw. With Dapper: `QueryAsync`, `ExecuteAsync` |
| Not available | Subqueries, `UNION`, outer joins, `ALTER TABLE`, vector search from SQL |

[Measurements and details](docs/benchmarks/fase-12.md).

### Functions and procedures in C#

SQL can call static methods of the application. A source generator writes, at compile time, the code that passes them their arguments: there is no reflection, and it works under Native AOT.

```csharp
public static class Routines
{
    [TkFunction("slugify", Deterministic = true)]
    public static string Slugify(ReadOnlySpan<char> text) { … }

    [TkFunction("abs_delta", Vectorized = true)]          // one block of rows per call
    public static void AbsDelta(ReadOnlySpan<double> a, ReadOnlySpan<double> b, Span<double> result) { … }

    [TkProcedure("archive_orders")]
    public static async ValueTask<int> ArchiveOrders(TkContext context, DateTimeOffset before, CancellationToken ct) =>
        await context.ExecuteAsync("DELETE FROM orders WHERE placed < @before",
            new Dictionary<string, object?> { ["before"] = before }, ct);
}

database.UseRoutines(TkRoutines.All);                     // TkRoutines is written by the generator

await connection.QueryAsync("SELECT slugify(name), abs_delta(price, cost) FROM products");
await connection.ExecuteAsync("CALL archive_orders(@before)", new { before });
```

| | |
| --- | --- |
| Scalar function | Takes and returns `bool`, `int`, `long`, `double`, `decimal`, `string`, `Guid`, `DateTime`, `DateTimeOffset` or `byte[]`. Given NULL for a parameter that cannot be null, it is not called and the result is NULL |
| `Deterministic` | With constant arguments it is computed once per statement; with arguments from the row the result is reused while the arguments repeat |
| `Vectorized` | Receives blocks of up to 1,024 rows as `ReadOnlySpan<T>` (`double`, `float`, `long`, `int`). Today it is **not faster** than the scalar form: the SQL executor works row by row ([measured](docs/benchmarks/fase-13.md)) |
| Procedure | Runs with `CALL`, inside the transaction of the statement: what it writes (with the typed API or with SQL) commits or rolls back with it |
| Where they live | In the application, not in the file. A query or a view that names a function the application did not register fails when it is prepared, before reading a row |
| Compile errors | What cannot be a routine (a type SQL has no value for, a private method, two with the same name) is an error `TK0019`–`TK0023` |

### Stored procedures

A procedure can also live in the file. It is written in PL/Tk, a deliberately small subset of PL/pgSQL, compiled when it is created and kept in the catalog as a program for a small virtual machine. It travels with the file: a backup or a compacted copy carries it.

```sql
CREATE PROCEDURE archive_orders(p_before TIMESTAMPTZ) RETURNS INT AS $$
DECLARE n INT := 0;
BEGIN
    FOR r IN SELECT id, total FROM orders WHERE placed < p_before LOOP
        INSERT INTO orders_archive (id, total) VALUES (r.id, r.total);
        DELETE FROM orders WHERE id = r.id;
        n := n + 1;
    END LOOP;
    RETURN n;
END $$
```

```csharp
await using var db = await TekiumDb.OpenAsync(new DbOptions { Path = "app.tkdb", AllowStoredProcedures = true });
await using var connection = new TkConnection(db);
var archived = await connection.ExecuteScalarAsync<int>("CALL archive_orders(@before)", new { before });
```

| | |
| --- | --- |
| The language | `DECLARE`, assignment (`:=`), `IF`/`ELSIF`/`ELSE`, `WHILE`, `FOR row IN SELECT … LOOP`, `RETURN`, `RAISE`, `CALL … INTO`, and `SELECT … INTO`, `INSERT`, `UPDATE`, `DELETE`. No dynamic SQL, no exception handlers, no composite types, no DDL |
| Off by default | A stored procedure is program inside a data file. Nothing in the file runs, and none can be created, unless the database is opened with `AllowStoredProcedures = true` (`Stored Procedures=true` in a connection string) |
| What it can do | Compute, run SQL in the transaction it was called in, and call other procedures: stored ones, or C# ones the application registered. It has no I/O, no network and no way to reach any other code |
| Limits | `ProcedureInstructionLimit` (10 million by default) stops a loop that never ends; `ProcedureCallDepth` (32, at most 256) stops endless recursion. Both are an error |
| When one fails | The transaction it ran in can only be rolled back: a procedure stopped halfway never leaves half its work to be committed |
| Checked when created | Unknown variables and functions, a `RETURN` with a value in a procedure that returns nothing, a variable with the same name as a column of a table its statement uses |
| A damaged program | Is verified before it runs: it is either refused, saying why, or runs within the same limits. It cannot make the engine read outside its own registers and code |
| C# or stored? | Work over data costs about the same either way (2–9 % more when stored): the time goes to the data, not to the interpreter ([measured](docs/benchmarks/fase-14.md)). Stored ones need no redeploy and are visible to every application that opens the file; C# ones are faster at pure computation and can use any library |

### Dates and time series

An index on a date orders by instant (the same moment in two time zones is a single key) and, if dates only grow, its leaves end up full. For large tables written in time order there is also a **zone map**: for each window of time, the table remembers between which keys its rows are, and a date-range query reads only that stretch.

```csharp
[TkTable("readings")]
public sealed partial class Reading
{
    [TkKey] public Guid Id { get; set; }                       // UUID v7: grows with time

    [TkZoneMap] public DateTimeOffset At { get; set; }         // one-hour windows

    public double Value { get; set; }
}

var lastDay = Reading.Fields.At.Between(now.AddDays(-1), now);
await foreach (var reading in readings.QueryAsync(lastDay))    // ZoneScan, "zone_At"
    Process(reading);
```

| | Zone map | Secondary index |
| --- | --- | --- |
| What it stores | One entry per window of time | One entry per row |
| Cost on write | Almost none: noted in memory and written at commit | One more tree to maintain on every write |
| Date range | Reads the stretch of the table sequentially | Goes to the table once per row found |
| When it helps | Rows written roughly in date order under keys that grow | Always |
| Precision | Whole windows: reads a few extra rows and discards them | Exact |

Both can be declared on the same column: the planner uses the zone map for ranges of several windows and the index for an exact date or a short range. `BucketMinutes` changes the window size. Deleting rows does not shrink the windows.

### UUID v7 keys

A UUID v7 starts with the millisecond it was created in. With `Guid.CreateVersion7()` as the key, rows are stored in creation order and "everything created between X and Y" is a range of the key: no date column or index on it is needed.

```csharp
var lastHour = Order.Fields.Id.CreatedBetween(now.AddHours(-1), now);   // KeyRange
await foreach (var order in orders.QueryAsync(lastHour))
    Process(order);

Order.Fields.Id.CreatedSince(monday);      // from a moment on
Order.Fields.Id.CreatedBefore(monday);     // before a moment
```

Moments are cut to the millisecond, like the UUID itself. The same conditions work on any `Guid` column (through its index, if it has one); on a column that does not hold UUID v7 they compare the same bytes, which say nothing about the date.

| How keys arrive | Leaf occupancy |
| --- | ---: |
| UUID v7, up to a few dozen per millisecond (the usual: one per operation) | 96–99 % |
| UUID v7 created in a tight loop, hundreds per millisecond | 68 %, like random: within a millisecond they have no order |
| UUID v4 | 69 %, and six times slower to insert |

To load many rows at once, [bulk load](#bulk-load) sorts the keys and leaves the leaves full however they arrive. [Measurements and details](docs/benchmarks/fase-8.md).

### Constraints

They are declared with attributes and the engine checks them on every write. A violation throws `TkConstraintException`, which names the constraint and the row, and aborts the transaction: it can only be rolled back.

```csharp
[TkTable("customers")]
public sealed partial class Customer
{
    [TkKey] public Guid Id { get; set; }

    [TkIndex(Unique = true)] public string Email { get; set; } = "";     // UNIQUE
}

[TkTable("invoices")]
public sealed partial class Invoice
{
    [TkKey] public long Id { get; set; }

    [TkForeignKey(typeof(Customer))] public Guid Customer { get; set; }  // FOREIGN KEY

    public decimal Total { get; set; }

    [TkCheck] public bool TotalIsNotNegative => Total >= 0;              // CHECK
}

try
{
    await invoices.InsertAsync(tx, new Invoice { Id = 1, Customer = unknown, Total = 10 });
}
catch (TkConstraintException violation)
{
    // violation.Constraint == "fk_Customer", violation.Table == "invoices", violation.Key == "1"
}
```

| Constraint | How |
| --- | --- |
| `CHECK` | `[TkCheck]` on a `bool` property computed from the columns of the row. It must be deterministic |
| `UNIQUE` | `Unique = true` on a `[TkIndex]`, over one or several columns, partial too. Rows with no value in any of its columns are exempt. The planner knows that a lookup with all its columns fixed gives at most one row (`UniqueLookup`) |
| `FOREIGN KEY` | `[TkForeignKey(typeof(Other))]` on a column of the type of the other table's key. The referred row must exist (or the column have no value), and it cannot be deleted while something refers to it. There is no cascading delete. It comes with its own index |
| Adding them later | The first write with the type that declares them checks them against the rows already there, and if one does not comply, rejects it by name |
| When they are checked | On every operation, not at commit: two rows that refer to each other are inserted with the reference empty and completed afterwards |

### Change feed

What each commit did to the rows of a table, read after the commit and outside the writer. It replaces triggers for auditing, invalidating caches or replicating.

```csharp
await using var db = await TekiumDb.OpenAsync(new DbOptions
{
    Path = "app.tkdb",
    ChangeRetention = 1000,                    // how many past commits are kept; 0 turns it off
});

await foreach (var change in db.WatchAsync<Order>(lastSeen, ct))
{
    // change.Kind: Insert | Update | Delete
    // change.Before, change.After, change.TransactionId
    await HandleAsync(change);
    lastSeen = change.TransactionId;           // save it after each transaction to resume
}
```

| What | How |
| --- | --- |
| What it reports | One change per row and commit, with the row before and after: the net effect of the transaction, not every step. In commit order and, within a commit, in key order |
| From where | From the id of the last transaction seen. `0` is the creation of the database; `db.LastTransactionId` is "from now on". It also holds after closing and reopening |
| When it ends | On cancellation. When it catches up with the writer, it waits for the next commit |
| What it costs | Kept commits do not release their pages: the file is larger, in proportion to `ChangeRetention`. The writer does not wait for the consumer |
| If the consumer falls behind | After `ChangeRetention` commits it gets `TkChangeFeedException` instead of skipping changes: it must read the table again and continue from there |

### Bulk load

For importing a table or restoring a backup. It builds the table and its indexes bottom-up, writing each page once, and never holds all the rows in memory:

```csharp
await using var tx = await db.BeginWriteAsync();
var loaded = await orders.BulkInsertAsync(tx, ReadOrdersFromCsv(path));   // IEnumerable<T> or IAsyncEnumerable<T>
await tx.CommitAsync();
```

| What | How |
| --- | --- |
| Row order | Any. If they arrive in key order they go straight into the tree, which is fastest; otherwise they are sorted first, in temporary space of the file itself |
| Memory | Bounded; it does not grow with the number of rows: finished pages are written to the file during the load and become part of the database at commit |
| Indexes and constraints | Indexes are built by sorting their entries. `CHECK`, `UNIQUE` and `FOREIGN KEY` are checked for every row |
| A table with rows | It is rewritten entirely, merged with the new ones: meant for loading, not for adding a few rows to a large table |
| If something fails | A repeated key, a violated constraint or any error leaves the transaction unable to commit: all the rows go in, or none |
| Not supported | A table with a `FOREIGN KEY` to itself |

### Vector search

In its own package, `Tekium.Embedded.Vectors`. A vector index stores vectors under a key and finds the closest to a query with an HNSW graph, which lives in the same file and changes in the same transaction as the rows:

```csharp
using Tekium.Embedded.Vectors;

var embeddings = db.VectorIndex<Guid>("doc_embeddings", new VectorIndexOptions { Dimensions = 384 });

await using (var tx = await db.BeginWriteAsync())
{
    await docs.InsertAsync(tx, doc);
    await embeddings.UpsertAsync(tx, doc.Id, vector);     // the row and its vector, or neither
    await tx.CommitAsync();
}

var hits = await embeddings.SearchAsync(queryVector, topK: 10, efSearch: 64);
foreach (var hit in hits)                                   // from closest to farthest
    Show(await docs.GetAsync(hit.Key), hit.Distance);
```

| What | How |
| --- | --- |
| Keys | `Guid`, `int`, `long` or `string`: usually the key of the row the vector belongs to |
| Metrics | `Cosine` (the default; normalizes on store), `Euclidean` and `InnerProduct`. Fixed when the index is created, like the dimensions and `M` |
| `efSearch` | How many candidates each search considers: higher finds more of the true neighbours and takes longer. Chosen per query |
| Quantization | `Quantize = true` stores each component of the graph in one byte instead of four; the original vectors are kept apart and the final candidates are re-ranked with them, so the distance returned is exact |
| Transactions | What a transaction writes it finds itself before committing, and nobody else until after; a reader keeps seeing the graph of its snapshot |
| Deleting | `RemoveAsync` stops returning the vector, but its place in the graph is not recovered until `DropAsync`, which removes the whole index and frees its pages |
| Encryption | The graph is pages like any others: encrypted if the database is |

Measured over one million vectors of 128 dimensions clustered the way embeddings are, with the default parameters ([measurements](docs/benchmarks/fase-9.md)):

| `efSearch` | Recall@10 against brute force | Mean latency |
| ---: | ---: | ---: |
| 16 | 0.79 | 0.39 ms |
| 64 (default) | 0.98 | 0.75 ms |
| 256 | 0.999 | 1.19 ms |

Recall depends on the data: with vectors that have no structure at all it is much lower, and `efSearch` and `M` have to be raised. Building the index runs at about a thousand vectors per second.

The index is not tied to a table: whoever writes the row also writes its vector, in the same transaction.

### Time travel

Reading the database as it was when a past transaction committed, as far back as the horizon reaches (`ChangeRetention`, the same as the change feed's; off by default):

```csharp
var db = await TekiumDb.OpenAsync(new DbOptions { Path = "app.tkdb", ChangeRetention = 100 });

await using var past = await db.AsOfAsync(transactionId);
var before = await orders.GetAsync(past, id);               // the row as it was then
await foreach (var order in orders.QueryAsync(past, filter)) // queries and indexes too
    Compare(order);
```

The transaction number comes from `TkWriteTransaction.TransactionId`, `LastTransactionId` and the changes of the change feed. Further back than the horizon, `TkStateUnavailableException` is thrown. While `past` lives its state is not released; when it is closed, its space is reused. The file grows with the horizon and stays that size. [Measurements and details](docs/benchmarks/fase-10.6.md).

### Hot backup

With the database open and in use. The copy has exactly what the last commit left when the backup started; what is written meanwhile does not go in, and nothing goes in halfway.

```csharp
var report = await db.BackupAsync("backup.tkdb");
Console.WriteLine($"Backed up to transaction {report.TransactionId}");

// An encrypted database is never backed up unencrypted by default:
await db.BackupAsync("backup.tkdb", new TkBackupOptions { Password = backupPassword });
```

It does not block the writer or evict the cache (it reads the file directly). Measured over one million rows, with backups running one after another: the median durable commit does not change (13.0 ms against 13.1 ms without a backup), p95 rises from 16 to 19 ms because they share the disk, and each backup takes 0.35 s. While it runs, the pages of the state being copied are not reused, so a database under heavy writing can grow during a long backup. [Measurements and details](docs/benchmarks/fase-10.2.md).

### Verification and diagnostics

```csharp
var verification = await db.VerifyAsync();        // with the database open and in use
if (!verification.IsHealthy)
    foreach (var finding in verification.Findings)
        log.Error(finding.Message);
```

`VerifyAsync` reads every page in use from the file and checks its checksum (or its authentication tag, if the database is encrypted), that every tree is well formed, and that the free-page list does not offer a page in use. It reads the disk, not the cache: it finds a damaged page even if the application holds a good copy in memory. It does not block the writer.

With `Diagnostics = true`, the database publishes counters through a `Meter` named `Tekium.Embedded`, which OpenTelemetry, `dotnet-counters` or any `MeterListener` can read:

| Instrument | What it says |
| --- | --- |
| `tekium.cache.hits`, `tekium.cache.misses` | Pages found in the cache, and read from the file |
| `tekium.commit.pages`, `tekium.commit.duration` | Pages written and time of each commit |
| `tekium.snapshots.live`, `tekium.snapshots.oldest_age` | Readers holding a state, and the age of the oldest: what prevents space from being reused |
| `tekium.tree.depth` | Levels of each table and index |

Without that option no `Meter` is created and nothing is counted: measured, a point read takes the same time. [Measurements and details](docs/benchmarks/fase-10.4.md).

### ASP.NET Core adapters

Three small packages, each with only the dependency it adapts to. The core gains none.

```csharp
await using var db = await TekiumDb.OpenAsync(new DbOptions { Path = "app.tkdb", Password = password.AsMemory() });
var keys = await TkXmlRepository.CreateAsync(db);                       // Tekium.Embedded.DataProtection

builder.Services.AddTekiumDistributedCache(_ => db);                    // Tekium.Embedded.Caching
builder.Services.AddDataProtection().PersistKeysToTekiumDb(keys);
builder.Services.AddHealthChecks().AddTekiumDb(_ => db);                // Tekium.Embedded.HealthChecks
```

| Adapter | What it does | Worth knowing |
| --- | --- | --- |
| `TkDistributedCache` | An `IDistributedCache` that survives restarts, with no server. Absolute and sliding expiration | Only the `…Async` methods work; the synchronous ones throw, because TekiumDB does no synchronous I/O. Every `SetAsync` is a durable commit. Reading a sliding entry renews it (a write) only after a tenth of its window has passed. Expired entries are never returned, and leave the file during a later write: there is no background thread |
| `TkXmlRepository` | Keeps the Data Protection key ring in the database: encrypted at rest if the database is | Data Protection's interface is synchronous, so keys are read once at start and served from memory, and a new key is written to the file right after it is stored, without blocking. If the process dies in those milliseconds the new key is lost. `FlushAsync` waits for the write and reports a failed one |
| `TkHealthCheck` | Healthy when the database is open and accepts writes. It does no I/O | With `Verify = true` it also reads and verifies every page in use: damage is unhealthy, warnings are degraded. Its cost grows with the file, so run it now and then, not every few seconds |

There is no Entity Framework Core provider.

## Tools

`tkdb` is a command-line tool for closed `.tkdb` files. None of its commands changes the file it is given, except `passwd`.

```bash
tkdb info    app.tkdb                              # what the header says; asks for no password
tkdb check   app.tkdb                              # reads every page and says what is wrong, without opening the database
tkdb compact app.tkdb -o compact.tkdb              # a copy without the space that changes left behind
tkdb repair  damaged.tkdb -o recovered.tkdb        # a copy with everything that can still be read
tkdb passwd  app.tkdb --password-env OLD --new-password-env NEW
```

```
$ tkdb check app.tkdb
app.tkdb: 4194304 bytes
Format version 2, pages of 8192 bytes, not encrypted
Root slot 0: transaction 2, 318 pages, root page 315
Root slot 1 (current): transaction 3, 443 pages, root page 440

Pages in use: 191; free: 247
  catalog: 7 entries in 1 pages
  table tickets: 5000 entries in 81 pages
  index tickets.Due: 5000 entries in 12 pages
  index tickets.Owner: 4717 entries in 24 pages, 1 DAMAGED
  index tickets.Owner_Priority: 5000 entries in 26 pages
  index tickets.TitleLower: 5000 entries in 32 pages
  index tickets.open: 5000 entries in 14 pages

ERROR: Page 57 of index tickets.Owner cannot be read: Page 57 failed its checksum: the file is corrupt.

The file has problems.

$ tkdb repair app.tkdb -o recovered.tkdb
25000 entries copied to recovered.tkdb
4194304 bytes became 1359872
Left out: index tickets.Owner

ERROR: Page 57 of index tickets.Owner cannot be read: Page 57 failed its checksum: the file is corrupt.
```

(Real output, over a test file in which one byte of page 57 was changed.)

| Command | What it does |
| --- | --- |
| `check` | Checks both copies of the header, both records of where the database is, every page in use (its checksum or its encryption tag, and that the tree is well formed) and the free-page list. It names every damaged page and the table or index it belongs to. It reads files the engine refuses to open |
| `compact` | Rebuilds every tree with full pages in a new file. It refuses if the file is damaged |
| `repair` | The same, but it copies what can be read and says what it left out. Rows in damaged pages are lost; a damaged index, or one whose table lost rows, is not copied, and the first write to that table builds it again |
| `passwd` | Changes the password in the same file: it rewrites only the header |
| Passwords | Never on the command line: from an environment variable, a file or standard input. `compact` and `repair` can give the copy another password or none, and always give it a new file key |
| Exit code | 0 nothing wrong, 1 the file has problems, 2 the command could not run |

The same is in the core, to use from code: `TkInspector.InspectAsync`, `TkMaintenance.CompactAsync` and `TkMaintenance.RepairAsync`. Compacting and repairing do not copy the history of the change feed. [Measurements and details](docs/benchmarks/fase-10.md).

## Performance

Measured with BenchmarkDotNet on a single machine (Apple M2 Max, macOS, arm64, internal SSD), against `Microsoft.Data.Sqlite` with prepared commands. They are orders of magnitude from one machine, not promises; each link leads to the exact conditions and their caveats (the measurement documents are in Spanish).

**Reads**

| Workload | SQLite | TekiumDB | |
| --- | ---: | ---: | --- |
| Read a row by its key and build the object (100,000 rows) | 2,016 ns, 670 B | 336 ns, 326 B | 6× faster ([details](docs/benchmarks/fase-5.md)) |
| Raw point read (one million rows) | 2,177 ns | 397 ns | 5.5× faster ([details](docs/benchmarks/fase-2.md)) |
| By secondary index: the ~100 invoices of a customer | 124 µs | 69 µs | 1.8× faster ([details](docs/benchmarks/fase-6.md)) |
| By secondary index: the ~270 invoices of a day | 309 µs | 149 µs | 2.1× faster |
| Date range over 10 million rows: one hour (3,601 rows) | 2.91 ms | 0.75 ms | 3.9× faster, with a zone map ([details](docs/benchmarks/fase-7.md)) |
| Date range: one day (86,401 rows) | 69.4 ms | 10.7 ms | 6.5× faster |
| Date range: thirty days (2.6 million rows) | 2,102 ms | 310 ms | 6.8× faster |

**Writes**

| Workload | SQLite | TekiumDB | |
| --- | ---: | ---: | --- |
| 100,000 rows in one transaction | 122 ms | 29.5 ms | 4.1× faster ([details](docs/benchmarks/fase-2.md)) |
| 100,000 rows in 100 durable transactions | 749 ms | 957 ms | **1.28× slower**: every commit copies the path up to the root |
| 10,000 rows with three indexes, one commit | 31.6 ms | 22.9 ms | 1.4× faster ([details](docs/benchmarks/fase-6.md)) |
| Load 10 million rows in order | 14.0 s | 1.85 s (bulk load) | 7.6× faster, with 100 MiB of memory ([details](docs/benchmarks/fase-6.8.md)) |

**Space**

| | TekiumDB | SQLite |
| --- | ---: | ---: |
| 100,000 rows after 100,000 random updates, relative to the size of the data | 1.16× | 1.22× |

Copy-on-write has a known cost: a read transaction left open prevents pages from being reused and the file grows for as long as it lives (84× in that same workload with a reader held the whole time). The counter `tekium.snapshots.oldest_age` exists to watch for it. [Details](docs/benchmarks/fase-4.md).

**Other measurements**

| What | Result |
| --- | --- |
| Vector search, one million vectors of 128 dimensions | Recall@10 of 0.98 at 0.75 ms per query ([details](docs/benchmarks/fase-9.md)) |
| Leaf occupancy with UUID v7 keys | 96–99 % ([details](docs/benchmarks/fase-8.md)) |
| Hot backup of one million rows | 0.35 s; the writer's median commit does not change ([details](docs/benchmarks/fase-10.2.md)) |
| Diagnostic counters turned off | No measurable cost on a point read ([details](docs/benchmarks/fase-10.4.md)) |
| C# function called from SQL | About 30 ns per row, the same as the expression written in SQL ([details](docs/benchmarks/fase-13.md)) |
| Stored procedure: one turn of an empty loop | About 100 ns; archiving 10,000 rows takes 2–9 % longer than the same statements from C# ([details](docs/benchmarks/fase-14.md)) |
| Per-page compression | Reduces the bytes written 3.3×, but not the size of the file: off by default ([details](docs/benchmarks/fase-5.5.md)) |
| Taking a snapshot and pinning a cached page | 15.9 ns, no allocations |

The benchmarks are reproduced with `dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- --filter '*'`.

## Architecture

Optional packages over a layered core. Arrows are dependencies: the core references none of the packages above it.

```
Tekium.Embedded.Sql          Tekium.Embedded.Linq        Tekium.Embedded.Vectors
(SQL, ADO.NET, procedures)   (C# predicates)             (HNSW + quantization)
         |                            |                            |
         v                            v                            v
+-------------------------------------------------------------------------+
| Tekium.Embedded.Core — no native dependencies, AOT compatible           |
|  Public API          TekiumDb, Table<T>, transactions, IAsyncEnumerable |
|  Catalog and tables  schemas, typed rows, planner, constraints          |
|  Indexes             B+Tree, secondary, zone maps, bulk load            |
|  MVCC                snapshots, atomic commit, history of states        |
|  Pager               page cache, free list, copy-on-write               |
|  Encryption          AES-256-GCM per page, Argon2id                     |
|  I/O                 asynchronous RandomAccess, no P/Invoke             |
+-------------------------------------------------------------------------+
```

| Decision | Consequence |
| --- | --- |
| Copy-on-write: a committed page is never modified | Readers do not block; a snapshot, a backup or a past state is just a root that is kept |
| Commit = write new pages, flush, and switch a root record | There is no WAL to replay and no checkpoint; after a power cut the database opens at the last complete commit |
| Everything is B+Trees of the same file | Indexes, catalog, zone maps and the vector graph inherit transactions, encryption and atomic commit |
| The nonce of each encrypted page is `page ‖ transaction` | A page is never encrypted twice with the same nonce |
| A source generator instead of reflection | It works under Native AOT; modelling mistakes are compile errors (`TK0001`…) |

## Reliability

How the engine is tested:

- **Against a model.** The B+Tree, the indexes, the constraints and the queries are compared with an in-memory reference implementation over random sequences of operations (a million on the tree), also verifying the structure and that no page is lost.
- **Power cuts.** The commit is interrupted before every I/O operation, losing in pieces whatever had not been flushed to disk; the database must always open in a committed state.
- **Killed processes.** A child process is killed at every point of the commit, with the same requirement.
- **Corruption.** Every page of a file is damaged, one by one: inspection always names it, without throwing, and online verification finds it even if it is cached. Every byte of a stored procedure's program is damaged too: it is refused or runs within its limits.
- **Cryptography.** Argon2id and BLAKE2b against reference vectors (RFC 9106); the rest is .NET's cryptography.
- **File and memory.** Storage tests run over both devices, with I/O fault injection.
- **Native AOT.** A program published with AOT exercises the engine, the typed tables, vectors, the tools, LINQ and SQL on every verification.
- **The tests are tested.** Defects are introduced by hand into the engine to check that some test fails.

Platforms: verified on macOS (arm64) and on Linux (arm64, container). Windows has not been verified.

## Limits

What is worth knowing before adopting it:

- **One process per file.** There is no concurrent access from several processes.
- **One writer at a time.** Writes are serialized; many small durable commits are slower than in SQLite with WAL.
- **Long-lived readers make the file grow**, and the file does not shrink by itself: it is compacted with `tkdb compact`.
- **SQL is a subset:** no subqueries, `UNION`, outer joins or `ALTER TABLE`.
- **LINQ translates or refuses:** no `||`, `!=`, ordering or grouping.
- **No triggers, materialized views, replication or cascading deletes.** Instead: declarative constraints, expression indexes and the change feed.
- **The vector index** does not recover the space of deleted vectors until it is dropped, and cannot be queried from SQL.
- **No Entity Framework Core provider.** Use the typed API, LINQ, or SQL through ADO.NET and Dapper.
- **The file format is not stable** until the first published version.

## Building and testing

Requirements: the .NET 11 SDK. Publishing with Native AOT also needs the platform's native toolchain (Xcode on macOS, `clang` on Linux, the Visual Studio C++ tools on Windows).

```bash
dotnet build -c Release                                   # warnings are errors
dotnet test -c Release                                    # every test
dotnet test --filter-class "*BTreePropertyTests"          # one class

# Native AOT
dotnet publish tests/Tekium.Embedded.AotSmoke -c Release -o artifacts/aot
./artifacts/aot/Tekium.Embedded.AotSmoke

# Benchmarks
dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- --filter '*'

# Build, tests and AOT on Linux, inside a container (requires Docker)
scripts/test-linux.sh
```

The tests use xUnit v3 on Microsoft.Testing.Platform, which is why the filters are `--filter-class` and `--filter-method` rather than `--filter`.

```
src/
  Tekium.Embedded.Core/          Engine and public API
  Tekium.Embedded.Generators/    Source generator: tables, indexes, constraints and routines
  Tekium.Embedded.Sql/           SQL, ADO.NET provider, C# routines and stored procedures
  Tekium.Embedded.Linq/          Queries with C# predicates
  Tekium.Embedded.Vectors/       HNSW vector search
  Tekium.Embedded.Caching/       IDistributedCache
  Tekium.Embedded.DataProtection/ Data Protection key repository
  Tekium.Embedded.HealthChecks/  Health check
  Tekium.Embedded.Cli/           tkdb
tests/                           Tests, benchmarks and the Native AOT verification program
docs/benchmarks/                 Measurements, with their conditions
```

## License

[MIT](LICENSE). Copyright (c) 2026 Tekium.
