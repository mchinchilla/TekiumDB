# TekiumDB

**Base de datos embebida para .NET: un solo archivo, cifrada, asíncrona de punta a punta y sin binarios nativos.**

[![English](https://img.shields.io/badge/lang-English-blue)](README.md)
[![Español](https://img.shields.io/badge/lang-Espa%C3%B1ol-success)](README.es.md)

![.NET 11](https://img.shields.io/badge/.NET-11-512BD4)
![Native AOT](https://img.shields.io/badge/Native%20AOT-compatible-blue)
![Licencia MIT](https://img.shields.io/badge/licencia-MIT-green)
![Estado: pre-alpha](https://img.shields.io/badge/estado-pre--alpha-orange)

TekiumDB guarda todo en un único archivo `.tkdb` y es código 100 % administrado. Su motor es un B+Tree copy-on-write con MVCC: los lectores nunca bloquean ni esperan, y el commit es atómico sin WAL ni journal. Encima hay tablas tipadas sin reflexión, índices secundarios, restricciones, SQL con proveedor ADO.NET, LINQ, búsqueda vectorial, change feed, backup en caliente y herramientas de inspección y reparación.

> [!NOTE]
> **Pre-alpha.** Todavía no hay paquete NuGet publicado y el formato de archivo puede cambiar entre versiones. Verificado en macOS y Linux (arm64); Windows no se ha verificado.

## Contenido

- [Por qué TekiumDB](#por-qué-tekiumdb)
- [Características](#características)
- [Inicio rápido](#inicio-rápido)
- [Guía de la API](#guía-de-la-api)
- [Herramientas](#herramientas)
- [Rendimiento](#rendimiento)
- [Arquitectura](#arquitectura)
- [Confiabilidad](#confiabilidad)
- [Límites](#límites)
- [Compilar y probar](#compilar-y-probar)
- [Licencia](#licencia)

## Por qué TekiumDB

| | SQLite en .NET | TekiumDB |
| --- | --- | --- |
| Binarios nativos | Un `.so` / `.dll` / `.dylib` por plataforma | Ninguno: código administrado, compatible con Native AOT, single-file y móvil |
| Async | La API asíncrona es síncrona por debajo | E/S asíncrona de punta a punta |
| Cifrado | SEE comercial o SQLCipher | AES-256-GCM autenticado, incluido |
| Búsqueda vectorial | Extensión nativa aparte | HNSW en el mismo archivo y en la misma transacción |
| UUID v7 y fechas | `BLOB` con un índice encima | Tipos de primera clase, con rangos por fecha de creación y zone maps |
| Modelo de datos | SQL sobre filas sin tipo | Clases de C# con serializadores generados en compilación; SQL y LINQ opcionales sobre las mismas tablas |

Lo que TekiumDB **no** pretende: ganarle a SQLite en SQL complejo (optimización por costos, reordenamiento de joins). Su SQL es un subconjunto.

## Características

**Almacenamiento**

- Archivo único `.tkdb` con páginas de tamaño fijo (8 KiB por defecto; 4, 16 o 32 KiB).
- B+Tree copy-on-write con MVCC: un escritor, lectores ilimitados sin bloqueo, cada uno sobre un snapshot inmutable.
- Commit atómico por registros de raíz alternados. Si el proceso muere a mitad de un commit, la base abre en el estado anterior, sin fase de recuperación.
- Filas de hasta 256 MiB (páginas de overflow, transparentes).
- Evolución de esquema sin migraciones: cada fila recuerda con qué versión se escribió.
- Modo en memoria para pruebas (`InMemory = true`) y compresión Zstandard por página, opcional.

**Seguridad**

- AES-256-GCM por página con la criptografía de .NET. La etiqueta de autenticación es también la suma de verificación: corrupción y manipulación se detectan al leer.
- Derivación de clave con Argon2id. Cambiar la contraseña reescribe sólo la cabecera.
- Sin cifrado, cada página lleva igualmente su suma de verificación.

**Índices y consultas**

- Secundarios de una o varias columnas, sobre expresiones, parciales y únicos, mantenidos en la misma transacción que la fila.
- Zone maps para rangos de fechas sobre series de tiempo.
- UUID v7 como clave: hojas llenas y consultas por fecha de creación sin columna ni índice extra.
- Vectorial: HNSW con métricas coseno, euclidiana y producto interno, y cuantización `int8` opcional.
- `EXPLAIN` del plan elegido, en la API tipada y en SQL.

**Integridad**

- Restricciones `CHECK`, `UNIQUE` y `FOREIGN KEY` declaradas con atributos.
- Aislamiento por snapshot; transacciones que se deshacen al salir del scope sin commit.

**Operación**

- Backup en caliente, verificación de integridad con la base en uso y lectura de estados pasados (viaje en el tiempo).
- Change feed: lo que cada commit cambió, para auditoría, cachés y replicación propia.
- Carga masiva con memoria acotada.
- Contadores por `System.Diagnostics.Metrics`, opcionales.
- Adaptadores para ASP.NET Core: caché distribuida, claves de Data Protection y health checks, cada uno en su paquete.
- `tkdb`: inspeccionar, compactar, reparar y cambiar la contraseña desde la línea de comandos.

**Ecosistema**

- SQL con proveedor ADO.NET (funciona con Dapper), proveedor de Entity Framework Core, LINQ y vectores, cada uno en su paquete.
- Funciones y procedimientos escritos en C#, llamados desde SQL, y procedimientos guardados en el archivo (PL/Tk), apagados por defecto.
- Sin reflexión en tiempo de ejecución: todo lo que depende de los tipos del usuario lo escribe un generador de código al compilar. Compatible con Native AOT y trimming.

## Inicio rápido

| Paquete | Para qué |
| --- | --- |
| `Tekium.Embedded.Core` | El motor y la API tipada. Sin dependencias en tiempo de ejecución. Incluye el generador de código |
| `Tekium.Embedded.Sql` | SQL y proveedor ADO.NET |
| `Tekium.Embedded.Linq` | Consultas con predicados de C# |
| `Tekium.Embedded.Vectors` | Búsqueda vectorial |
| `Tekium.Embedded.EntityFrameworkCore` | Proveedor de Entity Framework Core, sólo asíncrono. El único paquete que no es compatible con Native AOT, porque EF Core no lo es |
| `Tekium.Embedded.Caching` | `IDistributedCache` en un archivo local |
| `Tekium.Embedded.DataProtection` | Las claves de Data Protection de ASP.NET Core en la base |
| `Tekium.Embedded.HealthChecks` | El estado de la base en los health checks de la aplicación |

Mientras no haya paquetes publicados, se referencian los proyectos de `src/` (ver [Compilar y probar](#compilar-y-probar)).

```csharp
using Tekium.Embedded;

[TkTable("documents")]
public sealed partial class Document
{
    [TkKey]
    public Guid Id { get; set; }

    public string Title { get; set; } = "";

    public string? Summary { get; set; }      // anulable: puede no tener valor

    public DateTimeOffset CreatedAt { get; set; }
}

await using var db = await TekiumDb.OpenAsync(new DbOptions
{
    Path = "app.tkdb",                        // o InMemory = true, para pruebas
    Password = password.AsMemory(),           // opcional: sin contraseña no se cifra
});

var docs = db.Table<Document>();

await using (var tx = await db.BeginWriteAsync())
{
    await docs.InsertAsync(tx, new Document { Id = Guid.CreateVersion7(), Title = "Hola", CreatedAt = DateTimeOffset.UtcNow });
    await tx.CommitAsync();                   // sin commit, salir del scope hace rollback
}

var one = await docs.GetAsync(id);            // null si no existe

await foreach (var doc in docs.ScanAsync())   // en orden de clave
    Process(doc);

await using (var read = db.BeginRead())       // una vista que no cambia aunque otros escriban
{
    var before = await docs.GetAsync(read, id);
}
```

El generador de código escribe el serializador de `Document` al compilar: no hay reflexión en tiempo de ejecución, y por eso funciona con Native AOT. Si la clase cambia (columnas nuevas, quitadas o reordenadas) las filas ya escritas no se tocan: cada una recuerda con qué versión del esquema se escribió.

| Qué | Cómo |
| --- | --- |
| Tipos de columna | `bool`, enteros de 8 a 64 bits con y sin signo, `float`, `double`, `decimal`, `char`, `string`, `byte[]`, `Guid`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `DateOnly`, `TimeOnly`, enums, y la forma anulable de todos |
| Clave | Una propiedad con `[TkKey]`: `Guid`, `int`, `long`, `uint`, `ulong` o `string` |
| Operaciones | `InsertAsync`, `UpdateAsync`, `UpsertAsync`, `DeleteAsync`, `GetAsync`, `ScanAsync`, `QueryAsync`, `ExplainAsync`, `BulkInsertAsync` |
| Otros atributos | `[TkColumn("nombre")]` guarda la propiedad con otro nombre; `[TkIgnore]` la deja fuera |
| Tamaño de fila | Hasta 256 MiB. Una fila que no cabe en un cuarto de página se guarda en páginas de overflow, sin que quien la usa lo note |

Reglas de toda la API:

- Toda operación de E/S devuelve `ValueTask` o `IAsyncEnumerable<T>` y acepta `CancellationToken`. No hay métodos síncronos salvo `Dispose`.
- Las transacciones son `IAsyncDisposable`: salir del scope sin commit hace rollback.
- Hay un escritor a la vez (`BeginWriteAsync` espera su turno) y cualquier número de lectores, que nunca esperan.

## Guía de la API

### Índices y consultas

Un índice se declara con `[TkIndex]` y el motor lo mantiene en cada escritura, dentro de la misma transacción:

```csharp
[TkTable("orders")]
[TkIndex(nameof(Customer), nameof(CreatedAt))]                 // de varias columnas
[TkIndex(nameof(CreatedAt), Name = "open", Where = nameof(IsOpen))]   // parcial: sólo las filas abiertas
public sealed partial class Order
{
    [TkKey] public Guid Id { get; set; }

    [TkIndex] public string Customer { get; set; } = "";       // de una columna

    public DateTimeOffset CreatedAt { get; set; }
    public decimal Total { get; set; }
    public bool Deleted { get; set; }

    [TkIndex] public int Month => (CreatedAt.Year * 12) + CreatedAt.Month;   // sobre una expresión

    public bool IsOpen => !Deleted && Total > 0;               // la condición del índice parcial
}

var orders = db.Table<Order>();

// El generador declara un campo por columna en Order.Fields; las condiciones se unen con And.
var recent = Order.Fields.Customer.EqualTo("ana")
    .And(Order.Fields.CreatedAt.Between(from, to));

await foreach (var order in orders.QueryAsync(recent))         // usa el índice Customer_CreatedAt
    Process(order);

var plan = await orders.ExplainAsync(recent);                  // IndexScan, "Customer_CreatedAt"
```

| Qué | Cómo |
| --- | --- |
| Condiciones | `EqualTo`, `LessThan`, `AtMost`, `GreaterThan`, `AtLeast`, `Between`, `IsNull`, `IsNotNull`, unidas con `And`. Se puede filtrar por cualquier columna; si ningún índice sirve, se recorre la tabla |
| Orden de los valores | El de `CompareTo`, con dos salvedades: el texto compara por punto de código Unicode (sin cultura), y una fila sin valor no cumple ninguna comparación salvo `IsNull` |
| Índice sobre expresión | `[TkIndex]` sobre una propiedad calculada (sin `set`). Debe ser determinista y depender sólo de las columnas de su fila. Si su código cambia, el índice se reconstruye solo |
| Índice parcial | `Where` nombra una propiedad `bool` cuyo cuerpo compara columnas con constantes unidas por `&&`. Sólo se usa cuando la consulta implica esa condición; si el motor no puede probarlo, lo ignora |
| Cambios de índices | Declarar uno nuevo, cambiarlo o quitarlo no requiere migración: la primera escritura con el tipo nuevo lo construye, reconstruye o elimina. Hasta entonces las consultas recorren la tabla |
| Límite | Los valores indexados de una fila más su clave no pueden pasar de 1/16 de página (512 bytes con páginas de 8 KiB): una fila que lo exceda se rechaza con `TkIndexKeyTooLargeException` |

### Consultas con LINQ

En su propio paquete, `Tekium.Embedded.Linq`. El predicado se traduce al mismo filtro que se escribiría con `Fields`, sin compilar nada en caliente, y funciona bajo Native AOT:

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
| Se puede | Comparar una propiedad guardada con un valor (`==`, `<`, `<=`, `>`, `>=`), unir con `&&`, preguntar por nulos, banderas `bool`, enumeraciones. El valor puede ser cualquier cosa que no dependa de la fila, y se calcula una vez |
| No se puede | `||`, `!=`, métodos sobre una propiedad, comparar dos propiedades: `NotSupportedException` al construir la consulta, diciendo qué parte y por qué |
| No compila | Ordenar, agrupar, unir: la consulta no es un `IQueryable` ni un `IAsyncEnumerable`, así que el compilador rechaza lo que el motor no hace, en vez de resolverlo en memoria sin avisar. Para hacerlo en memoria hay que pedirlo: `AsAsyncEnumerable()` |

[Mediciones y detalle](docs/benchmarks/fase-11.md).

### SQL y ADO.NET

En su propio paquete, `Tekium.Embedded.Sql`: un proveedor ADO.NET que Dapper usa sin adaptador.

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
| Sentencias | `SELECT` con `INNER JOIN` y `LEFT JOIN`, `WHERE`, `GROUP BY`, `HAVING`, `ORDER BY`, `LIMIT`; `INSERT`, `UPDATE`, `DELETE`, cada uno con `RETURNING`; `CREATE TABLE`, `CREATE INDEX`, `CREATE VIEW`, `CREATE PROCEDURE` y sus `DROP`; `CALL`; `EXPLAIN` |
| Subconsultas | `EXISTS (…)`, `x IN (…)`, una consulta como valor y una consulta en el `FROM`. Pueden leer la fila de la consulta de afuera, y se corren una vez por fila |
| Expresiones | `CASE`, `CAST`, `LIKE`, `BETWEEN`, `IN`, aritmética, `||`, `COALESCE`, `LOWER`, `UPPER`, `LENGTH`, `ABS`, `STARTSWITH`, `ENDSWITH`, `CONTAINS` |
| Claves | Una clave primaria entera que no se da en un `INSERT` es la siguiente a la más alta de la tabla |
| Las mismas tablas | SQL lee las tablas que declaran los tipos con `[TkTable]`, y lo que escribe es lo que escribiría la API tipada, filas e índices. Una tabla con índices sobre expresiones, `CHECK` o claves foráneas se lee con SQL pero no se escribe |
| Vistas | Consultas guardadas, de lectura; pueden usar otras vistas, y una que acabaría usándose a sí misma se rechaza al crearla |
| `EXPLAIN` | El camino elegido, con las filas estimadas (desde la forma del árbol de cada índice) y las que salieron |
| Sólo asíncrono | `OpenAsync`, `ExecuteReaderAsync`, `BeginTransactionAsync`…; los métodos síncronos de ADO.NET lanzan. Con Dapper: `QueryAsync`, `ExecuteAsync` |
| Lo que no hay | `UNION`, joins derechos y completos, `ALTER TABLE`, subconsultas en consultas con `GROUP BY` o en `UPDATE`/`DELETE`, búsqueda vectorial desde SQL |

[Mediciones y detalle](docs/benchmarks/fase-12.md).

### Entity Framework Core

En su propio paquete, `Tekium.Embedded.EntityFrameworkCore`. Un modelo existente funciona sin reescribir la aplicación, siempre que use la API asíncrona.

```csharp
public sealed class Shop(DbContextOptions<Shop> options) : DbContext(options)
{
    public DbSet<Blog> Blogs => Set<Blog>();
    public DbSet<Post> Posts => Set<Post>();
}

var options = new DbContextOptionsBuilder<Shop>()
    .UseTekium("Data Source=app.tkdb;Password=…")          // o UseTekium(db), compartiendo un TekiumDb abierto
    .Options;

await using var shop = new Shop(options);
await shop.Database.EnsureCreatedAsync();

shop.Add(new Blog { Name = "Engine notes", Posts = { new Post { Title = "Copy-on-write" } } });
await shop.SaveChangesAsync();

var busy = await shop.Blogs
    .Where(b => b.Posts.Any(p => p.Likes > 10))
    .Include(b => b.Posts)
    .OrderBy(b => b.Name)
    .Take(20)
    .ToListAsync();
```

| | |
| --- | --- |
| Funciona | `EnsureCreatedAsync` y `EnsureDeletedAsync`; `SaveChangesAsync` con altas, cambios y bajas, y su comprobación de concurrencia; `ExecuteUpdateAsync` y `ExecuteDeleteAsync`; consultas con `Where`, `OrderBy`, `Skip`/`Take`, `Include`/`ThenInclude` (en una consulta o divididas), `Any`, `All`, `Count`, `First`, `Single`, `Find`, `Min`, `Max`, `Sum`, `Average`, `GroupBy`, `Distinct`, `Join`, `SelectMany`, `Contains`, proyecciones y `FromSqlRaw`; transacciones |
| Comprobado contra SQLite | Las pruebas hacen cada operación sobre TekiumDB y sobre el proveedor de SQLite de EF Core, con el mismo modelo, y exigen el mismo resultado |
| Sólo asíncrono | `ToList()`, `SaveChanges()`, `EnsureCreated()` y los demás métodos síncronos lanzan `NotSupportedException`: TekiumDB no hace E/S síncrona |
| Claves | De una columna. Una clave `int` o `long` la asigna la base; una `Guid`, EF Core |
| Tipos | `bool`, `byte`, `short`, `int`, `long`, `float`, `double`, `decimal`, `string`, `Guid`, `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `TimeSpan`, `byte[]`, enumeraciones, y sus formas anulables. Las fechas con zona se comparan y ordenan como instantes, cosa que SQLite no sabe hacer |
| Texto | `StartsWith`, `EndsWith` y `Contains` comparan caracteres, como en .NET: las mayúsculas cuentan |
| Lo que no hay | Migraciones; claves de varias columnas; claves foráneas en la base (EF Core sigue arreglando y borrando en cascada lo que tiene cargado, pero nada impide una referencia a una fila que no existe); puntos de guardado; `Union`; y lo que EF Core no sabe traducir falla diciéndolo |
| Native AOT | Este paquete no: EF Core arma sus consultas con reflexión y código generado en ejecución |

### Funciones y procedimientos en C#

SQL puede llamar a métodos estáticos de la aplicación. Un generador de código escribe en compilación lo que les pasa los argumentos: no hay reflexión, y funciona bajo Native AOT.

```csharp
public static class Routines
{
    [TkFunction("slugify", Deterministic = true)]
    public static string Slugify(ReadOnlySpan<char> text) { … }

    [TkFunction("abs_delta", Vectorized = true)]          // un bloque de filas por llamada
    public static void AbsDelta(ReadOnlySpan<double> a, ReadOnlySpan<double> b, Span<double> result) { … }

    [TkProcedure("archive_orders")]
    public static async ValueTask<int> ArchiveOrders(TkContext context, DateTimeOffset before, CancellationToken ct) =>
        await context.ExecuteAsync("DELETE FROM orders WHERE placed < @before",
            new Dictionary<string, object?> { ["before"] = before }, ct);
}

database.UseRoutines(TkRoutines.All);                     // TkRoutines lo escribe el generador

await connection.QueryAsync("SELECT slugify(name), abs_delta(price, cost) FROM products");
await connection.ExecuteAsync("CALL archive_orders(@before)", new { before });
```

| | |
| --- | --- |
| Función escalar | Toma y devuelve `bool`, `int`, `long`, `double`, `decimal`, `string`, `Guid`, `DateTime`, `DateTimeOffset` o `byte[]`. Con NULL en un parámetro que no lo admite no se la llama y el resultado es NULL |
| `Deterministic` | Con argumentos constantes se calcula una vez por sentencia; con argumentos de la fila se reutiliza el resultado mientras los argumentos se repitan |
| `Vectorized` | Recibe bloques de hasta 1 024 filas en `ReadOnlySpan<T>` (`double`, `float`, `long`, `int`). Hoy **no es más rápida** que la escalar: el ejecutor de SQL trabaja fila por fila ([medido](docs/benchmarks/fase-13.md)) |
| Procedimiento | Corre con `CALL`, dentro de la transacción de la sentencia: lo que escribe (con la API tipada o con SQL) se confirma o se deshace con ella |
| Dónde viven | En la aplicación, no en el archivo. Una consulta o una vista que nombra una función que la aplicación no registró falla al prepararse, antes de leer una fila |
| Errores de compilación | Lo que no puede ser una rutina (un tipo que SQL no tiene, un método privado, dos con el mismo nombre) es un error `TK0019`–`TK0023` |

### Procedimientos almacenados

Un procedimiento también puede vivir en el archivo. Se escribe en PL/Tk, un subconjunto de PL/pgSQL corto a propósito, se compila al crearlo y se guarda en el catálogo como programa para una máquina virtual pequeña. Viaja con el archivo: un respaldo o una copia compactada lo llevan.

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
| El lenguaje | `DECLARE`, asignación (`:=`), `IF`/`ELSIF`/`ELSE`, `WHILE`, `FOR fila IN SELECT … LOOP`, `RETURN`, `RAISE`, `CALL … INTO`, y `SELECT … INTO`, `INSERT`, `UPDATE`, `DELETE`. Sin SQL dinámico, sin manejo de excepciones, sin tipos compuestos, sin DDL |
| Apagados por defecto | Un procedimiento almacenado es programa dentro de un archivo de datos. Nada del archivo corre, ni se puede crear ninguno, si la base no se abre con `AllowStoredProcedures = true` (`Stored Procedures=true` en una cadena de conexión) |
| Lo que puede hacer | Calcular, correr SQL en la transacción en que lo llamaron, y llamar a otros procedimientos: almacenados, o de C# que la aplicación registró. No tiene E/S, ni red, ni forma de alcanzar otro código |
| Límites | `ProcedureInstructionLimit` (10 millones por defecto) corta un bucle que no termina; `ProcedureCallDepth` (32, a lo sumo 256) corta la recursión sin fin. Los dos son un error |
| Cuando uno falla | La transacción en que corría sólo puede deshacerse: un procedimiento cortado a medias nunca deja la mitad de su trabajo lista para confirmarse |
| Se comprueba al crearlo | Variables y funciones desconocidas, un `RETURN` con valor en un procedimiento que no devuelve nada, una variable que se llama igual que una columna de una tabla que su sentencia usa |
| Un programa dañado | Se verifica antes de correr: o se rechaza diciendo por qué, o corre dentro de los mismos límites. No puede hacer que el motor lea fuera de sus propios registros y código |
| ¿C# o almacenado? | El trabajo sobre datos cuesta casi lo mismo de las dos formas (entre 2 y 9 % más el almacenado): el tiempo se va en los datos, no en el intérprete ([medido](docs/benchmarks/fase-14.md)). Los almacenados no piden redesplegar y los ve toda aplicación que abra el archivo; los de C# son más rápidos en cálculo puro y pueden usar cualquier biblioteca |

### Fechas y series de tiempo

Un índice sobre una fecha ordena por instante (el mismo momento en dos zonas horarias es una sola clave) y, si las fechas sólo crecen, sus hojas quedan llenas. Para tablas grandes escritas en orden de tiempo hay además un **zone map**: por cada ventana de tiempo, la tabla recuerda entre qué claves están sus filas, y una consulta por rango de fechas lee sólo ese tramo.

```csharp
[TkTable("readings")]
public sealed partial class Reading
{
    [TkKey] public Guid Id { get; set; }                       // UUID v7: crece con el tiempo

    [TkZoneMap] public DateTimeOffset At { get; set; }         // ventanas de una hora

    public double Value { get; set; }
}

var lastDay = Reading.Fields.At.Between(now.AddDays(-1), now);
await foreach (var reading in readings.QueryAsync(lastDay))    // ZoneScan, "zone_At"
    Process(reading);
```

| | Zone map | Índice secundario |
| --- | --- | --- |
| Lo que guarda | Una entrada por ventana de tiempo | Una entrada por fila |
| Costo al escribir | Casi nada: se anota en memoria y se escribe al confirmar | Un árbol más que mantener en cada escritura |
| Rango de fechas | Lee de corrido el tramo de la tabla | Va a la tabla una vez por fila encontrada |
| Cuándo sirve | Filas escritas más o menos en orden de fecha bajo claves que crecen | Siempre |
| Precisión | Ventanas enteras: lee algunas filas de más y las descarta | Exacta |

Se pueden declarar los dos sobre la misma columna: el planificador usa el zone map para rangos de varias ventanas y el índice para una fecha exacta o un rango corto. `BucketMinutes` cambia el tamaño de la ventana. Borrar filas no achica las ventanas.

### Claves UUID v7

Un UUID v7 empieza por el milisegundo en que se creó. Con `Guid.CreateVersion7()` como clave, las filas quedan en orden de creación y "todo lo creado entre X e Y" es un rango de la clave: no hace falta una columna de fecha ni un índice sobre ella.

```csharp
var lastHour = Order.Fields.Id.CreatedBetween(now.AddHours(-1), now);   // KeyRange
await foreach (var order in orders.QueryAsync(lastHour))
    Process(order);

Order.Fields.Id.CreatedSince(monday);      // desde un momento
Order.Fields.Id.CreatedBefore(monday);     // antes de un momento
```

Los momentos se cortan al milisegundo, igual que el UUID. Las mismas condiciones sirven sobre cualquier columna `Guid` (por su índice, si lo tiene); en una columna que no guarde UUID v7 comparan los mismos bytes, que no dicen nada de la fecha.

| Cómo llegan las claves | Ocupación de las hojas |
| --- | ---: |
| UUID v7, hasta unas decenas por milisegundo (lo normal: uno por operación) | 96–99 % |
| UUID v7 creados en un bucle cerrado, cientos por milisegundo | 68 %, como al azar: dentro de un milisegundo no tienen orden |
| UUID v4 | 69 %, y seis veces más lento de insertar |

Para cargar muchas filas de una vez, la [carga masiva](#carga-masiva) ordena las claves y deja las hojas llenas vengan como vengan. [Mediciones y detalle](docs/benchmarks/fase-8.md).

### Restricciones

Se declaran con atributos y el motor las comprueba en cada escritura. Una violación lanza `TkConstraintException`, que nombra la restricción y la fila, y aborta la transacción: ya sólo puede deshacerse.

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

| Restricción | Cómo |
| --- | --- |
| `CHECK` | `[TkCheck]` sobre una propiedad `bool` calculada a partir de las columnas de la fila. Debe ser determinista |
| `UNIQUE` | `Unique = true` en un `[TkIndex]`, de una o varias columnas, también parcial. Las filas sin valor en alguna de sus columnas quedan exentas. El planificador sabe que una búsqueda con todas sus columnas fijadas da a lo sumo una fila (`UniqueLookup`) |
| `FOREIGN KEY` | `[TkForeignKey(typeof(Otra))]` sobre una columna del tipo de la clave de la otra tabla. La fila referida debe existir (o la columna no tener valor), y no puede borrarse mientras alguien se refiera a ella. No hay borrado en cascada. Trae su propio índice |
| Añadirlas después | La primera escritura con el tipo que las declara las comprueba contra las filas que ya hay, y si alguna no cumple, la rechaza nombrándola |
| Momento de la comprobación | En cada operación, no al confirmar: dos filas que se refieren una a otra se insertan con la referencia vacía y se completan después |

### Change feed

Lo que cada commit le hizo a las filas de una tabla, leído después del commit y fuera del escritor. Reemplaza a los triggers para auditoría, invalidar cachés o replicar.

```csharp
await using var db = await TekiumDb.OpenAsync(new DbOptions
{
    Path = "app.tkdb",
    ChangeRetention = 1000,                    // cuántos commits pasados se conservan; 0 lo apaga
});

await foreach (var change in db.WatchAsync<Order>(lastSeen, ct))
{
    // change.Kind: Insert | Update | Delete
    // change.Before, change.After, change.TransactionId
    await HandleAsync(change);
    lastSeen = change.TransactionId;           // guardar al terminar cada transacción para reanudar
}
```

| Qué | Cómo |
| --- | --- |
| Qué informa | Un cambio por fila y commit, con la fila antes y después: el efecto neto de la transacción, no cada paso. En orden de commit y, dentro de un commit, de clave |
| Desde dónde | Desde el id de la última transacción vista. `0` es la creación de la base; `db.LastTransactionId` es "desde ahora". Vale también tras cerrar y reabrir |
| Cuándo termina | Al cancelar. Cuando alcanza al escritor, espera el siguiente commit |
| Lo que cuesta | Los commits conservados no liberan sus páginas: el archivo es más grande, en proporción a `ChangeRetention`. El escritor no espera al consumidor |
| Si el consumidor se atrasa | Pasados `ChangeRetention` commits recibe `TkChangeFeedException` en vez de saltarse cambios: debe volver a leer la tabla y seguir desde ahí |

### Carga masiva

Para importar una tabla o restaurar un respaldo. Construye la tabla y sus índices de abajo hacia arriba, escribiendo cada página una sola vez, y no tiene todas las filas en memoria a la vez:

```csharp
await using var tx = await db.BeginWriteAsync();
var loaded = await orders.BulkInsertAsync(tx, ReadOrdersFromCsv(path));   // IEnumerable<T> o IAsyncEnumerable<T>
await tx.CommitAsync();
```

| Qué | Cómo |
| --- | --- |
| Orden de las filas | Cualquiera. Si llegan en orden de clave van directo al árbol, que es lo más rápido; si no, se ordenan antes, en espacio temporal del propio archivo |
| Memoria | Acotada, no crece con el número de filas: las páginas terminadas se escriben al archivo durante la carga y pasan a ser parte de la base al confirmar |
| Índices y restricciones | Los índices se construyen ordenando sus entradas. `CHECK`, `UNIQUE` y `FOREIGN KEY` se comprueban para cada fila |
| Tabla con filas | Se reescribe entera, mezclada con las nuevas: pensado para cargar, no para agregar unas pocas filas a una tabla grande |
| Si algo falla | Una clave repetida, una restricción violada o cualquier error deja la transacción sin poder confirmarse: entran todas las filas o ninguna |
| Lo que no admite | Una tabla con `FOREIGN KEY` a sí misma |

### Búsqueda vectorial

En su propio paquete, `Tekium.Embedded.Vectors`. Un índice vectorial guarda vectores bajo una clave y encuentra los más cercanos a una consulta con un grafo HNSW, que vive en el mismo archivo y cambia en la misma transacción que las filas:

```csharp
using Tekium.Embedded.Vectors;

var embeddings = db.VectorIndex<Guid>("doc_embeddings", new VectorIndexOptions { Dimensions = 384 });

await using (var tx = await db.BeginWriteAsync())
{
    await docs.InsertAsync(tx, doc);
    await embeddings.UpsertAsync(tx, doc.Id, vector);     // la fila y su vector, o ninguno de los dos
    await tx.CommitAsync();
}

var hits = await embeddings.SearchAsync(queryVector, topK: 10, efSearch: 64);
foreach (var hit in hits)                                   // del más cercano al más lejano
    Show(await docs.GetAsync(hit.Key), hit.Distance);
```

| Qué | Cómo |
| --- | --- |
| Claves | `Guid`, `int`, `long` o `string`: normalmente la clave de la fila a la que pertenece el vector |
| Métricas | `Cosine` (por defecto; normaliza al guardar), `Euclidean` e `InnerProduct`. Se fija al crear el índice, igual que las dimensiones y `M` |
| `efSearch` | Cuántos candidatos considera cada búsqueda: más alto encuentra más de los verdaderos vecinos y tarda más. Se elige por consulta |
| Cuantización | `Quantize = true` guarda cada componente del grafo en un byte en vez de cuatro; los vectores originales se guardan aparte y los candidatos finales se reordenan con ellos, así que la distancia devuelta es exacta |
| Transacciones | Lo que una transacción escribe lo encuentra ella misma antes de confirmar y nadie más hasta después; un lector sigue viendo el grafo de su snapshot |
| Borrar | `RemoveAsync` deja de devolver el vector, pero su sitio en el grafo no se recupera hasta `DropAsync`, que elimina el índice entero y libera sus páginas |
| Cifrado | El grafo son páginas como las demás: cifradas si la base lo está |

Medido sobre un millón de vectores de 128 dimensiones agrupados como los embeddings, con los parámetros por defecto ([mediciones](docs/benchmarks/fase-9.md)):

| `efSearch` | Recall@10 contra fuerza bruta | Latencia media |
| ---: | ---: | ---: |
| 16 | 0,79 | 0,39 ms |
| 64 (por defecto) | 0,98 | 0,75 ms |
| 256 | 0,999 | 1,19 ms |

El recall depende de los datos: con vectores sin ninguna estructura es mucho más bajo y hay que subir `efSearch` y `M`. Construir el índice va a unos mil vectores por segundo.

El índice no está atado a una tabla: quien escribe la fila escribe también su vector, en la misma transacción.

### Viaje en el tiempo

Leer la base como estaba cuando confirmó una transacción pasada, hasta donde alcance el horizonte (`ChangeRetention`, el mismo del change feed; apagado por defecto):

```csharp
var db = await TekiumDb.OpenAsync(new DbOptions { Path = "app.tkdb", ChangeRetention = 100 });

await using var past = await db.AsOfAsync(transactionId);
var before = await orders.GetAsync(past, id);               // la fila como estaba entonces
await foreach (var order in orders.QueryAsync(past, filter)) // consultas e índices, también
    Compare(order);
```

El número de transacción lo dan `TkWriteTransaction.TransactionId`, `LastTransactionId` y los cambios del change feed. Más atrás del horizonte se lanza `TkStateUnavailableException`. Mientras `past` viva su estado no se suelta; al cerrarlo, su espacio vuelve a usarse. El archivo crece con el horizonte y ahí se queda. [Mediciones y detalle](docs/benchmarks/fase-10.6.md).

### Backup en caliente

Con la base abierta y en uso. La copia tiene exactamente lo que dejó el último commit al empezar; lo que se escriba mientras tanto no entra, y nada entra a medias.

```csharp
var report = await db.BackupAsync("respaldo.tkdb");
Console.WriteLine($"Respaldado hasta la transacción {report.TransactionId}");

// Una base cifrada no se respalda sin cifrar por omisión:
await db.BackupAsync("respaldo.tkdb", new TkBackupOptions { Password = backupPassword });
```

No bloquea al escritor ni desaloja la caché (lee el archivo directamente). Medido sobre un millón de filas, con backups corriendo uno tras otro: la mediana del commit durable no cambia (13,0 ms contra 13,1 ms sin backup), el p95 sube de 16 a 19 ms porque comparten el disco, y cada backup tarda 0,35 s. Mientras corre, las páginas del estado que copia no se reutilizan, así que una base con mucha escritura puede crecer durante un backup largo. [Mediciones y detalle](docs/benchmarks/fase-10.2.md).

### Verificación y diagnóstico

```csharp
var verification = await db.VerifyAsync();        // con la base abierta y en uso
if (!verification.IsHealthy)
    foreach (var finding in verification.Findings)
        log.Error(finding.Message);
```

`VerifyAsync` lee del archivo cada página en uso y comprueba su suma de verificación (o su etiqueta de autenticación, si la base está cifrada), que cada árbol esté bien formado, y que la lista de páginas libres no ofrezca una en uso. Lee el disco, no la caché: encuentra una página dañada aunque la aplicación tenga una copia buena en memoria. No bloquea al escritor.

Con `Diagnostics = true`, la base publica contadores por un `Meter` llamado `Tekium.Embedded`, que lee OpenTelemetry, `dotnet-counters` o cualquier `MeterListener`:

| Instrumento | Qué dice |
| --- | --- |
| `tekium.cache.hits`, `tekium.cache.misses` | Páginas encontradas en la caché, y leídas del archivo |
| `tekium.commit.pages`, `tekium.commit.duration` | Páginas escritas y tiempo de cada commit |
| `tekium.snapshots.live`, `tekium.snapshots.oldest_age` | Lectores con un estado retenido, y la edad del más viejo: lo que impide reutilizar espacio |
| `tekium.tree.depth` | Niveles de cada tabla e índice |

Sin esa opción no se crea ningún `Meter` y no se cuenta nada: medido, la lectura puntual tarda lo mismo. [Mediciones y detalle](docs/benchmarks/fase-10.4.md).

### Adaptadores para ASP.NET Core

Tres paquetes pequeños, cada uno sólo con la dependencia a la que se adapta. El núcleo no gana ninguna.

```csharp
await using var db = await TekiumDb.OpenAsync(new DbOptions { Path = "app.tkdb", Password = password.AsMemory() });
var keys = await TkXmlRepository.CreateAsync(db);                       // Tekium.Embedded.DataProtection

builder.Services.AddTekiumDistributedCache(_ => db);                    // Tekium.Embedded.Caching
builder.Services.AddDataProtection().PersistKeysToTekiumDb(keys);
builder.Services.AddHealthChecks().AddTekiumDb(_ => db);                // Tekium.Embedded.HealthChecks
```

| Adaptador | Qué hace | Conviene saber |
| --- | --- | --- |
| `TkDistributedCache` | Un `IDistributedCache` que sobrevive a los reinicios, sin servidor. Vencimiento absoluto y deslizante | Sólo funcionan los métodos `…Async`; los síncronos lanzan, porque TekiumDB no hace E/S síncrona. Cada `SetAsync` es un commit durable. Leer una entrada deslizante la renueva (una escritura) sólo pasada una décima de su ventana. Lo vencido nunca se devuelve, y sale del archivo durante una escritura posterior: no hay hilo en segundo plano |
| `TkXmlRepository` | Guarda el anillo de claves de Data Protection en la base: cifrado en reposo si la base lo está | La interfaz de Data Protection es síncrona, así que las claves se leen una vez al arrancar y se sirven desde memoria, y una clave nueva se escribe al archivo justo después de guardarse, sin bloquear. Si el proceso muere en esos milisegundos, la clave nueva se pierde. `FlushAsync` espera la escritura e informa si falló |
| `TkHealthCheck` | Sano cuando la base está abierta y acepta escrituras. No hace E/S | Con `Verify = true` además lee y verifica cada página en uso: el daño es "unhealthy", las advertencias "degraded". Su costo crece con el archivo: para correrlo de vez en cuando, no cada pocos segundos |

## Herramientas

`tkdb` es una herramienta de línea de comandos para archivos `.tkdb` cerrados. Ninguno de sus comandos cambia el archivo que recibe, salvo `passwd`.

```bash
tkdb info    app.tkdb                              # lo que dice la cabecera; no pide contraseña
tkdb check   app.tkdb                              # lee cada página y dice qué está mal, sin abrir la base
tkdb compact app.tkdb -o compacta.tkdb             # una copia sin el espacio que dejaron los cambios
tkdb repair  dañada.tkdb -o recuperada.tkdb        # una copia con todo lo que todavía se puede leer
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

$ tkdb repair app.tkdb -o recuperada.tkdb
25000 entries copied to recuperada.tkdb
4194304 bytes became 1359872
Left out: index tickets.Owner

ERROR: Page 57 of index tickets.Owner cannot be read: Page 57 failed its checksum: the file is corrupt.
```

(Salida real, sobre un archivo de prueba al que se le cambió un byte de la página 57.)

| Comando | Qué hace |
| --- | --- |
| `check` | Revisa las dos copias de la cabecera, los dos registros de dónde está la base, cada página en uso (su suma de verificación o su etiqueta de cifrado, y que el árbol esté bien formado) y la lista de páginas libres. Nombra cada página dañada y a qué tabla o índice pertenece. Lee archivos que el motor se niega a abrir |
| `compact` | Reconstruye cada árbol con sus páginas llenas en un archivo nuevo. Se niega si el archivo está dañado |
| `repair` | Lo mismo, pero copia lo que se puede leer y dice qué dejó fuera. Las filas de las páginas dañadas se pierden; un índice dañado, o cuya tabla perdió filas, no se copia y la primera escritura en esa tabla lo vuelve a construir |
| `passwd` | Cambia la contraseña en el mismo archivo: sólo reescribe la cabecera |
| Contraseñas | Nunca en la línea de comandos: de una variable de entorno, de un archivo o de la entrada estándar. `compact` y `repair` pueden dar a la copia otra contraseña o ninguna, y siempre le dan una clave de archivo nueva |
| Código de salida | 0 nada mal, 1 el archivo tiene problemas, 2 el comando no pudo correr |

Lo mismo está en el núcleo, para usarlo desde código: `TkInspector.InspectAsync`, `TkMaintenance.CompactAsync` y `TkMaintenance.RepairAsync`. Compactar y reparar no copian el historial del change feed. [Mediciones y detalle](docs/benchmarks/fase-10.md).

## Rendimiento

Medido con BenchmarkDotNet en una sola máquina (Apple M2 Max, macOS, arm64, SSD interno), contra `Microsoft.Data.Sqlite` con comandos preparados. Son órdenes de magnitud de una máquina, no promesas; cada enlace lleva a las condiciones exactas y a sus advertencias.

**Lecturas**

| Carga | SQLite | TekiumDB | |
| --- | ---: | ---: | --- |
| Leer una fila por su clave y construir el objeto (100 000 filas) | 2 016 ns, 670 B | 336 ns, 326 B | 6× más rápido ([detalle](docs/benchmarks/fase-5.md)) |
| Lectura puntual cruda (un millón de filas) | 2 177 ns | 397 ns | 5,5× más rápido ([detalle](docs/benchmarks/fase-2.md)) |
| Por índice secundario: las ~100 facturas de un cliente | 124 µs | 69 µs | 1,8× más rápido ([detalle](docs/benchmarks/fase-6.md)) |
| Por índice secundario: las ~270 facturas de un día | 309 µs | 149 µs | 2,1× más rápido |
| Rango de fechas sobre 10 millones de filas: una hora (3 601 filas) | 2,91 ms | 0,75 ms | 3,9× más rápido, con zone map ([detalle](docs/benchmarks/fase-7.md)) |
| Rango de fechas: un día (86 401 filas) | 69,4 ms | 10,7 ms | 6,5× más rápido |
| Rango de fechas: treinta días (2,6 millones de filas) | 2 102 ms | 310 ms | 6,8× más rápido |

**Escrituras**

| Carga | SQLite | TekiumDB | |
| --- | ---: | ---: | --- |
| 100 000 filas en una transacción | 122 ms | 29,5 ms | 4,1× más rápido ([detalle](docs/benchmarks/fase-2.md)) |
| 100 000 filas en 100 transacciones durables | 749 ms | 957 ms | **1,28× más lento**: cada commit copia el camino hasta la raíz |
| 10 000 filas con tres índices, un commit | 31,6 ms | 22,9 ms | 1,4× más rápido ([detalle](docs/benchmarks/fase-6.md)) |
| Cargar 10 millones de filas en orden | 14,0 s | 1,85 s (carga masiva) | 7,6× más rápido, con 100 MiB de memoria ([detalle](docs/benchmarks/fase-6.8.md)) |

**Espacio**

| | TekiumDB | SQLite |
| --- | ---: | ---: |
| 100 000 filas tras 100 000 actualizaciones al azar, sobre el tamaño de los datos | 1,16× | 1,22× |

Copy-on-write tiene un costo conocido: una transacción de lectura que se deja abierta impide reutilizar páginas y el archivo crece mientras viva (84× en esa misma carga con un lector retenido todo el tiempo). El contador `tekium.snapshots.oldest_age` existe para vigilarlo. [Detalle](docs/benchmarks/fase-4.md).

**Otras mediciones**

| Qué | Resultado |
| --- | --- |
| Búsqueda vectorial, un millón de vectores de 128 dimensiones | Recall@10 de 0,98 en 0,75 ms por consulta ([detalle](docs/benchmarks/fase-9.md)) |
| Ocupación de las hojas con claves UUID v7 | 96–99 % ([detalle](docs/benchmarks/fase-8.md)) |
| Backup en caliente de un millón de filas | 0,35 s; la mediana del commit del escritor no cambia ([detalle](docs/benchmarks/fase-10.2.md)) |
| Contadores de diagnóstico apagados | Sin costo medible en la lectura puntual ([detalle](docs/benchmarks/fase-10.4.md)) |
| Función de C# llamada desde SQL | Unos 30 ns por fila, lo mismo que la expresión escrita en SQL ([detalle](docs/benchmarks/fase-13.md)) |
| Procedimiento almacenado: una vuelta de un bucle vacío | Unos 100 ns; archivar 10 000 filas tarda entre 2 y 9 % más que las mismas sentencias desde C# ([detalle](docs/benchmarks/fase-14.md)) |
| Compresión por página | Reduce 3,3× los bytes escritos, pero no el tamaño del archivo: apagada por defecto ([detalle](docs/benchmarks/fase-5.5.md)) |
| Tomar un snapshot y fijar una página en caché | 15,9 ns, sin asignaciones |

Los benchmarks se reproducen con `dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- --filter '*'`.

## Arquitectura

Paquetes opcionales sobre un núcleo en capas. Las flechas son dependencias: el núcleo no referencia a ninguno de los paquetes de arriba.

```
Tekium.Embedded.Sql          Tekium.Embedded.Linq        Tekium.Embedded.Vectors
(SQL, ADO.NET, procedimientos) (predicados de C#)        (HNSW + cuantización)
         |                            |                            |
         v                            v                            v
+-------------------------------------------------------------------------+
| Tekium.Embedded.Core — sin dependencias nativas, compatible con AOT     |
|  API pública         TekiumDb, Table<T>, transacciones, IAsyncEnumerable|
|  Catálogo y tablas   esquemas, filas tipadas, planificador, restricciones|
|  Índices             B+Tree, secundarios, zone maps, carga masiva       |
|  MVCC                snapshots, commit atómico, historial de estados    |
|  Pager               caché de páginas, free-list, copy-on-write         |
|  Cifrado             AES-256-GCM por página, Argon2id                   |
|  E/S                 RandomAccess asíncrono, sin P/Invoke               |
+-------------------------------------------------------------------------+
```

| Decisión | Consecuencia |
| --- | --- |
| Copy-on-write: una página confirmada nunca se modifica | Los lectores no bloquean; un snapshot, un backup o un estado pasado son sólo una raíz que se conserva |
| Commit = escribir páginas nuevas, vaciar, y cambiar un registro de raíz | No hay WAL que reproducir ni checkpoint; tras un corte la base abre en el último commit completo |
| Todo son B+Trees del mismo archivo | Índices, catálogo, zone maps y el grafo vectorial heredan transacciones, cifrado y commit atómico |
| El nonce de cada página cifrada es `página ‖ transacción` | Una página nunca se cifra dos veces con el mismo nonce |
| Generador de código en vez de reflexión | Funciona bajo Native AOT; los errores de modelo son errores de compilación (`TK0001`…) |

## Confiabilidad

Cómo se prueba el motor:

- **Contra un modelo.** El B+Tree, los índices, las restricciones y las consultas se comparan con una implementación de referencia en memoria sobre secuencias aleatorias de operaciones (un millón en el árbol), verificando además la estructura y que no se pierda ninguna página.
- **Cortes de energía.** El commit se interrumpe antes de cada operación de E/S, perdiendo a pedazos lo que no se había vaciado al disco; la base tiene que abrir siempre en un estado confirmado.
- **Procesos muertos.** Un proceso hijo se mata en cada punto del commit, con el mismo requisito.
- **Corrupción.** Se daña cada página de un archivo, una por una: la inspección la nombra siempre, sin lanzar, y la verificación en línea la encuentra aunque esté en caché. También se daña cada byte del programa de un procedimiento almacenado: se rechaza o corre dentro de sus límites.
- **Criptografía.** Argon2id y BLAKE2b contra vectores de referencia (RFC 9106); el resto es la criptografía de .NET.
- **Archivo y memoria.** Las pruebas del almacenamiento corren sobre los dos dispositivos, con inyección de fallos de E/S.
- **Native AOT.** Un programa publicado con AOT ejercita el motor, las tablas tipadas, los vectores, las herramientas, LINQ y SQL en cada verificación.
- **Las pruebas se prueban.** Se introducen defectos a mano en el motor y se comprueba que alguna prueba falle.

Plataformas: verificado en macOS (arm64) y en Linux (arm64, contenedor). Windows no se ha verificado.

## Límites

Lo que conviene saber antes de adoptarla:

- **Un proceso por archivo.** No hay acceso concurrente desde varios procesos.
- **Un escritor a la vez.** Las escrituras se serializan; muchos commits pequeños y durables son más lentos que en SQLite con WAL.
- **Lectores largos hacen crecer el archivo**, y el archivo no se achica solo: se compacta con `tkdb compact`.
- **SQL es un subconjunto:** sin `UNION`, joins derechos o completos, ni `ALTER TABLE`; una subconsulta se corre una vez por fila de la consulta de afuera.
- **LINQ traduce o rechaza:** sin `||`, `!=`, ordenar ni agrupar.
- **Sin triggers, vistas materializadas, replicación ni borrado en cascada.** En su lugar: restricciones declarativas, índices sobre expresiones y change feed.
- **El índice vectorial** no recupera el espacio de los vectores borrados hasta eliminarlo, y no se consulta desde SQL.
- **El proveedor de Entity Framework Core es sólo asíncrono**, no tiene migraciones y no crea claves foráneas en la base.
- **Formato de archivo sin estabilizar** hasta la primera versión publicada.

## Compilar y probar

Requisitos: SDK de .NET 11. Para publicar con Native AOT hace falta además la cadena de herramientas nativa de la plataforma (Xcode en macOS, `clang` en Linux, las herramientas de C++ de Visual Studio en Windows).

```bash
dotnet build -c Release                                   # los warnings son errores
dotnet test -c Release                                    # todas las pruebas
dotnet test --filter-class "*BTreePropertyTests"          # una clase

# Native AOT
dotnet publish tests/Tekium.Embedded.AotSmoke -c Release -o artifacts/aot
./artifacts/aot/Tekium.Embedded.AotSmoke

# Benchmarks
dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- --filter '*'

# Build, pruebas y AOT en Linux, dentro de un contenedor (requiere Docker)
scripts/test-linux.sh
```

Las pruebas usan xUnit v3 sobre Microsoft.Testing.Platform, por eso los filtros son `--filter-class` y `--filter-method` en lugar de `--filter`.

```
src/
  Tekium.Embedded.Core/          Motor y API pública
  Tekium.Embedded.Generators/    Generador de código: tablas, índices, restricciones y rutinas
  Tekium.Embedded.Sql/           SQL, proveedor ADO.NET, rutinas de C# y procedimientos almacenados
  Tekium.Embedded.Linq/          Consultas con predicados de C#
  Tekium.Embedded.Vectors/       Búsqueda vectorial HNSW
  Tekium.Embedded.EntityFrameworkCore/ Proveedor de Entity Framework Core
  Tekium.Embedded.Caching/       IDistributedCache
  Tekium.Embedded.DataProtection/ Almacén de claves de Data Protection
  Tekium.Embedded.HealthChecks/  Health check
  Tekium.Embedded.Cli/           tkdb
tests/                           Pruebas, benchmarks y el programa de verificación de Native AOT
docs/benchmarks/                 Mediciones, con sus condiciones
```

## Licencia

[MIT](LICENSE). Copyright (c) 2026 Tekium.
