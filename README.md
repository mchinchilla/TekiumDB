# TekiumDB

**Base de datos embebida para .NET: un solo archivo, cifrada, asíncrona de verdad y sin binarios nativos.**

![.NET 11](https://img.shields.io/badge/.NET-11-512BD4)
![Licencia MIT](https://img.shields.io/badge/licencia-MIT-green)
![Estado: pre-alpha](https://img.shields.io/badge/estado-pre--alpha-orange)

TekiumDB guarda todo en un único archivo `.tkdb` y se distribuye como un paquete NuGet 100% administrado. Su motor es un B+Tree copy-on-write con MVCC: los lectores nunca bloquean ni esperan, y el commit es atómico sin WAL ni journal.

> [!WARNING]
> **Proyecto en fase temprana (Fase 1 de la hoja de ruta).** Hoy existen el pager (caché acotada, checksums por página, free-list, commit atómico) y un B+Tree copy-on-write mínimo, medidos contra SQLite. No hay API pública, cifrado, índices secundarios ni paquete publicado. La API y las características descritas aquí son el diseño objetivo, no funcionalidad disponible. Ver [Estado y hoja de ruta](#estado-y-hoja-de-ruta).

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

Antes de invertir en el resto, la Fase 0.5 midió un motor mínimo contra `Microsoft.Data.Sqlite`: lectura puntual 6× más rápida, rango de fechas 38× más rápido e inserción secuencial entre empate y 5× más rápida. Los números, las condiciones y sus advertencias están en [`docs/benchmarks/fase-0.5.md`](docs/benchmarks/fase-0.5.md). Con el pager real de la Fase 1 las lecturas siguen entre 4,7× y 38× por delante; la inserción con commits pequeños quedó 1,28× por detrás ([`docs/benchmarks/fase-1.md`](docs/benchmarks/fase-1.md)).

## Características

Todas forman parte del diseño. Hoy existen el pager y, en versión mínima, el B+Tree copy-on-write con commit atómico.

**Almacenamiento**

- Archivo único `.tkdb` con páginas de tamaño fijo (8 KiB por defecto; 4, 16 o 32 KiB configurables).
- B+Tree copy-on-write con MVCC: un escritor, lectores ilimitados sin bloqueo, cada uno sobre su snapshot inmutable.
- Commit atómico por slots de raíz alternados. Si el proceso muere a mitad de un commit, la base abre en el estado anterior, sin recuperación.
- Compresión Zstandard por página, opcional.
- Modo en memoria para pruebas (el dispositivo ya existe; falta exponerlo en la API pública).

**Seguridad**

- AES-256-GCM por página con la criptografía del BCL. El tag de autenticación es también el checksum: corrupción y manipulación se detectan al descifrar.
- Derivación de clave con Argon2id. Cambiar la contraseña reescribe sólo la cabecera.

**Índices**

- Temporal: zone maps por hoja y split 100/0 para series de tiempo.
- UUID v7: localidad temporal aprovechada, hojas llenas en inserción secuencial.
- Vectorial: HNSW persistido, con métricas coseno, L2 y producto interno, y cuantización `int8` opcional.
- Secundarios genéricos, sobre expresiones y parciales.

**API y ecosistema**

- Núcleo tipado, sin reflexión en runtime: un generador de código fuente emite los serializadores.
- Capas opcionales en paquetes separados: SQL con proveedor ADO.NET, LINQ y vectores.
- Restricciones declarativas (`CHECK`, `UNIQUE`, `FOREIGN KEY`) y change feed post-commit.
- Backup en caliente, verificación de integridad y viaje en el tiempo sobre snapshots.

## Vistazo a la API

Diseño objetivo del núcleo tipado. Todavía no compila contra este repositorio.

```csharp
await using var db = await TekiumDb.OpenAsync(new DbOptions
{
    Path = "app.tkdb",
    Password = password,
    PageSize = 8192,
    CacheSizeMb = 256
});

var docs = db.Table<Document>();

await using (var tx = await db.BeginWriteAsync())
{
    await docs.InsertAsync(tx, doc);
    await tx.CommitAsync();
}

await foreach (var d in docs.RangeAsync(x => x.CreatedAt, from, to))
    Process(d);

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

Las fases son estrictamente secuenciales: cada una termina con su criterio de salida cumplido y CI en verde antes de empezar la siguiente.

| Fase | Entrega | Estado |
| --- | --- | --- |
| 0 | Esqueleto de la solución, CI, BenchmarkDotNet, analizadores | Hecho en macOS; pendiente confirmar CI en Linux y Windows |
| 0.5 | Spike de validación: pager y B+Tree mínimos medidos contra SQLite | Hecho: [dentro del criterio](docs/benchmarks/fase-0.5.md), se sigue |
| 1 | Capa de E/S y pager: caché de páginas, checksums, free-list | Hecho en macOS: [criterio cumplido](docs/benchmarks/fase-1.md); pendiente confirmar CI en Linux y Windows |
| 1.5 | Modo en memoria | Hecho en macOS: las pruebas del pager corren sobre archivo y sobre memoria; pendiente confirmar CI en Linux y Windows |
| 2 | Cifrado por página y derivación de clave | Pendiente |
| 3 | B+Tree copy-on-write | Pendiente |
| 4 | MVCC, transacciones y commit atómico | Pendiente |
| 5 – 5.5 | Catálogo, filas tipadas, generador de código y compresión | Pendiente |
| 6 – 6.8 | Índices secundarios, restricciones, change feed y carga masiva | Pendiente |
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
```

Las pruebas usan xUnit v3 sobre Microsoft.Testing.Platform, por eso los filtros son `--filter-class` y `--filter-method` en lugar de `--filter`.

## Estructura del repositorio

```
src/
  Tekium.Embedded.Core/          Núcleo del motor
tests/
  Tekium.Embedded.Core.Tests/    Pruebas unitarias
  Tekium.Embedded.Benchmarks/    Benchmarks con BenchmarkDotNet
  Tekium.Embedded.AotSmoke/      Consumidor mínimo publicado con Native AOT
docs/benchmarks/                 Resultados de benchmarks publicados por fase
.github/workflows/ci.yml         Build, pruebas y AOT en Linux, Windows y macOS
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
