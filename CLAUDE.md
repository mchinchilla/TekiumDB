# TekiumDB

Base de datos embebida de archivo único (`.tkdb`), cifrada, async-first, B+Tree copy-on-write + MVCC, sin dependencias nativas. El diseño completo está en `TekiumDB-plan.md`: leer la sección de la fase en curso antes de tocar código.

## Cómo se trabaja

- **Una fase a la vez.** Las fases del plan son estrictamente secuenciales. No se andamia nada de una fase futura. Una fase termina cuando su criterio de salida se cumple y CI está verde.
- **Decisiones fijadas** (tabla "Decisiones fijadas" del plan): no se renegocian. Si algo empuja a cambiarlas, parar y preguntar. Triggers, vistas materializadas, multi-proceso y compatibilidad con VistaDB se rechazan.
- **TDD al 100%.** Primero la prueba, verla fallar, después el mínimo código que la pasa, después refactor. No entra código de producción sin una prueba que lo haya pedido. Para B+Tree/MVCC la prueba es de propiedades contra un modelo (`SortedDictionary`), no de ejemplos.
- **Async/await siempre.** Toda E/S es asíncrona de punta a punta; no hay métodos síncronos salvo `Dispose`. Ver reglas duras abajo.
- **DDD donde aplica.** Cada capa del núcleo es un contexto con su lenguaje (el del plan) y su contrato interno; ninguna conoce a la de arriba.
  - `Tekium.Embedded.Vectors` (HNSW) referencia al núcleo y ve sus internos (`InternalsVisibleTo`); es el único proyecto de `/src` con una dependencia NuGet en runtime (`System.Numerics.Tensors`).
  - Carpetas/namespaces de `Tekium.Embedded.Core`: `Storage` (E/S, pager), `Security` (cifrado, KDF), `Transactions` (MVCC), `Indexing`, `Catalog`, y la API pública en la raíz. El generador de código vive en `src/Tekium.Embedded.Generators` (netstandard2.0, lo carga el compilador) y viaja dentro del paquete del núcleo.
  - Conceptos del dominio como objetos de valor (`PageSize`, `PageNumber`, `TransactionId`…): `readonly struct`, inmutables, sin estado inválido representable (incluido `default`), validados en la frontera.
  - Los patrones tácticos no pueden costar asignaciones en el camino caliente: nada de clases-entidad por página ni por fila.
