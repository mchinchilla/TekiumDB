# TekiumDB

Base de datos embebida de archivo único (`.tkdb`), cifrada, async-first, B+Tree copy-on-write + MVCC, sin dependencias nativas. El diseño completo está en `TekiumDB-plan.md`: leer la sección de la fase en curso antes de tocar código.

## Cómo se trabaja

- **Una fase a la vez.** Las fases del plan son estrictamente secuenciales. No se andamia nada de una fase futura. Una fase termina cuando su criterio de salida se cumple y CI está verde.
- **Decisiones fijadas** (tabla "Decisiones fijadas" del plan): no se renegocian. Si algo empuja a cambiarlas, parar y preguntar. Triggers, vistas materializadas, multi-proceso y compatibilidad con VistaDB se rechazan.
- **TDD al 100%.** Primero la prueba, verla fallar, después el mínimo código que la pasa, después refactor. No entra código de producción sin una prueba que lo haya pedido. Para B+Tree/MVCC la prueba es de propiedades contra un modelo (`SortedDictionary`), no de ejemplos.
- **Async/await siempre.** Toda E/S es asíncrona de punta a punta; no hay métodos síncronos salvo `Dispose`. Ver reglas duras abajo.
- **DDD donde aplica.** Cada capa del núcleo es un contexto con su lenguaje (el del plan) y su contrato interno; ninguna conoce a la de arriba.
  - Carpetas/namespaces de `Tekium.Embedded.Core`: `Storage` (E/S, pager), `Security` (cifrado, KDF), `Transactions` (MVCC), `Indexing`, `Catalog`, y la API pública en la raíz.
  - Conceptos del dominio como objetos de valor (`PageSize`, `PageNumber`, `TransactionId`…): `readonly struct`, inmutables, sin estado inválido representable (incluido `default`), validados en la frontera.
  - Los patrones tácticos no pueden costar asignaciones en el camino caliente: nada de clases-entidad por página ni por fila.
- **Layout:** `/src` código de producto, `/tests` pruebas unitarias, benchmarks y el smoke de AOT. El núcleo no referencia a `.Sql`, `.Linq` ni `.Vectors`, y no lleva dependencias NuGet en runtime.
- **Pruebas sobre los dos dispositivos.** Lo que toque el pager se prueba heredando de `DeviceBackedTests`, que lo corre sobre archivo y sobre memoria. Un dispositivo nuevo hereda `StorageDeviceContractTests`. Las pruebas usan `TestDevice` (envuelve el dispositivo real, inyecta fallos y daña bytes), nunca un doble escrito aparte.
- Código, identificadores y XML docs en inglés. Cada `.cs` lleva la cabecera de licencia (la exige `IDE0073`). Toda dependencia debe ser compatible con MIT.

## Comandos

```
dotnet build -c Release
dotnet test
dotnet test --filter-class "*PageSizeTests"
dotnet test -c Release --filter-method "*One_million*" --explicit only   # prueba de 4 GiB, no corre en CI
dotnet publish tests/Tekium.Embedded.AotSmoke -c Release -o artifacts/aot && ./artifacts/aot/Tekium.Embedded.AotSmoke
dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- --filter '*'
```

Las pruebas corren con xUnit v3 sobre Microsoft.Testing.Platform (`global.json`), no VSTest: los filtros son `--filter-class`, `--filter-method`, etc.

## Reglas duras de rendimiento y asincronía

Varias las impone el build (`BannedSymbols.txt`, `src/BannedSymbols.Engine.txt`, `.editorconfig`, warnings como errores). No se silencian con `#pragma` ni `NoWarn` sin preguntar.

### Asignaciones

- Cero asignaciones en el camino de lectura caliente. `ArrayPool<byte>.Shared` para buffers temporales, devueltos siempre en `try`/`finally`.
- Las páginas en caché son `Memory<byte>` sobre buffers alquilados, nunca `byte[]` nuevos por lectura.
- Claves comparadas sobre `ReadOnlySpan<byte>` con `SequenceCompareTo`. Nada de `string` ni `Encoding.GetString` dentro del motor.
- `stackalloc` para claves pequeñas con umbral fijo y fallback al pool. Nunca dentro de un bucle.
- Cursores como `readonly ref struct`.

### Asincronía

- `ValueTask` en lo que acierta en caché la mayoría de las veces; `Task` sólo en superficie pública que se espere más de una vez.
- `ConfigureAwait(false)` en toda la librería (`/src`). Sin excepciones.
- Nunca `lock` ni `Monitor` en código async: `SemaphoreSlim`.
- Nunca `.Result`, `.Wait()` ni `GetAwaiter().GetResult()`. Ni en pruebas.
- E/S con `RandomAccess.ReadAsync`/`WriteAsync` sobre `SafeFileHandle`, no `FileStream`.
- Fast path síncrono cuando la página ya está en caché: `ValueTask` completada, sin tocar el scheduler.
- Toda operación acepta `CancellationToken`; escaneos como `IAsyncEnumerable<T>` con `[EnumeratorCancellation]`; transacciones `IAsyncDisposable`.

### Formato y tipos

- Little-endian explícito con `BinaryPrimitives`. Nunca `MemoryMarshal.Read<T>` sobre structs.
- `Guid.CreateVersion7()` y `ToByteArray(bigEndian: true)` / `TryWriteBytes(..., bigEndian: true, ...)`. Nunca la sobrecarga sin `bigEndian`.
- Nada de `object`, `dynamic`, reflexión en runtime ni `Expression.Compile()` en el núcleo.
- Criptografía sólo del BCL (`AesGcm`, `HKDF`, `HMACSHA256`), salvo Argon2id y BLAKE2b, que .NET no trae y viven en `Security/`; se validan contra vectores de referencia y no se tocan sin ellos.
- La construcción del nonce (`página || txid`) no se toca sin revisión, ni tampoco lo que la hace única: la reserva de ids en `Pager.BeginWriteAsync`. Un id de transacción nunca se emite dos veces.

### Compilación

- Compatible con `PublishAot` siempre (`IsAotCompatible` en `/src`); cualquier warning de AOT/trimming es un error.
- `runtime-async=on` sólo después de tener benchmarks estables.
- `unsafe` permitido en el núcleo, justificado caso por caso en comentario.

### Lo que no se hace

No se optimiza nada sin benchmark. Toda optimización va con el número antes y después medido con BenchmarkDotNet (`MemoryDiagnoser` activo, y SQLite/LiteDB como punto de comparación cuando aplique). Sin medición, se revierte.
