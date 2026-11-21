# TekiumDB

**An embedded database for .NET: a single file, encrypted, async end to end, and with no native binaries.**

TekiumDB keeps everything in a single `.tkdb` file and is 100 % managed code. Its engine is a copy-on-write B+Tree with MVCC: readers never block or wait, and a commit is atomic with no WAL and no journal. On top of it there are typed tables without reflection, secondary indexes, constraints, SQL with an ADO.NET provider, LINQ, an Entity Framework Core provider, vector search, JSON documents, a change feed, time travel, hot backup, and tools to inspect and repair files.

> **Pre-alpha.** The file format may change between versions. Verified on Windows, macOS and Linux.

## Packages

| Package | What for |
| --- | --- |
| `TekiumDB` | The engine and the typed API, with its source generator. Namespace `TekiumDB` |
| `TekiumDB.Sql` | SQL and the ADO.NET provider (works with Dapper) |
| `TekiumDB.Linq` | Queries with C# predicates |
| `TekiumDB.Vectors` | HNSW vector search |
| `TekiumDB.EntityFrameworkCore` | Entity Framework Core provider, async only |
| `TekiumDB.Caching` | `IDistributedCache` in a local file |
| `TekiumDB.DataProtection` | ASP.NET Core Data Protection keys in the database |
| `TekiumDB.HealthChecks` | Health checks |
| `TekiumDB.Tool` | `tkdb`: inspect, check, compact, repair and change the password of `.tkdb` files |

```bash
dotnet add package TekiumDB              # any other package brings it too, with its source generator
dotnet tool install -g TekiumDB.Tool     # tkdb
```

## Quick start

```csharp
using TekiumDB;

[TkTable("documents")]
public sealed partial class Document
{
    [TkKey] public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

await using var db = await TekiumDb.OpenAsync(new DbOptions
{
    Path = "app.tkdb",
    Password = password.AsMemory(),           // optional: without a password nothing is encrypted
});

var docs = db.Table<Document>();

await using (var tx = await db.BeginWriteAsync())
{
    await docs.InsertAsync(tx, new Document { Id = Guid.CreateVersion7(), Title = "Hello", CreatedAt = DateTimeOffset.UtcNow });
    await tx.CommitAsync();                   // without a commit, leaving the scope rolls back
}

await foreach (var doc in docs.ScanAsync())
    Console.WriteLine(doc.Title);
```

## Documentation

- [Wiki](https://github.com/mchinchilla/TekiumDB/wiki) (English and Spanish)
- [Repository](https://github.com/mchinchilla/TekiumDB)

MIT License.