- **Layout:** `/src` código de producto, `/tests` pruebas unitarias, benchmarks y el smoke de AOT. El núcleo no referencia a `.Sql`, `.Linq` ni `.Vectors`, y no lleva dependencias NuGet en runtime.
- **Pruebas sobre los dos dispositivos.** Lo que toque el pager se prueba heredando de `DeviceBackedTests`, que lo corre sobre archivo y sobre memoria. Un dispositivo nuevo hereda `StorageDeviceContractTests`. Las pruebas usan `TestDevice` (envuelve el dispositivo real, inyecta fallos y daña bytes), nunca un doble escrito aparte.
- **El B+Tree se prueba contra un modelo.** Todo cambio en `Indexing/` debe pasar `BTreePropertyTests` (secuencias aleatorias con CsCheck y un millón de operaciones contra un `SortedDictionary`), que además verifican la estructura con `BTreeVerifier` y que no se pierda ninguna página. Si CsCheck falla imprime la semilla: se reproduce con `CsCheck_Seed=<semilla>`.
- **El borde derecho del árbol no se parte por la mitad.** Las claves que crecen con desorden local (UUID v7 de un mismo milisegundo) caen en las dos últimas hojas: la última se parte donde cae la clave (el split 100/0 es el caso en que cae al final) y la penúltima, llena, le pasa sus entradas más altas a la última en vez de partirse (`BTree.CanSpill`/`TrySpill`, que decide todo antes de mover nada y actualiza la clave del padre). Fuera de ese borde no se reparte entre hojas: está medido que hacerlo con entradas sueltas no sube la ocupación (`docs/benchmarks/fase-8.md`). Todo cambio pasa `BTreeUuidTests` (ocupación por encima del 90 %) además de las pruebas de propiedades.
- **Los valores grandes viven en cadenas de overflow.** Lo que no cabe en una hoja sale a páginas `Overflow` y la hoja guarda una referencia de 8 bytes (longitud y primera página). Una cadena nunca se modifica: se escribe entera y se libera entera. Todo camino que quite o reemplace una entrada debe liberar su cadena (`FreeChainAsync`), y todo lo que copie celdas entre hojas debe conservar la marca de overflow; `BTreeOverflowTests` y las pruebas de propiedades (que mezclan valores grandes) lo comprueban contando páginas.
- **La compresión va delante de la protección de la página y nada más la conoce.** `CompressedPageProtector` envuelve al checksum o al cifrado: comprimir, después proteger. En memoria una página es igual haya estado comprimida o no. Está medido que, con cada página en su casilla, no ahorra disco (`docs/benchmarks/fase-5.5.md`): no se enciende por defecto ni se "mejora" sin volver a medir.
- **El commit se prueba matándolo.** Todo cambio en `Pager.CommitAsync`, en la free-list o en el orden de escrituras y flushes debe pasar `PowerLossTests` (corte de energía simulado antes de cada operación, con lo no vaciado perdido a pedazos) y `ProcessCrashTests` (un proceso hijo, `tests/Tekium.Embedded.CrashChild`, que se mata en cada punto). Las dos usan la misma carga (`CrashWorkload`) y la misma exigencia (`CrashRecovery`). Lo que toque lectores concurrentes debe pasar `ConcurrencyTests`.
- **Un commit de resultado incierto detiene al escritor.** Si falla la escritura del slot de raíz o su flush, el pager no acepta más escrituras hasta reabrir: no se "arregla" para que siga. Pasado ese punto el commit tampoco observa la cancelación.
- **No hay CI por ahora: se verifica en local.** Antes de dar una fase por cerrada: `dotnet test -c Release` y el smoke de AOT en macOS, y `scripts/test-linux.sh` (Docker) para Linux. Windows queda sin verificar y se dice.
- **El formato de fila y el catálogo son formato de archivo.** Los valores de `TkColumnType` no se renumeran; la disposición de la fila (versión, mapa de nulos, valores) y las claves del catálogo no cambian sin subir la versión de formato. Un cambio en `TkRowWriter`/`TkRowReader` pasa `RoundTripTests`, `RowFormatTests` y `SchemaEvolutionTests`.
- **La codificación ordenada de claves es formato de archivo.** `TkKeyWriter` escribe cada valor de modo que comparar los bytes sea comparar los valores, y que ningún valor codificado sea prefijo de otro (por eso varias columnas seguidas ordenan como la tupla). Las entradas de todos los índices son esos bytes: no se cambia sin subir la versión de formato, y todo cambio pasa `TkKeyWriterTests` (orden para los 21 tipos, contra `CompareTo`).
- **Un índice es otro árbol y se mantiene en la misma transacción que la fila.** Su entrada es `valores indexados ‖ clave primaria`, y el valor guarda dónde empieza la clave. El catálogo guarda la definición de cada índice (`IndexDefinition.Encoded`); un índice cuya definición guardada no es idéntica a la declarada no se lee nunca, y la siguiente escritura con ese tipo lo reconstruye o lo elimina. Todo cambio en `Table` que toque escrituras pasa `IndexTests`, que compara cada consulta contra un modelo y cuenta las entradas de cada índice.
- **Una operación que falla después de empezar a tocar árboles envenena la transacción** (`TkWriteTransaction.Fail`): la fila y sus índices son varios árboles y pueden haber quedado en desacuerdo, así que sólo puede deshacerse. Lo que pueda rechazarse (clave de índice demasiado larga, clave duplicada) se rechaza antes de tocar nada.
- **Las restricciones se comprueban antes de tocar nada, y una violación aborta la transacción.** `Table.EnforceAsync` corre CHECK, después FOREIGN KEY, después UNIQUE, sobre la fila que se va a escribir; `EnsureNotReferredAsync` antes de borrar. Toda violación pasa por `TkWriteTransaction.Fail` y lanza `TkConstraintException` con la restricción, la tabla y la clave de la fila. UNIQUE es una propiedad de su índice (otro byte de formato en su definición); CHECK y FOREIGN KEY van en una entrada del catálogo por tabla (`ConstraintSet`), y la tabla referida guarda quién se refiere a ella. Una restricción nueva se valida contra las filas existentes en la primera escritura. Todo cambio pasa `ConstraintTests`, que incluye escrituras al azar contra un modelo con las mismas reglas.
- **El change feed es comparar dos estados, no un registro aparte.** `BTree.DiffAsync` compara el árbol de la tabla antes y después de un commit saltándose las páginas que comparten (una página compartida dice lo mismo en los dos, porque nunca se reescribe). Cada commit anota en su catálogo dónde estaba el anterior (`HistoryStore`), y el pager no reutiliza las páginas de los estados desde el piso de retención (`SetRetentionFloor`) ni de los fijados uno por uno (`TryRetain`). El piso se repone al abrir, antes de la primera escritura. Una historia con un hueco se borra o se marca incompleta: nunca se informa como completa. Todo cambio pasa `BTreeDiffTests` y `ChangeFeedTests`.
- **Una transacción puede volcar sus páginas antes del commit, y sólo en puntos seguros.** `WriteTransaction.SpillAsync` escribe al archivo las páginas terminadas y las suelta; nada las alcanza hasta el cambio de raíz, así que un rollback o un corte las deja como espacio sin usar. Después de volcar, esas páginas se leen del archivo y cambiarlas las copia. Nadie puede tener una página de la transacción en la mano mientras se vuelca (un recorrido suyo, un payload a medio llenar): por eso sólo la usa la carga masiva, cuyas piezas entregan cada página ya completa. Todo cambio pasa `SpillTests` (incluye corte de energía en cada operación).
- **La carga masiva son tres piezas.** `BTreeBuilder` construye un árbol de abajo hacia arriba desde entradas en orden; `RunSorter` ordena más de lo que cabe en memoria, con las tandas en páginas temporales del propio archivo (pasan por el pager: cifradas si la base lo está); `Table.BulkInsertAsync` manda las filas en orden directo al árbol y lo demás por el ordenador, y reconstruye cada índice mezclando lo que había con lo nuevo. Cada una se prueba contra un modelo (`BTreeBuilderTests`, `RunSorterTests`, `BulkInsertTests`).
- **Un zone map no es un índice por fila.** Es un árbol chico con una entrada por ventana de tiempo: la clave más baja y la más alta de las filas cuya fecha cae en ella. Las ventanas sólo se ensanchan (borrar no las achica), así que una consulta puede leer de más y descartar, nunca de menos. Lo que una transacción aprende se acumula en memoria (`ZoneWindows`) y se escribe al confirmar, o antes si una consulta de la misma transacción lo necesita o si otro tipo va a cambiar los índices de la tabla. Comparte ciclo de vida con los índices (se construye, reconstruye o elimina según lo que declare el tipo que escribe). Todo cambio pasa `ZoneMapTests`.
- **El índice vectorial vive en `Tekium.Embedded.Vectors` y guarda su grafo en B+Trees del mismo archivo.** No hay página propia de HNSW: nodos (`id → [marcas][nivel][vector][clave]`), listas de vecinos (`id, capa → ids`), claves (`clave → id`) y, si está cuantizado, los vectores originales, son cuatro árboles cuyas raíces van en una entrada del catálogo (`HnswHeader`, prefijo 7). Así hereda copy-on-write, snapshots, cifrado y commit atómico sin código propio. El núcleo sólo sabe que hay participantes del commit (`ITkCommitParticipant`): el índice lleva su cabecera en memoria durante la transacción y la escribe al confirmar. Un vector borrado conserva su nodo, marcado: se atraviesa pero no se devuelve ni se elige como vecino; el espacio sólo vuelve con `DropAsync`. La cabecera y los registros son formato de archivo. Todo cambio pasa `HnswIndexTests` (recall contra fuerza bruta, transacciones, corte de energía, páginas perdidas), y lo que toque la calidad del grafo se mide con `-- vector` antes y después.
- **Las herramientas leen sin confiar y nunca cambian el archivo que reciben.** `OpenedFile` lee la cabecera como puede (segunda copia si la primera está dañada, el slot de raíz que pase su comprobación) y `TreeWalker` recorre los árboles anotando hallazgos en vez de lanzar: una página que no se puede leer se salta con lo que cuelga de ella. Sobre ese recorrido están el inspector (`TkInspector`), y compactar y reparar (`TkMaintenance`, con `DatabaseCopier`), que escriben siempre un archivo nuevo reconstruyendo cada árbol con `BTreeBuilder`. Reparar deja fuera lo que no puede garantizar: el índice dañado o de una tabla que perdió filas (el motor reconstruye el índice que no encuentra) y el índice vectorial con cualquier daño. Una entrada nueva del catálogo que apunte a árboles tiene que enseñarse a `DatabaseWalk.Describe` y a `DatabaseCopier`, o esos árboles no se revisan ni se copian. Todo cambio pasa `InspectorTests` (daña cada página del archivo, una por una), `CopyTests` y `CliTests`.
- **El backup en caliente es la copia de la Fase 10 sobre un snapshot fijado.** `TekiumDb.BackupAsync` abre una `ReadTransaction` (que impide reutilizar las páginas de ese estado hasta que termina) y copia desde su raíz con `TkMaintenance.CopyTreesAsync`, leyendo por `DirectPageSource`: directo del archivo, comprobando cada página, sin pasar por la caché, para no desalojar lo que la aplicación usa. Una base cifrada nunca se respalda sin cifrar por omisión. Todo cambio pasa `BackupTests`, y lo que pueda tocar la latencia del escritor se mide con `-- backup`.
- **Verificar una base abierta es el inspector sobre un snapshot, leyendo el disco.** `TekiumDb.VerifyAsync` fija un estado y lo recorre con `TreeWalker` por `DirectPageSource`, más `FreeListCheck`: comprueba lo que hay en el archivo, no lo que hay en la caché. Todo cambio pasa `VerifyTests` (altera cada página en uso bajo la base abierta).
- **Los contadores no existen si no se piden.** Con `DbOptions.Diagnostics` el pager recibe un `PagerDiagnostics` y `TekiumDb` crea un `Meter` (`TkDiagnostics`); sin la opción el campo es nulo y cada punto que contaría sólo comprueba eso: ni cuenta, ni toma la hora. Un contador nuevo sigue esa regla (`_diagnostics?.X()`, `Now()` para las horas) y entra con su prueba en `DiagnosticsTests`; si toca el camino de lectura se mide con `PointReadBenchmarks` antes y después.
- **El viaje en el tiempo es el historial del change feed leído al revés.** No hay anillo de raíces en la cabecera: cada commit anota en su catálogo dónde quedó el estado anterior (`HistoryStore`), y `TekiumDb.AsOfAsync` busca ahí el último commit en o antes de la transacción pedida, lo retiene por separado (`Pager.TryRetain`, para que el escritor pueda recortar el historial sin llevarse sus páginas) y devuelve un `TkReadTransaction` cuyo catálogo es esa raíz. `ChangeRetention` es el horizonte de las dos cosas. Todo cambio pasa `TimeTravelTests`, además de `ChangeFeedTests`.
- **La capa LINQ traduce o rechaza; nunca filtra en memoria.** `Tekium.Embedded.Linq` lee el árbol de expresión de un predicado y arma el mismo `TkFilter` que se escribiría a mano (`PredicateTranslator`); lo que el motor no tiene lanza `NotSupportedException` al construir la consulta, diciendo qué parte. `TkQuery<T>` no implementa `IQueryable` ni `IAsyncEnumerable` a propósito: así los operadores que el motor no hace no compilan, en vez de caer en los de LINQ estándar. Un operador nuevo sólo entra si el motor lo ejecuta. Llega a los campos sin reflexión, por `ITkRow.FindField` (que emite el generador) y `TkField<TRow>.Accept`. `Expression.Compile` está prohibido en todo `/src`. Todo cambio pasa `LinqTests` (cada predicado contra el mismo sobre objetos, y fragmentos que no deben compilar).
- **El planificador no adivina.** Elige por reglas (`Table.Choose`) y un índice parcial sólo entra si cada condición suya cubre el rango que la consulta impone al mismo campo (`KeyRange.Covers`). Toda fila que sale de una consulta se vuelve a comprobar contra el filtro completo: el plan sólo acota la búsqueda. `KeyRangeTests` prueba esas preguntas contra un modelo clave por clave.
- **El generador se prueba de dos formas.** `TableGeneratorTests` lo corre dentro de la prueba (diagnósticos, y que lo generado compile sin advertencias); las pruebas de `Database/` usan tipos generados de verdad. Un tipo de columna nuevo se agrega en `TkColumnType`, lector, escritor, `TkKeyWriter` (su `Write` y `WriteValue`), `TableParser.Map` y `Everything` (el tipo de prueba que los tiene todos). Un diagnóstico nuevo va también en `AnalyzerReleases.Unshipped.md`.
- Código, identificadores y XML docs en inglés. Cada `.cs` lleva la cabecera de licencia (la exige `IDE0073`). Toda dependencia debe ser compatible con MIT.

