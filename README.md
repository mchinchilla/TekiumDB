# TekiumDB

**Base de datos embebida para .NET: un solo archivo, cifrada, asíncrona de verdad y sin binarios nativos.**

![.NET 11](https://img.shields.io/badge/.NET-11-512BD4)
![Licencia MIT](https://img.shields.io/badge/licencia-MIT-green)
![Estado: pre-alpha](https://img.shields.io/badge/estado-pre--alpha-orange)

TekiumDB guarda todo en un único archivo `.tkdb` y se distribuye como un paquete NuGet 100% administrado. Su motor es un B+Tree copy-on-write con MVCC: los lectores nunca bloquean ni esperan, y el commit es atómico sin WAL ni journal.

> [!WARNING]
> **Proyecto en fase temprana (Fase 12 de la hoja de ruta).** Ya hay una API pública que se puede usar: tablas tipadas con serializadores generados en compilación, índices secundarios (de columna, de varias, sobre expresiones y parciales) con consultas por igualdad y por rango, restricciones `CHECK`, `UNIQUE` y `FOREIGN KEY`, change feed, carga masiva, transacciones con aislamiento por snapshot, cifrado y modo en memoria, sobre el motor de las fases anteriores (pager, B+Tree copy-on-write, commit atómico probado matando el proceso). Hay además búsqueda vectorial (HNSW) en un paquete aparte. Hay también una capa LINQ y una capa SQL con proveedor ADO.NET (un subconjunto de SQL: ver su sección). Todavía no hay paquete publicado, y el formato de archivo puede cambiar. Ver [Estado y hoja de ruta](#estado-y-hoja-de-ruta).

## Contenido

- [Por qué TekiumDB](#por-qué-tekiumdb)
- [Características](#características)
- [Vistazo a la API](#vistazo-a-la-api)
- [Arquitectura](#arquitectura)
- [Estado y hoja de ruta](#estado-y-hoja-de-ruta)
- [Compilar y probar](#compilar-y-probar)
- [Estructura del repositorio](#estructura-del-repositorio)
- [Principios de ingeniería](#principios-de-ingeniería)
- [Fuera de alcance](#fuera-de-alcance)
- [Contribuir](#contribuir)
- [Licencia](#licencia)

## Por qué TekiumDB

El competidor real de una base embebida es SQLite, y "SQLite pero más rápido" no es una propuesta. TekiumDB apunta a cinco cosas que con SQLite cuestan trabajo en .NET:

| | SQLite en .NET | TekiumDB |
| --- | --- | --- |
| Binarios nativos | Un `.so` / `.dll` / `.dylib` por plataforma | Ninguno: código administrado, compatible con Native AOT, single-file y móvil |
| Async | La API asíncrona es síncrona por debajo | E/S asíncrona de punta a punta |
| Búsqueda vectorial | Extensión nativa aparte | HNSW incluido |
| Cifrado | SEE comercial o SQLCipher | AES-256-GCM autenticado, incluido |
| UUID v7 y fechas | `BLOB` con un índice encima | Tipos de primera clase con índices especializados |

Lo que TekiumDB **no** pretende: ganarle a SQLite en SQL complejo (joins, estimación de cardinalidad, reordenamiento).

Antes de invertir en el resto, la Fase 0.5 midió un motor mínimo contra `Microsoft.Data.Sqlite`: lectura puntual 6× más rápida, rango de fechas 38× más rápido e inserción secuencial entre empate y 5× más rápida. Los números, las condiciones y sus advertencias están en [`docs/benchmarks/fase-0.5.md`](docs/benchmarks/fase-0.5.md). Con el pager real las lecturas siguen entre 5× y 38× por delante; la inserción con commits pequeños quedó 1,28× por detrás ([Fase 1](docs/benchmarks/fase-1.md), [Fase 2](docs/benchmarks/fase-2.md)).

## Características

Todas forman parte del diseño. Hoy existen el pager, el cifrado, el B+Tree copy-on-write con commit atómico, el catálogo, las tablas tipadas con filas de cualquier tamaño (páginas de overflow) y los índices secundarios genéricos; falta en el árbol la compresión de prefijos.

**Almacenamiento**

- Archivo único `.tkdb` con páginas de tamaño fijo (8 KiB por defecto; 4, 16 o 32 KiB configurables).
- B+Tree copy-on-write con MVCC: un escritor, lectores ilimitados sin bloqueo, cada uno sobre su snapshot inmutable.
- Commit atómico por slots de raíz alternados. Si el proceso muere a mitad de un commit, la base abre en el estado anterior, sin recuperación.
- Compresión Zstandard por página, opcional (`Compression = true`). Reduce los bytes que se escriben, no el tamaño del archivo: ver [las mediciones](docs/benchmarks/fase-5.5.md).
- Modo en memoria para pruebas: `new DbOptions { InMemory = true }`.

**Seguridad**

- AES-256-GCM por página con la criptografía del BCL. El tag de autenticación es también el checksum: corrupción y manipulación se detectan al descifrar.
- Derivación de clave con Argon2id. Cambiar la contraseña reescribe sólo la cabecera.

**Índices**

- Temporal: zone maps por ventana de tiempo y split 100/0 para series de tiempo (ya existe: ver [Fechas y series de tiempo](#fechas-y-series-de-tiempo)).
- UUID v7: las hojas quedan llenas aunque los de un mismo milisegundo lleguen en desorden, y "todo lo creado entre X e Y" es un rango de la clave (ya existe: ver [Claves UUID v7](#claves-uuid-v7)).
- Vectorial: HNSW persistido en el mismo archivo, con métricas coseno, L2 y producto interno, y cuantización `int8` opcional (ya existe: ver [Búsqueda vectorial](#búsqueda-vectorial)).
- Secundarios genéricos, sobre expresiones y parciales (ya existen: ver [Índices y consultas](#índices-y-consultas)).

**API y ecosistema**

- Núcleo tipado, sin reflexión en runtime: un generador de código fuente emite los serializadores.
- Capas opcionales en paquetes separados: SQL con proveedor ADO.NET, LINQ y vectores.
- Restricciones declarativas (`CHECK`, `UNIQUE`, `FOREIGN KEY`; ya existen: ver [Restricciones](#restricciones)) y change feed post-commit (ver [Change feed](#change-feed)).
- Backup en caliente, verificación de integridad y viaje en el tiempo sobre snapshots.

## Vistazo a la API

Esto compila y funciona hoy:

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

Detalle en [docs/benchmarks/fase-11.md](docs/benchmarks/fase-11.md).

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
| Sentencias | `SELECT` con `INNER JOIN`, `WHERE`, `GROUP BY`, `HAVING`, `ORDER BY`, `LIMIT`; `INSERT`, `UPDATE`, `DELETE`; `CREATE TABLE`, `CREATE INDEX`, `CREATE VIEW` y sus `DROP`; `EXPLAIN` |
| Las mismas tablas | SQL lee las tablas que declaran los tipos con `[TkTable]`, y lo que escribe es lo que escribiría la API tipada, filas e índices. Una tabla con índices sobre expresiones, `CHECK` o claves foráneas se lee con SQL pero no se escribe |
| Vistas | Consultas guardadas, de lectura; pueden usar otras vistas, y una que acabaría usándose a sí misma se rechaza al crearla |
| `EXPLAIN` | El camino elegido, con las filas estimadas (desde la forma del árbol de cada índice) y las que salieron |
| Sólo asíncrono | `OpenAsync`, `ExecuteReaderAsync`, `BeginTransactionAsync`…; los métodos síncronos de ADO.NET lanzan. Con Dapper: `QueryAsync`, `ExecuteAsync` |
| Lo que no hay | Subconsultas, `UNION`, joins externos, `ALTER TABLE`, búsqueda vectorial desde SQL |

Detalle, y lo que falta respecto del plan, en [docs/benchmarks/fase-12.md](docs/benchmarks/fase-12.md).

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

Para cargar muchas filas de una vez, la [carga masiva](#carga-masiva) ordena las claves y deja las hojas llenas vengan como vengan. Detalle en [docs/benchmarks/fase-8.md](docs/benchmarks/fase-8.md).

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

Medido sobre un millón de vectores de 128 dimensiones agrupados como los embeddings, con los parámetros por defecto ([detalle](docs/benchmarks/fase-9.md)):

| `efSearch` | Recall@10 contra fuerza bruta | Latencia media |
| ---: | ---: | ---: |
| 16 | 0,79 | 0,39 ms |
| 64 (por defecto) | 0,98 | 0,75 ms |
| 256 | 0,999 | 1,19 ms |

El recall depende de los datos: con vectores sin ninguna estructura es mucho más bajo y hay que subir `efSearch` y `M`. Construir el índice va a unos mil vectores por segundo.

El índice no está atado a una tabla: quien escribe la fila escribe también su vector, en la misma transacción. Declararlo como columna (`[TkVector]`) y que la tabla lo mantenga sola todavía no existe.

### Viaje en el tiempo

Leer la base como estaba cuando confirmó una transacción pasada, hasta donde alcance el horizonte (`ChangeRetention`, el mismo del change feed; apagado por defecto):

```csharp
var db = await TekiumDb.OpenAsync(new DbOptions { Path = "app.tkdb", ChangeRetention = 100 });

await using var past = await db.AsOfAsync(transactionId);
var before = await orders.GetAsync(past, id);               // la fila como estaba entonces
await foreach (var order in orders.QueryAsync(past, filter)) // consultas e índices, también
    Compare(order);
```

El número de transacción lo dan `TkWriteTransaction.TransactionId`, `LastTransactionId` y los cambios del change feed. Más atrás del horizonte se lanza `TkStateUnavailableException`. Mientras `past` viva su estado no se suelta; al cerrarlo, su espacio vuelve a usarse. El archivo crece con el horizonte y ahí se queda. Detalle en [docs/benchmarks/fase-10.6.md](docs/benchmarks/fase-10.6.md).

### Backup en caliente

Con la base abierta y en uso. La copia tiene exactamente lo que dejó el último commit al empezar; lo que se escriba mientras tanto no entra, y nada entra a medias.

```csharp
var report = await db.BackupAsync("respaldo.tkdb");
Console.WriteLine($"Respaldado hasta la transacción {report.TransactionId}");

// Una base cifrada no se respalda sin cifrar por omisión:
await db.BackupAsync("respaldo.tkdb", new TkBackupOptions { Password = backupPassword });
```

No bloquea al escritor ni desaloja la caché (lee el archivo directamente). Medido sobre un millón de filas, con backups corriendo uno tras otro: la mediana del commit durable no cambia (13,0 ms contra 13,1 ms sin backup), el p95 sube de 16 a 19 ms porque comparten el disco, y cada backup tarda 0,35 s. Mientras corre, las páginas del estado que copia no se reutilizan, así que una base con mucha escritura puede crecer durante un backup largo. Detalle en [docs/benchmarks/fase-10.2.md](docs/benchmarks/fase-10.2.md).

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

Sin esa opción no se crea ningún `Meter` y no se cuenta nada: medido, la lectura puntual tarda lo mismo. Detalle en [docs/benchmarks/fase-10.4.md](docs/benchmarks/fase-10.4.md).

### Herramientas

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

Lo mismo está en el núcleo, para usarlo desde código: `TkInspector.InspectAsync`, `TkMaintenance.CompactAsync` y `TkMaintenance.RepairAsync`. Compactar y reparar no copian el historial del change feed. Detalle en [docs/benchmarks/fase-10.md](docs/benchmarks/fase-10.md).

Reglas de la API:

- Toda operación de E/S devuelve `ValueTask` o `IAsyncEnumerable<T>` y acepta `CancellationToken`. No hay métodos síncronos salvo `Dispose`.
- Las transacciones son `IAsyncDisposable`: salir del scope sin commit hace rollback.
- Nada de `object`, `dynamic` ni `Expression.Compile()`.

## Arquitectura

Tres paquetes opcionales sobre un núcleo de siete capas. Las flechas son dependencias: el núcleo no referencia a ninguno de los paquetes de arriba.

```
Tekium.Embedded.Sql          Tekium.Embedded.Linq        Tekium.Embedded.Vectors
(SQL + proveedor ADO.NET)    (IQueryable tipado)         (HNSW + cuantización)
         |                            |                            |
         v                            v                            v
+-------------------------------------------------------------------------+
| Tekium.Embedded.Core — sin dependencias nativas, compatible con AOT     |
|  API pública         IDatabase, ITable, ITransaction, IAsyncEnumerable  |
|  Ejecutor y catálogo planes, cursores, serialización de filas sin boxing|
|  Gestor de índices   B+Tree, temporal, UUID v7, IVectorIndex            |
|  MVCC                snapshots, raíz de versiones, commit atómico       |
|  Pager               caché de páginas, free-list, copy-on-write         |
|  Cifrado             AES-256-GCM por página, Argon2id                   |
|  E/S                 RandomAccess, sin P/Invoke ni binarios nativos     |
+-------------------------------------------------------------------------+
```

Cada capa expone su contrato como interfaz interna y se prueba sola: el pager no sabe que existe el B+Tree, y el B+Tree no sabe que las páginas van cifradas.

El diseño completo —formato de archivo, flujo de commit, construcción del nonce, índices y justificación de cada decisión— está en [`TekiumDB-plan.md`](TekiumDB-plan.md).

## Estado y hoja de ruta

Las fases son estrictamente secuenciales: cada una termina con su criterio de salida cumplido y CI en verde antes de empezar la siguiente. Mientras no haya CI disponible, la verificación es local: macOS directamente y Linux con `scripts/test-linux.sh`.

| Fase | Entrega | Estado |
| --- | --- | --- |
| 0 | Esqueleto de la solución, CI, BenchmarkDotNet, analizadores | Hecho en macOS y Linux; Windows sin verificar |
| 0.5 | Spike de validación: pager y B+Tree mínimos medidos contra SQLite | Hecho: [dentro del criterio](docs/benchmarks/fase-0.5.md), se sigue |
| 1 | Capa de E/S y pager: caché de páginas, checksums, free-list | Hecho en macOS y Linux: [criterio cumplido](docs/benchmarks/fase-1.md); Windows sin verificar |
| 1.5 | Modo en memoria | Hecho en macOS y Linux: las pruebas del pager corren sobre archivo y sobre memoria; Windows sin verificar |
| 2 | Cifrado por página y derivación de clave | Hecho en macOS y Linux: [criterio cumplido](docs/benchmarks/fase-2.md); Windows sin verificar |
| 3 | B+Tree copy-on-write: insert, lookup, delete, split, merge | Hecho en macOS y Linux: un millón de operaciones aleatorias contra un modelo; Windows sin verificar |
| 4 | MVCC, transacciones y commit atómico | Hecho en macOS y Linux: [criterio cumplido](docs/benchmarks/fase-4.md); Windows sin verificar |
| 5 | Catálogo, filas tipadas y generador de código | Hecho en macOS y Linux: [criterio cumplido](docs/benchmarks/fase-5.md); Windows sin verificar |
| 5.5 | Compresión Zstandard por página | Hecho y [medido](docs/benchmarks/fase-5.5.md): comprime 3,3× los bytes escritos pero no ahorra disco y cuesta latencia; queda apagada por defecto |
| 6 | Índices secundarios, codificación ordenada de claves, índices sobre expresiones y parciales | Hecho en macOS y Linux: [criterio cumplido](docs/benchmarks/fase-6.md); Windows sin verificar |
| 6.5 | Restricciones declarativas: `CHECK`, `UNIQUE`, `FOREIGN KEY` | Hecho en macOS y Linux: [criterio cumplido](docs/benchmarks/fase-6.5.md); Windows sin verificar |
| 6.7 | Change feed post-commit | Hecho en macOS y Linux: [criterio cumplido](docs/benchmarks/fase-6.7.md); Windows sin verificar |
| 6.8 | Carga masiva: el B+Tree construido de abajo hacia arriba | Hecho en macOS y Linux, [medido](docs/benchmarks/fase-6.8.md): 3× más rápida que insertar fila por fila en una sola transacción, con la novena parte de la memoria; no es el orden de magnitud del criterio, que sólo se alcanza contra insertar con un commit cada mil filas (58×) |
| 7 | Índice temporal con zone maps y split 100/0 | Hecho en macOS y Linux: [criterio cumplido](docs/benchmarks/fase-7.md); Windows sin verificar |
| 8 | Índice UUID v7 | Hecho en macOS y Linux, [medido](docs/benchmarks/fase-8.md): criterio cumplido con hasta decenas de UUID por milisegundo (96–99 % de ocupación); no con cientos por milisegundo creados en un bucle (68 %); Windows sin verificar |
| 9 | Índice vectorial HNSW | Hecho en macOS y Linux: [criterio cumplido](docs/benchmarks/fase-9.md) (recall@10 de 0,98 sobre un millón de vectores con estructura); Windows sin verificar |
| 10 | Herramientas: `tkdb` para inspeccionar, reparar, cambiar la contraseña y compactar | Hecho en macOS y Linux: [criterio cumplido](docs/benchmarks/fase-10.md); Windows sin verificar |
| 10.2 | Backup en caliente desde un snapshot fijado | Hecho en macOS y Linux: [criterio cumplido](docs/benchmarks/fase-10.2.md); Windows sin verificar |
| 10.4 | `VerifyAsync` y contadores de diagnóstico por `Meter` | Hecho en macOS y Linux: [criterio cumplido](docs/benchmarks/fase-10.4.md); Windows sin verificar |
| 10.6 | Viaje en el tiempo: leer la base como estaba en una transacción pasada | Hecho en macOS y Linux: [criterio cumplido](docs/benchmarks/fase-10.6.md); Windows sin verificar |
| 11 | Capa LINQ | Hecho en macOS y Linux: [criterio cumplido](docs/benchmarks/fase-11.md); Windows sin verificar |
| 12 | Capa SQL y proveedor ADO.NET | Hecho en macOS y Linux: [criterio cumplido](docs/benchmarks/fase-12.md) sobre un subconjunto de SQL (sin subconsultas, `UNION`, joins externos ni búsqueda vectorial desde SQL); Windows sin verificar |
| 13 – 14 | Funciones y procedimientos: C# por generador y PL/Tk | Pendiente |
| 15 | Adaptadores: EF Core, `IDistributedCache`, Data Protection, HealthChecks | Pendiente |

Las fases 0 a 4 concentran el riesgo del proyecto. El producto es entregable al terminar la 10. Los criterios de salida de cada fase están en el [plan](TekiumDB-plan.md#fases-de-entrega).

## Compilar y probar

Requisitos: SDK de .NET 11. Para publicar con Native AOT hace falta además la cadena de herramientas nativa de la plataforma (Xcode en macOS, `clang` en Linux, las herramientas de C++ de Visual Studio en Windows).

```bash
# Compilar (los warnings son errores)
dotnet build -c Release

# Pruebas
dotnet test

# Una sola clase de pruebas
dotnet test --filter-class "*PageSizeTests"

# Verificar la compatibilidad con Native AOT
dotnet publish tests/Tekium.Embedded.AotSmoke -c Release -o artifacts/aot
./artifacts/aot/Tekium.Embedded.AotSmoke

# Benchmarks
dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- --filter '*'

# Todo lo anterior en Linux, dentro de un contenedor (requiere Docker)
scripts/test-linux.sh
```

Las pruebas usan xUnit v3 sobre Microsoft.Testing.Platform, por eso los filtros son `--filter-class` y `--filter-method` en lugar de `--filter`.

## Estructura del repositorio

```
src/
  Tekium.Embedded.Cli/           tkdb: herramienta de línea de comandos
  Tekium.Embedded.Core/          Núcleo del motor y API pública
  Tekium.Embedded.Generators/    Generador de código fuente: serializador, campos, índices y restricciones de cada [TkTable]
  Tekium.Embedded.Linq/          Consultas con predicados de C#, traducidos a filtros del motor
  Tekium.Embedded.Sql/           SQL y proveedor ADO.NET
  Tekium.Embedded.Vectors/       Búsqueda vectorial HNSW, en su propio paquete
tests/
  Tekium.Embedded.Core.Tests/    Pruebas unitarias
  Tekium.Embedded.Vectors.Tests/ Pruebas del índice vectorial, contra fuerza bruta
  Tekium.Embedded.CrashChild/    Proceso que las pruebas de crash lanzan y matan a mitad de un commit
  Tekium.Embedded.Benchmarks/    Benchmarks con BenchmarkDotNet
  Tekium.Embedded.AotSmoke/      Consumidor mínimo publicado con Native AOT
docs/benchmarks/                 Resultados de benchmarks publicados por fase
.github/workflows/ci.yml         Build, pruebas y AOT en Linux, Windows y macOS
scripts/test-linux.sh            Lo mismo dentro de un contenedor Linux, para verificar sin CI
BannedSymbols.txt                APIs prohibidas en todo el repositorio
src/BannedSymbols.Engine.txt     APIs prohibidas dentro del motor
CLAUDE.md                        Reglas de trabajo y de rendimiento
TekiumDB-plan.md                 Diseño completo y fases de entrega
```

## Principios de ingeniería

- **Las pruebas son el producto.** Nadie adopta una base de datos en la que no confía. Todo se escribe con TDD, y el motor se valida con pruebas de propiedades contra un modelo de referencia, pruebas de crash en cada punto del commit e inyección de fallos de E/S.
- **Async de punta a punta.** Sin sync-over-async, sin `lock` en código asíncrono, `ConfigureAwait(false)` en toda la librería.
- **Cero asignaciones en el camino caliente.** Buffers alquilados, claves comparadas como `ReadOnlySpan<byte>`, cursores `ref struct`.
- **Compatible con AOT, nunca obligatorio.** La misma librería corre bajo Native AOT y bajo JIT; quien la consume elige.
- **Ninguna optimización sin medición.** Cada una va con su número antes y después, medido con BenchmarkDotNet.

Varias de estas reglas las impone el build: `.Result`, `.Wait()`, `FileStream`, `Guid.ToByteArray()` sin `bigEndian` y la falta de `ConfigureAwait(false)` en `/src` son errores de compilación.

## Fuera de alcance

Decisiones de diseño, no pendientes:

- Triggers y vistas materializadas. En su lugar: restricciones declarativas, índices sobre expresiones y change feed post-commit.
- Acceso desde varios procesos al mismo archivo.
- Replicación y sincronización.
- Almacenamiento columnar, caché de resultados de consulta e hilos de mantenimiento en segundo plano.
- Compatibilidad de formato con VistaDB.

## Contribuir

El repositorio es privado mientras el motor se estabiliza. Para quien trabaje en él:

1. Leer la sección de la fase en curso en [`TekiumDB-plan.md`](TekiumDB-plan.md) y las reglas de [`CLAUDE.md`](CLAUDE.md).
2. Escribir primero la prueba y verla fallar.
3. No adelantar trabajo de fases futuras.
4. Toda dependencia nueva debe ser compatible con MIT, y el núcleo no admite dependencias en runtime.
5. Cada archivo `.cs` lleva la cabecera de licencia; el build la exige.

## Licencia

[MIT](LICENSE). Copyright (c) 2026 Tekium.
