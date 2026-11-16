# TekiumDB

**Base de datos embebida para .NET: un solo archivo, cifrada, asíncrona de verdad y sin binarios nativos.**

![.NET 11](https://img.shields.io/badge/.NET-11-512BD4)
![Licencia MIT](https://img.shields.io/badge/licencia-MIT-green)
![Estado: pre-alpha](https://img.shields.io/badge/estado-pre--alpha-orange)

TekiumDB guarda todo en un único archivo `.tkdb` y se distribuye como un paquete NuGet 100% administrado. Su motor es un B+Tree copy-on-write con MVCC: los lectores nunca bloquean ni esperan, y el commit es atómico sin WAL ni journal.

> [!WARNING]
> **Proyecto en fase temprana (Fase 6 de la hoja de ruta).** Ya hay una API pública que se puede usar: tablas tipadas con serializadores generados en compilación, índices secundarios (de columna, de varias, sobre expresiones y parciales) con consultas por igualdad y por rango, transacciones con aislamiento por snapshot, cifrado y modo en memoria, sobre el motor de las fases anteriores (pager, B+Tree copy-on-write, commit atómico probado matando el proceso). Todavía no hay restricciones, índices especializados ni paquete publicado, y el formato de archivo puede cambiar. Ver [Estado y hoja de ruta](#estado-y-hoja-de-ruta).

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

- Temporal: zone maps por hoja y split 100/0 para series de tiempo.
- UUID v7: localidad temporal aprovechada, hojas llenas en inserción secuencial.
- Vectorial: HNSW persistido, con métricas coseno, L2 y producto interno, y cuantización `int8` opcional.
- Secundarios genéricos, sobre expresiones y parciales (ya existen: ver [Índices y consultas](#índices-y-consultas)).

**API y ecosistema**

- Núcleo tipado, sin reflexión en runtime: un generador de código fuente emite los serializadores.
- Capas opcionales en paquetes separados: SQL con proveedor ADO.NET, LINQ y vectores.
- Restricciones declarativas (`CHECK`, `UNIQUE`, `FOREIGN KEY`) y change feed post-commit.
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
| Operaciones | `InsertAsync`, `UpdateAsync`, `UpsertAsync`, `DeleteAsync`, `GetAsync`, `ScanAsync`, `QueryAsync`, `ExplainAsync` |
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

Lo que sigue es diseño objetivo y todavía no existe:

```csharp
var hits = await docs.SearchVectorAsync(
    x => x.Embedding, queryVector, topK: 10, efSearch: 64);
```

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
| 6.5 – 6.8 | Restricciones declarativas, change feed y carga masiva | Pendiente |
| 7 – 9 | Índices temporal, UUID v7 y vectorial HNSW | Pendiente |
| 10 – 10.6 | Herramientas, backup en caliente, verificación y viaje en el tiempo | Pendiente |
| 11 – 12 | Capas LINQ y SQL con proveedor ADO.NET | Pendiente |
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
  Tekium.Embedded.Core/          Núcleo del motor y API pública
  Tekium.Embedded.Generators/    Generador de código fuente: el serializador, los campos y los índices de cada [TkTable]
tests/
  Tekium.Embedded.Core.Tests/    Pruebas unitarias
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