## Comandos

```
dotnet build -c Release
dotnet test
dotnet test --filter-class "*PageSizeTests"
dotnet test -c Release --filter-method "*One_million*" --explicit only   # prueba de 4 GiB, no corre en CI
dotnet test -c Release --filter-method "*for_several_minutes*" --explicit only   # concurrencia larga, no corre en CI
dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- space        # amplificación de espacio
dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- feed         # espacio que cuesta el change feed
dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- vector 1000000 128     # HNSW: construcción, recall@10 y latencia; tarda (también: quantized, uniform)
dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- backup 1000000       # latencia del escritor con y sin backups corriendo
dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- uuid 1000000         # inserción por UUID: tiempo y ocupación de hoja según cómo llegan
dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- range 10000000       # rango de fechas: zone map, índice y SQLite
dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- bulk bulk 10000000   # carga masiva; otros modos: one, shuffled, indexed-bulk, sqlite…
dotnet run --project src/Tekium.Embedded.Cli -- check app.tkdb                    # tkdb: info, check, compact, repair, passwd
dotnet publish tests/Tekium.Embedded.AotSmoke -c Release -o artifacts/aot && ./artifacts/aot/Tekium.Embedded.AotSmoke
dotnet run -c Release --project tests/Tekium.Embedded.Benchmarks -- --filter '*'
scripts/test-linux.sh                                                             # build, pruebas y AOT en Linux (Docker)
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
