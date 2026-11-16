# TekiumDB — plan de construcción para Claude Code

TekiumDB: una base de datos embebida tipo VistaDB, archivo único con extensión .tkdb, cifrada, async-first, con B+Tree copy-on-write + MVCC, índices optimizados para fechas y UUID v7, y búsqueda vectorial HNSW. Distribuible como un solo paquete NuGet sin dependencias nativas.

## Decisiones fijadas

Estas decisiones no se renegocian durante la implementación. Si Claude Code propone cambiarlas, debe parar y preguntar.

| Decisión | Elección | Por qué |
| --- | --- | --- |
| Superficie de API | Núcleo tipado + SQL en paquete aparte | El core no paga el costo del parser; la capa SQL se agrega sin tocar el motor |
| Motor de almacenamiento | B+Tree copy-on-write + MVCC | Lectores nunca bloquean ni esperan; commit atómico sin WAL separado; archivo único |
| Concurrencia | In-process, un solo proceso | Sin file locking entre procesos; el cifrado y el MVCC se simplifican mucho |
| Índice vectorial | HNSW persistido | Mejor recall/latencia hasta millones de vectores; detrás de `IVectorIndex` para enchufar DiskANN después |
| Cifrado | AES-256-GCM por página, Argon2id para derivar | Autenticado: detecta corrupción y manipulación en la misma operación que descifra |
| Compilación | AOT-compatible, nunca AOT-obligatorio | La misma librería corre bajo AOT y bajo JIT; quien la consume elige, y la disciplina es la misma |

Todo lo demás —nombres de tipos, layout de bytes dentro de la página, estrategia de free-list— queda a criterio de la implementación, siempre que respete las reglas de rendimiento más abajo.

## Posicionamiento

El rendimiento no es el diferenciador de este producto, y conviene tenerlo claro antes de escribir la primera línea. Un B+Tree copy-on-write bien escrito en .NET 11 con disciplina de cero asignaciones le gana a LocalDB, a Firebase local y a VistaDB sin heroísmos: LocalDB es SQL Server Express con un proceso aparte y un arranque pesado, Firebase en local es sobre todo red y caché, y VistaDB es un motor maduro pero de arquitectura vieja. Esa comparación no prueba nada.

### El competidor real es SQLite

| Carga | Expectativa honesta |
| --- | --- |
| Lectura puntual por clave | Empate o ventaja: no hay marshaling de P/Invoke y las páginas se quedan como spans en memoria manejada |
| Escaneo de rango | Empate o ventaja, por los zone maps del índice temporal |
| Inserción secuencial por UUID v7 | Ventaja, por el split 100/0 |
| Inserción aleatoria | Empate, en el mejor caso |
| SQL complejo: joins, estimación de cardinalidad, reordenamiento | Desventaja clara, y perseguirla es donde el proyecto se muere |

### Lo que justifica el producto

Ninguna de estas cinco cosas se consigue con SQLite sin dolor, y son la razón de ser del motor:

- **Sin binarios nativos.** SQLite arrastra un `.so`, `.dll` o `.dylib` por RID. Eso duele exactamente donde este motor apunta: Native AOT, single-file, móvil.
- **Async de verdad.** La API asíncrona de `Microsoft.Data.Sqlite` es síncrona por debajo. La de este motor no lo sería. Es el argumento más fuerte y el más fácil de demostrar.
- **Búsqueda vectorial en la caja**, sin `sqlite-vec` ni extensiones nativas.
- **Cifrado autenticado incluido**, sin SEE comercial ni SQLCipher.
- **UUID v7 y fechas como ciudadanos de primera**, no como `BLOB` con un índice encima.

"SQLite pero más rápido" no es una propuesta. "SQLite sin binarios nativos, con async real, vectores y cifrado" sí lo es.

### Los dos riesgos que no son técnicos

**La confianza, no la velocidad, es lo que mata estos proyectos.** La reputación de SQLite descansa en una proporción de pruebas a código que no tiene comparación en la industria. Nadie adopta una base de datos en la que no confía, y ganarse eso cuesta más esfuerzo que el motor mismo. Las pruebas de propiedades y las de crash no son una fase: son el producto.

**El GC y la durabilidad real.** La disciplina de cero asignaciones mitiga las pausas, pero no las elimina, y SQLite no las tiene. Y el comportamiento de `fsync` entre sistemas operativos —`F_FULLFSYNC` en macOS, unidades de red, sistemas de archivos que mienten sobre el flush— es donde se esconden los bugs que destruyen reputaciones. Hay que probarlo explícitamente en los tres sistemas desde la Fase 1.

### La fase de validación

Por eso el plan incluye una **Fase 0.5** antes de comprometerse con el resto: una versión mínima del pager y del B+Tree, sin cifrado, sin índices secundarios, sin catálogo, medida contra SQLite en lectura puntual, rango de fechas e inserción secuencial.

Son dos semanas de trabajo y responden la única pregunta que importa antes de invertir dos años. Si los números quedan dentro de un margen razonable, el proyecto es viable y ya tenés la cifra que justifica seguir. Si quedan un orden de magnitud abajo, lo sabés a tiempo.

## Setup en Claude Code

Primero el marketplace de .NET, después los plugins. `dotnet/skills` se instala como marketplace y de ahí salen los plugins por separado.

```
/plugin marketplace add dotnet/skills
/plugin install dotnet@dotnet-agent-skills
/plugin install dotnet11@dotnet-agent-skills
/plugin install dotnet-diag@dotnet-agent-skills
/plugin install dotnet-test@dotnet-agent-skills
/plugin install dotnet-nuget@dotnet-agent-skills
```

| Plugin | Para qué sirve aquí |
| --- | --- |
| `dotnet` | Skills generales de .NET y C#, más integración con el language server |
| `dotnet11` | APIs y features nuevas de .NET 11 y C# 15 — union types, Runtime Async, los SIMD nuevos |
| `dotnet-diag` | Investigación de rendimiento, debugging e incidentes; es el que más se va a usar |
| `dotnet-test` | Ejecución, generación y cobertura de pruebas |
| `dotnet-nuget` | Empaquetado y gestión de dependencias |

Se saltan `dotnet-data` (es para EF Core y acceso a datos, no para escribir un motor) y `dotnet-advanced` salvo que haga falta después. Después de instalar hay que reiniciar y verificar con `/skills`.

El skill `database-architect` se instala aparte:

```
npx skills add sickn33/agentic-awesome-skills --skill database-architect --agent claude-code
```

Una advertencia honesta sobre ese skill: está escrito para **elegir y modelar** sobre motores existentes —PostgreSQL contra MongoDB, esquemas, particionamiento, réplicas— no para implementar un motor de almacenamiento. Va a servir para el diseño del modelo de datos público y la estrategia de índices, y no va a aportar nada al B+Tree ni al HNSW. Conviene invocarlo explícitamente en la Fase 0 y no dejarlo auto-cargarse después.

Además conviene crear un `CLAUDE.md` en la raíz del repo con las reglas duras de rendimiento de más abajo, porque son las que un agente rompe sin darse cuenta.

Fuentes: [dotnet/skills](https://github.com/dotnet/skills) · [database-architect](https://www.skills.sh/sickn33/antigravity-awesome-skills/database-architect)

## Arquitectura

Tres paquetes opcionales sobre un núcleo de siete capas:

```
Tekium.Embedded.Sql          Tekium.Embedded.Linq        Tekium.Embedded.Vectors
(SQL + proveedor ADO.NET)    (IQueryable tipado)         (HNSW + cuantización)
         |                            |                            |
         v                            v                            v
+-------------------------------------------------------------------------+
| Tekium.Embedded.Core — sin dependencias nativas, AOT-friendly           |
|  API pública         IDatabase, ITable, ITransaction, IAsyncEnumerable  |
|  Ejecutor y catálogo planes, cursores, serialización de filas sin boxing|
|  Gestor de índices   B+Tree, temporal, UUID v7, IVectorIndex            |
|  MVCC                snapshots, raíz de versiones, commit atómico       |
|  Pager               caché de páginas, free-list, copy-on-write         |
|  Cifrado             AES-256-GCM por página, Argon2id                   |
|  E/S                 RandomAccess, sin P/Invoke ni binarios nativos     |
+-------------------------------------------------------------------------+
```

Las flechas son dependencias. `.Sql`, `.Linq` y `.Vectors` referencian al núcleo; el núcleo no referencia a ninguno. Esa regla es la que mantiene el paquete base pequeño y compilable con Native AOT.

Cada capa expone su contrato como interfaz interna y se prueba sola. El `Pager` no sabe que existe el B+Tree; el B+Tree no sabe que las páginas van cifradas.

## Formato de archivo

Un solo archivo, dividido en páginas de tamaño fijo. El tamaño de página se fija al crear la base y se guarda en la cabecera; no se puede cambiar después sin reconstruir.

**Página: 8 KiB por defecto**, configurable a 4, 16 o 32 KiB. 8 KiB es el punto donde un nodo B+Tree con claves de 16 bytes todavía da un fan-out alto (unos 400 hijos) sin desperdiciar caché.

### Cabecera (página 0, sin cifrar)

La página 0 queda en claro a propósito, porque hay que poder leer los parámetros de derivación de clave antes de tener la clave.

| Campo | Bytes | Contenido |
| --- | --- | --- |
| Magic | 8 | Identificador del formato, fijo |
| Versión de formato | 2 | Entero; se rechaza un archivo de versión mayor |
| Tamaño de página | 2 | Potencia de dos entre 4 KiB y 32 KiB |
| Flags | 4 | Cifrado activo, checksums, compresión de páginas |
| Salt de KDF | 16 | Aleatorio por archivo |
| Parámetros Argon2id | 12 | Memoria, iteraciones, paralelismo |
| Raíces de snapshot | N × 48 | Anillo de N slots (mínimo 2, alternados): número de página raíz, id de transacción, conteo de páginas, MAC. N se fija al crear el archivo |
| Reservado | resto | Ceros, para crecer sin romper el formato |

### Resto de páginas

Cada página lleva un encabezado corto en claro —tipo de página, número, id de transacción que la escribió— seguido del cuerpo cifrado. Tipos: nodo interno B+Tree, hoja B+Tree, overflow (valores grandes), free-list, nodo HNSW, catálogo.

Integridad: el tag GCM de 16 bytes **es** el checksum. No hace falta CRC aparte; si la página se corrompió, el descifrado falla. En modo sin cifrado se usa XxHash3 de 8 bytes en su lugar.

### Compresión

Zstandard por página con `ZstandardStream`, activable por archivo y guardado en los flags de la cabecera. El orden es **comprimir y después cifrar**, nunca al revés: el texto cifrado es ruido estadístico y no comprime nada.

Una página comprimida ocupa menos que su tamaño nominal, así que el encabezado en claro lleva la longitud del cuerpo comprimido. La página sigue ocupando su slot completo en el archivo —cambiar eso rompería el direccionamiento por número de página— pero se escriben y se leen menos bytes, que es donde está la ganancia. Nivel 3 por defecto, configurable.

No se comprimen las páginas de free-list ni los nodos HNSW: los vectores ya son densos y la compresión sólo agrega latencia.

### Versión de esquema en la fila

Cada fila arranca con un byte de versión de esquema. El catálogo guarda el historial de versiones de cada tipo, así que una fila vieja se lee con el lector de su versión y los campos que no existían salen con su valor por defecto. Agregar una columna anulable no reescribe ni una página.

### Valores grandes

Un valor que no cabe en una hoja se parte en páginas de overflow encadenadas. La hoja guarda los primeros bytes en línea más el número de la primera página de overflow. Umbral por defecto: un cuarto del tamaño de página.

### Endianness

Todo el formato es little-endian explícito, leído y escrito con `BinaryPrimitives`. Nunca `MemoryMarshal.Read<T>` directo sobre structs, porque el layout cambia entre arquitecturas.

## Motor: B+Tree copy-on-write y MVCC

Ningún dato en disco se sobrescribe nunca. Modificar una hoja escribe una hoja nueva, lo que obliga a escribir un padre nuevo, hasta la raíz. El commit es una única escritura atómica del slot de raíz en la cabecera.

### Flujo de una escritura

1. La transacción toma una referencia al snapshot actual: el número de página raíz vigente.
2. Toda página que vaya a modificar se copia a una página nueva, tomada de la free-list o del final del archivo.
3. Al hacer commit, se escriben todas las páginas nuevas y se emite un `FlushToDisk`.
4. Se escribe el slot de raíz **alterno** en la cabecera y se emite un segundo flush. Ese segundo flush es el punto de durabilidad.
5. Las páginas que el snapshot viejo ocupaba pasan a la free-list, pero sólo cuando ningún lector las tenga retenidas.

Los slots de raíz alternados son lo que da atomicidad: si el proceso muere entre el paso 3 y el 5, el slot viejo sigue íntegro y la base abre en el estado anterior. Sin WAL, sin recuperación, sin journal.

### Concurrencia

Un solo escritor a la vez, serializado con un `SemaphoreSlim(1)` asíncrono —nunca un `lock` dentro de código async. Lectores ilimitados y sin bloqueo: cada uno fija el número de raíz al abrirse y lee ese árbol, que es inmutable por construcción.

Un lector retiene su snapshot en una lista ordenada por id de transacción. El recolector de páginas libres sólo puede liberar páginas cuyo snapshot sea anterior al más viejo lector vivo. Una transacción de lectura olvidada es una fuga de espacio, así que el `IAsyncDisposable` no es opcional y las pruebas tienen que verificarlo.

### Free-list y crecimiento

La free-list se guarda como páginas del mismo archivo, con su propia raíz en la cabecera. Crece el archivo en bloques, no página por página: duplicando hasta 64 MiB y después en saltos de 64 MiB, con `RandomAccess.SetLength` para pedir el espacio de una vez.

Un `CompactAsync` explícito reescribe el archivo entero sin las páginas muertas. Es offline y opcional; copy-on-write amplifica el espacio y hay que asumirlo.

### Nodos

Las claves se guardan con prefijo comprimido dentro del nodo —comparten el prefijo común de la página— y un directorio de offsets al inicio. Buscar dentro del nodo es binario sobre el directorio, sin deserializar nada: se compara sobre `ReadOnlySpan<byte>`.

## Cifrado

AES-256-GCM por página, con `AesGcm` de `System.Security.Cryptography`, que usa AES-NI por hardware. Nada de criptografía escrita a mano.

### Derivación de clave

De la contraseña sale una clave maestra por Argon2id. .NET no lo trae, así que hay dos opciones y hay que elegir una conscientemente:

| Opción | Ventaja | Costo |
| --- | --- | --- |
| Implementar Argon2id en C# puro | Cero dependencias, AOT limpio | Unos cientos de líneas; hay que validar contra los vectores de prueba del RFC 9106 |
| `Konscious.Security.Cryptography.Argon2` | Probado, inmediato | Una dependencia NuGet en el paquete base |
| `Rfc2898DeriveBytes` (PBKDF2-SHA256) | Ya está en el BCL | Mucho más débil contra ataque con GPU |

La recomendación es implementar Argon2id en C# puro, porque la meta de "distribuible con lo menos posible" lo justifica y el algoritmo está bien especificado. Debe quedar detrás de `IKeyDerivation` para poder cambiarlo.

La clave maestra no cifra páginas directamente. Cifra una clave de archivo aleatoria de 32 bytes guardada envuelta en la cabecera. Así cambiar la contraseña es reescribir 48 bytes, no reescribir el archivo entero.

### Nonce

El nonce de 12 bytes se construye como `número de página (4 bytes) || id de transacción (8 bytes)`. Copy-on-write garantiza que esa combinación no se repite nunca, que es exactamente el requisito de GCM. **Esta propiedad es crítica**: reusar un nonce con la misma clave rompe GCM por completo. Debe haber una prueba dedicada que la verifique.

### Qué queda en claro

La página 0 entera y el encabezado corto de cada página. Eso filtra el tamaño de la base, el número de páginas y el tipo de cada página. Es un trade-off aceptable y hay que documentarlo; ocultarlo exigiría cifrar la estructura del archivo, lo que impide leer cualquier cosa sin descifrar todo.

### Manejo de la clave en memoria

La clave vive en un `byte[]` fijado, borrado con `CryptographicOperations.ZeroMemory` al cerrar. No se expone por ninguna propiedad pública, no se registra en logs, no aparece en `ToString()`. La contraseña se recibe como `ReadOnlySpan<char>` y nunca se guarda.

### Rekey

`ChangePasswordAsync` deriva la clave nueva, re-envuelve la clave de archivo y reescribe la cabecera. Es instantáneo. `RotateFileKeyAsync` —reescribir todas las páginas con una clave de archivo nueva— es una operación aparte, offline, y sólo se implementa si hace falta.

## Índices

Tres familias sobre la misma abstracción `IIndex`. Las tres comparten una idea: la clave se codifica a bytes de forma que **el orden lexicográfico de los bytes sea el orden semántico del valor**. Eso permite que el B+Tree sólo compare spans y no sepa nada de tipos.

### Índice temporal

Un `DateTimeOffset` se codifica como ticks UTC big-endian de 8 bytes, con el bit de signo invertido para que las fechas antes de la época ordenen bien. Así un rango de fechas es un escaneo de hoja contiguo, que es la operación que más se usa en un índice de fechas.

Dos cosas que lo hacen mucho mejor que un índice genérico:

- **Zone maps por hoja.** Cada hoja guarda su fecha mínima y máxima en el encabezado. Un rango descarta hojas enteras sin leerlas.
- **Append optimizado.** Las series temporales se insertan casi siempre al final. Detectar que la clave nueva es mayor que la máxima actual permite saltar el split normal y hacer un split 100/0, que deja las hojas llenas en vez de a la mitad.

El offset se guarda aparte del instante. Dos momentos iguales en zonas distintas ordenan igual, que es lo correcto.

### Índice de UUID v7

Un UUID v7 lleva un timestamp de milisegundos en sus primeros 48 bits, así que ya viene ordenado por tiempo de creación. Se guarda tal cual en orden de red —big-endian, los 48 bits de tiempo primero— y el árbol hereda la localidad temporal gratis.

Consecuencias que hay que aprovechar explícitamente:

- Las inserciones caen casi siempre en la hoja de más a la derecha. Vale el mismo split 100/0 del índice temporal.
- La página raíz y el camino derecho se mantienen calientes en caché; conviene fijarlos.
- Un prefijo de tiempo permite responder "todo lo creado entre X e Y" sin un índice de fecha aparte.

.NET ya trae `Guid.CreateVersion7()`. Hay que usarlo, no generar UUIDs a mano. **Ojo con `Guid.ToByteArray()`**: por compatibilidad con Windows devuelve los primeros tres campos en little-endian y rompe el orden. Hay que usar `ToByteArray(bigEndian: true)` en todo el motor, y debe haber una prueba que lo verifique.

### Índice vectorial HNSW

Grafo multicapa, persistido en páginas del mismo archivo. Cada nodo guarda el vector y sus listas de vecinos por capa.

| Parámetro | Valor por defecto | Nota |
| --- | --- | --- |
| `M` | 16 | Vecinos por nodo en capas superiores; 32 en la capa 0 |
| `efConstruction` | 200 | Más alto, mejor grafo, construcción más lenta |
| `efSearch` | 64 | Ajustable por consulta: es la palanca recall/latencia |
| Métricas | coseno, L2, producto interno | Coseno normaliza al insertar |

La distancia se calcula con `System.Numerics.Tensors.TensorPrimitives`, que ya usa SIMD de ancho completo y evita escribir intrínsecos a mano. .NET 11 agrega APIs nuevas de composición SIMD sobre `Vector512<T>` que valen la pena mirar si el perfilado lo pide, pero no antes.

Cuantización escalar a `int8` como opción: reduce la memoria del grafo cuatro veces, con reordenamiento exacto de los candidatos finales sobre los vectores originales para recuperar el recall.

El grafo se carga perezosamente por capas al abrir la base. No se reconstruye desde cero salvo que la versión del formato cambie. Todo detrás de `IVectorIndex`, para que DiskANN pueda entrar después sin tocar el formato de archivo.

## API pública

El núcleo expone una API tipada, asíncrona y sin reflexión en tiempo de ejecución. La capa SQL va aparte y se construye encima de ésta, nunca al revés.

### Forma del núcleo

```csharp
await using var db = await TekiumDb.OpenAsync(new DbOptions
{
    Path = "app.tkdb",
    Password = password,          // ReadOnlySpan<char>, opcional
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

### Reglas de la API

- Toda operación de E/S es `ValueTask` o `IAsyncEnumerable<T>`. No hay métodos síncronos salvo `Dispose`.
- Toda operación acepta `CancellationToken`.
- Las transacciones son `IAsyncDisposable`; salir del scope sin commit hace rollback.
- Los escaneos devuelven `IAsyncEnumerable<T>` con `[EnumeratorCancellation]`, nunca `Task<List<T>>`.
- Nada de `object`, nada de `dynamic`, nada de `Expression.Compile()` en caliente.

### Mapeo de tipos sin reflexión

Un **generador de código fuente** produce el serializador de cada tipo marcado con `[TkTable]` en tiempo de compilación: escribe y lee campos directamente sobre `Span<byte>`, sin boxing ni asignaciones. Esto es lo que hace que Native AOT funcione y es también lo que da el rendimiento; la reflexión en runtime está prohibida en el núcleo.

El generador emite: lectura y escritura de la fila, extracción de la clave de cada índice declarado, y la codificación ordenada de esa clave.

### Capa SQL (paquete aparte, fase posterior)

Subconjunto de SQL-92 con `SELECT`, `INSERT`, `UPDATE`, `DELETE`, `CREATE TABLE`, `CREATE INDEX`, joins internos y agregados. Más dos extensiones propias: `ORDER BY VECTOR_DISTANCE(col, @v) LIMIT k` para búsqueda vectorial, y sintaxis de rango temporal.

Parser de descenso recursivo escrito a mano —ningún generador de parsers, por el tamaño del binario— planificador basado en reglas con estadísticas por índice, y un caché de planes preparados con la consulta normalizada como clave. Encima de eso, `DbConnection` / `DbCommand` / `DbDataReader` para que Dapper funcione sin adaptador.

## Vistas e índices sobre expresiones

Vistas sí, vistas materializadas no. Son decisiones distintas con razones distintas.

### Vistas: sí, y son baratas

Una vista es una consulta guardada ya parseada que el planificador inlinea en el plan de quien la llama. No hay almacenamiento, no hay mantenimiento, no hay estado que se desincronice. El trabajo real es una entrada de catálogo, la resolución de vistas anidadas y la detección de ciclos.

Dos acotaciones:

- Sólo tienen sentido en la capa SQL. En el núcleo tipado una vista es un método que devuelve `IAsyncEnumerable<T>`; C# ya resuelve eso y el motor no necesita saber nada.
- Sólo de lectura. Las vistas actualizables son un pozo sin fondo de reglas de propagación y no aportan al caso de uso embebido.

### Vistas materializadas: no

El costo no es guardar el resultado, es mantenerlo sincronizado. Las cuatro razones, en orden de peso:

| Problema | Por qué descarta la funcionalidad |
| --- | --- |
| Refresco incremental | Propagar deltas en cada commit es un problema de grado investigación, funciona sólo para una clase restringida de consultas y concentra los bugs de corrección más difíciles de encontrar. PostgreSQL ni siquiera lo tiene |
| Recálculo en cada escritura | Mata el throughput de inserción, que es exactamente donde este motor pensaba ganar |
| Refresco automático | Exige triggers o un equivalente de hooks de escritura, que ya están descartados. Reintroducirlos por esto es pagar dos veces |
| Choque con copy-on-write | Refrescar reescribe la tabla entera; bajo CoW eso es un churn de páginas y una amplificación de espacio considerables |

Quien de verdad necesite una, tiene `CREATE TABLE AS SELECT` más un refresco explícito: tres líneas en la aplicación. En una base embebida la aplicación sabe cuándo escribe, no necesita que el motor lo adivine.

### Índices sobre expresiones: el 80% del valor, casi gratis

Lo que la gente quiere cuando pide una vista materializada es casi siempre una expresión precalculada y buscable. Eso es un índice sobre expresión con columnas incluidas:

```sql
CREATE INDEX ix_orders_mes ON orders (date_trunc('month', created_at)) INCLUDE (total);
```

El gestor de índices lo mantiene incrementalmente por el mismo camino de código que cualquier otro índice secundario —sin maquinaria nueva— y el planificador lo usa de forma transparente cuando reconoce la expresión en el predicado. La expresión debe ser determinista y el catálogo guarda su forma normalizada para poder compararla.

Por eso se adelanta a la **Fase 6**, junto con los índices secundarios genéricos, en vez de esperar a la capa SQL.

## Triggers: fuera de alcance

El sentido de un trigger es imponer una invariante cuando hay muchos clientes independientes escribiendo y no se controla el código de todos. En TekiumDB hay un solo proceso escribiendo y ese código es el de la aplicación. La justificación principal desaparece y lo que queda es control de flujo escondido dentro del archivo de datos.

Los cuatro costos, en orden de peso:

- **Rendimiento donde más duele.** El cuerpo corre por fila, dentro del lock del escritor. Bajo copy-on-write eso son más páginas sucias por commit y una ventana exclusiva más larga, justo en la inserción secuencial que era la ventaja del motor.
- **Recursión.** Un trigger que escribe dispara triggers. Hace falta límite de profundidad, detección de ciclos y reglas de orden entre varios triggers sobre la misma tabla. Semántica que hay que mantener diez años.
- **Seguridad, peor que con procedimientos.** Un procedimiento corre porque alguien lo llama; un trigger corre por el solo hecho de insertar. Un `.tkdb` ajeno pasaría a ser código que se ejecuta al escribir en él.
- **Deadlocks.** Un cuerpo que haga E/S dentro de la transacción y dentro del semáforo del escritor es una superficie de bloqueo que no se quiere tener.

### Qué se construye en su lugar

| Para qué se usan los triggers | Respuesta en TekiumDB |
| --- | --- |
| Auditoría e historial | Change feed post-commit |
| Columnas derivadas o desnormalizadas | Columnas calculadas más índices sobre expresiones |
| Validación | Restricciones declarativas: `CHECK`, `UNIQUE`, `FOREIGN KEY` |
| Invalidar caché, reaccionar a cambios | Evento post-commit en C# |

### Restricciones declarativas (Fase 6.5)

`CHECK`, `UNIQUE` y `FOREIGN KEY` como metadatos del catálogo, verificados por el motor al escribir. Son más baratas que un trigger, se pueden inspeccionar, el planificador puede razonar sobre ellas —un `UNIQUE` le dice que una búsqueda devuelve a lo sumo una fila— y no ejecutan código arbitrario. Una violación aborta la transacción con un error que nombra la restricción y la fila.

Las `CHECK` se limitan a expresiones deterministas sobre columnas de la misma fila. Nada de subconsultas: eso convierte cada inserción en una consulta y reintroduce el problema de rendimiento del trigger por otra puerta.

### Change feed post-commit (Fase 6.7)

Esta es la pieza que de verdad reemplaza a los triggers, y sale casi gratis porque el MVCC ya guarda las versiones viejas de cada fila.

```csharp
await foreach (var change in db.WatchAsync<Order>(sinceTx, ct))
{
    // change.Kind: Insert | Update | Delete
    // change.Before, change.After, change.TransactionId
}
```

Tres propiedades que lo hacen mejor que un trigger para el mismo trabajo:

- Corre **después** del commit y **fuera** del lock del escritor, así que no toca la latencia de escritura.
- Es async de verdad: el consumidor puede ser lento sin frenar al escritor.
- El consumidor arranca desde un id de transacción, así que puede reanudar tras un reinicio sin perder cambios, mientras esas versiones sigan retenidas.

El feed retiene snapshots, igual que un lector. Un consumidor abandonado es una fuga de espacio, así que necesita el mismo diagnóstico de snapshots viejos que las transacciones de lectura, y un horizonte configurable que corte al consumidor que se quedó demasiado atrás.

### Si algún día hicieran falta igual

La única versión aceptable sería la angosta: sólo `BEFORE INSERT` y `BEFORE UPDATE` por fila, sólo escritos en PL/Tk, sin recursión, sin DDL, y detrás del mismo flag `AllowStoredProcedures` desactivado por defecto. No antes de que exista demanda real medida.

## Capacidades que la arquitectura ya regala

Funcionalidades que salen casi gratis porque copy-on-write, MVCC y la capa de E/S aislada ya hacen el trabajo difícil. Ninguna agrega una instrucción al camino caliente.

### Backup en caliente (Fase 10.2)

La más valiosa de todas. Con copy-on-write, un respaldo consistente es fijar una raíz y copiar las páginas alcanzables desde ella. Sin bloquear al escritor, sin detener nada, sin impacto en la latencia de escritura: el árbol de ese snapshot es inmutable por construcción y sus páginas no se liberan mientras el backup las retenga.

SQLite necesita una API dedicada para esto y aun así interfiere con los escritores. Aquí es recorrer un árbol desde un snapshot fijado, que es maquinaria que ya existe. Para una base embebida es argumento de venta de primera línea.

El respaldo retiene espacio mientras corre, igual que un lector largo. Un backup sobre una base con escritura intensa amplifica el archivo, y hay que medirlo y documentarlo.

### Modo en memoria (Fase 1.5)

La misma capa de E/S contra memoria en vez de archivo: una segunda implementación de la interfaz del dispositivo, nada más. Lo que compra no es velocidad, es adopción. Hace que las pruebas de quien use TekiumDB sean triviales, y esa es una de las razones por las que la gente termina eligiendo SQLite.

Beneficio lateral: las pruebas del propio motor corren contra memoria y son órdenes de magnitud más rápidas en CI.

### Carga masiva (Fase 6.8)

Construir el B+Tree de abajo hacia arriba desde entrada ordenada —llenar hojas al 100%, después el nivel de arriba, hasta la raíz— en vez de insertar fila por fila. Un orden de magnitud más rápido en importación inicial y en restauración de respaldo, y el camino normal de inserción no se entera.

Si la entrada no viene ordenada, se ordena externamente primero. Sigue ganando.

### Verificación de integridad (Fase 10.4)

Un `VerifyAsync` que recorre cada página alcanzable y valida su tag GCM, más la consistencia estructural del árbol: orden de claves, ocupación de nodos, que la free-list no se solape con páginas vivas.

Suena aburrido y es justamente lo contrario: la confianza está identificada como el riesgo número uno del proyecto, y esta es la herramienta que la construye. Un usuario que puede verificar su archivo confía en el motor.

### Diagnóstico: EXPLAIN y contadores (Fases 10.4 y 12)

Contadores por `System.Diagnostics.Metrics.Meter`: aciertos y fallos de la caché de páginas, páginas escritas por commit, duración del commit, snapshots vivos y su antigüedad, profundidad del árbol por índice. Detrás de un flag, cuestan cero cuando están apagados.

Y un `EXPLAIN` que imprima el plan elegido: qué índice, por qué, cardinalidad estimada contra real. Llega con la capa SQL. Sin esto, cada reporte de "va lento" es soporte a ciegas.

### Adaptadores de ecosistema (Fase 15)

Cero costo en el motor, efecto desproporcionado en adopción. Cada uno en su propio paquete, para que el núcleo no gane ni una dependencia:

| Adaptador | Qué desbloquea |
| --- | --- |
| Proveedor de EF Core | Migraciones y modelo existente sin reescribir la aplicación |
| `IDistributedCache` | Caché persistente local, sin Redis |
| Almacén de claves de Data Protection | Claves de cifrado de ASP.NET Core en un archivo local |
| Proveedor de `HealthChecks` | Estado de la base en el endpoint de salud |

### Índices parciales (Fase 6)

`CREATE INDEX ix_activos ON orders (created_at) WHERE deleted = false`. Mismo camino de código que los índices sobre expresiones: al insertar o actualizar, el motor evalúa el predicado y decide si la fila entra al índice.

Lo que compra es doble. El índice es más chico, así que cabe más en caché y el árbol es menos profundo. Y las escrituras de filas que no cumplen el predicado dejan de pagar el mantenimiento de ese índice. En el patrón típico de borrado lógico, donde el 90% de las filas están marcadas como borradas, la diferencia es grande.

El predicado se guarda normalizado en el catálogo y el planificador sólo usa el índice cuando puede probar que el predicado de la consulta lo implica. Si no puede probarlo, lo ignora — nunca devuelve resultados incompletos en silencio.

### Viaje en el tiempo (Fase 10.6)

La cabecera ya guarda slots de raíz. Guardar un anillo de N en vez de dos convierte eso en "abrí la base como estaba en la transacción X":

```csharp
await using var past = await db.AsOfAsync(transactionId);
var before = await past.Table<Order>().FindAsync(id);
```

Es una transacción de lectura como cualquier otra, apuntando a una raíz vieja en vez de a la actual. Cero costo para quien no lo usa, y encaja con las otras dos piezas: el change feed te dice **qué** cambió, el viaje en el tiempo te deja ver **cómo estaba**, y el backup en caliente es exactamente el mismo mecanismo de fijar una raíz.

El costo es retención de espacio: mientras una raíz vieja viva en el anillo, sus páginas no se liberan. Por eso va con un horizonte configurable —por número de transacciones o por tiempo— y apagado por defecto, con el anillo reducido a los dos slots mínimos. **El anillo tiene que estar reservado en la cabecera desde la Fase 1**, aunque sólo se usen dos slots: agregarlo después es un cambio de formato de archivo.

### Lo que se descarta a propósito

- **Almacenamiento columnar o compresión por columna.** Empuja al motor hacia analítica y rompe el modelo de página.
- **Caché de resultados de consulta.** Invalidación complicada, beneficio escaso cuando las lecturas ya son rápidas.
- **Hilos de mantenimiento en segundo plano.** Pausas impredecibles, que es exactamente lo que este diseño evita. La compactación se pide explícitamente.

## Procedimientos y funciones personalizadas

Sí, pero Native AOT parte la pregunta en dos caminos que no se pueden fusionar. Hay que implementar los dos, porque resuelven cosas distintas.

### Qué prohíbe AOT

| Técnica | Bajo AOT | Consecuencia |
| --- | --- | --- |
| Compilar C# en caliente con Roslyn | Imposible | No hay `CREATE PROCEDURE ... AS $$ C# $$` ejecutable |
| Cargar un .dll de plugin en runtime | Imposible | No hay procedimientos distribuidos como ensamblado |
| `Reflection.Emit`, `DynamicMethod` | Imposible | El planificador no puede emitir IL para los predicados |
| `Expression.Compile()` | Imposible | LINQ se traduce, nunca se compila en caliente |
| Generador de código fuente | Funciona | Es el camino para C# |
| Intérprete de bytecode escrito en C# | Funciona | Es el camino para un PL almacenado |

El intérprete es la clave: una máquina virtual es código C# normal, así que **datos ejecutables sí son posibles bajo AOT; código ejecutable no**. Eso define toda la arquitectura de abajo.

### Camino A — C# por generador de código

El usuario escribe sus funciones en su propio proyecto y un generador las registra en una tabla estática en tiempo de compilación.

```csharp
[TkFunction("slugify", Deterministic = true)]
public static int Slugify(ReadOnlySpan<char> src, Span<char> dst) { ... }

[TkFunction("abs_delta", Vectorized = true)]
public static void AbsDelta(ReadOnlySpan<double> a, ReadOnlySpan<double> b, Span<double> dst) { ... }

[TkProcedure("archive_orders")]
public static async ValueTask<int> ArchiveOrders(
    TkContext ctx, DateTimeOffset before, CancellationToken ct) { ... }
```

El generador emite un thunk tipado por función —lee los argumentos de los registros del frame y escribe el resultado— y un `switch` sobre un id entero para despacharlas. Cero reflexión, cero boxing, cero asignaciones. La llamada cuesta lo mismo que un método estático normal.

La limitación es real: estas funciones viven en la aplicación, no en el archivo. Cambiar una exige recompilar y redesplegar, y dos aplicaciones distintas que abran el mismo archivo pueden tener conjuntos de funciones distintos. El catálogo guarda la firma esperada, no el cuerpo: si una consulta referencia una función que el host no registró, el error sale al planificar, nunca a mitad del escaneo.

### Camino B — PL/Tk, compilado a bytecode y guardado en el archivo

Un subconjunto reducido de PL/pgSQL que se compila al crearlo y se guarda como bytecode en el catálogo. Nunca se vuelve a parsear.

```sql
CREATE PROCEDURE archive_orders(p_before TIMESTAMPTZ) RETURNS INT AS $$
DECLARE n INT := 0;
BEGIN
    FOR r IN SELECT id FROM orders WHERE created_at < p_before LOOP
        INSERT INTO orders_archive SELECT * FROM orders WHERE id = r.id;
        DELETE FROM orders WHERE id = r.id;
        n := n + 1;
    END LOOP;
    RETURN n;
END $$;
```

El lenguaje se queda corto a propósito: `DECLARE`, asignación, `IF`/`ELSIF`/`ELSE`, `WHILE`, `FOR` sobre cursor, `RETURN`, `RAISE`, llamada a otro procedimiento, y sentencias SQL embebidas. Sin excepciones definidas por el usuario, sin SQL dinámico, sin tipos compuestos. Cada cosa que se agregue hay que mantenerla diez años.

**El punto de rendimiento que hay que entender**: cada sentencia SQL embebida se planifica al crear el procedimiento y se guarda el plan junto al bytecode. La VM nunca parsea ni planifica en tiempo de llamada. El intérprete sólo paga el control de flujo, que es una fracción mínima frente al acceso a datos. Un bucle de diez mil iteraciones que toca filas gasta su tiempo en el B+Tree, no en el dispatch de opcodes.

Diseño de la VM: basada en registros —no en pila, menos instrucciones por expresión—, bytecode en un `byte[]` plano, despacho por `switch` sobre el opcode, registros en un `Span<TkValue>` alquilado del pool, frames reutilizados. `TkValue` es un struct de 16 bytes con un tag y la carga superpuesta; los valores grandes van por referencia al heap del frame. Cero asignaciones por llamada en el caso normal.

### El costo por fila, que es donde esto se arruina

Una función escalar llamada una vez por fila es el peor caso de cualquier motor: a diez millones de filas, el costo de la llamada domina sobre el cuerpo. Tres mitigaciones, todas obligatorias:

- **Inlining en el plan.** Aritmética, comparaciones y concatenación simples se compilan dentro del plan como expresiones, no se llaman como función.
- **Interfaz vectorizada.** La función recibe un bloque de filas en `Span<T>` y devuelve un `Span<T>`. Una llamada por cada mil filas en vez de mil llamadas, y el cuerpo se vectoriza con `TensorPrimitives`.
- **`Deterministic = true`.** Habilita plegado de constantes y memoización dentro de la misma consulta.

Un UDF escalar fila por fila sigue siendo lento y hay que documentarlo como tal, no esconderlo.

### Tipos de rutina a soportar

| Tipo | Contrato | Camino |
| --- | --- | --- |
| Escalar | valor en, valor out | C# y PL/Tk |
| Escalar vectorizada | bloque de filas en spans | Sólo C# |
| Agregada | `Init` / `Accumulate` / `Merge` / `Finalize` | Sólo C# |
| Tabular | devuelve `IAsyncEnumerable` de filas | C# y PL/Tk |
| Procedimiento | efectos, transacción propia o heredada | C# y PL/Tk |

### Seguridad

Un archivo con bytecode guardado es contenido ejecutable dentro de un archivo de datos. Hay que tratarlo así desde el principio:

- La VM no tiene E/S, ni red, ni acceso al sistema de archivos, ni manera de llamar a C# que el host no haya registrado explícitamente.
- Límite configurable de instrucciones y de profundidad de llamada por invocación, para que un bucle infinito corte en vez de colgar el proceso.
- `DbOptions.AllowStoredProcedures` en `false` por defecto. Abrir un archivo ajeno no ejecuta nada hasta que alguien lo habilite a conciencia.

### Dónde cae en las fases

El camino A entra como Fase 13, apenas existan el catálogo y el generador de código. El camino B es Fase 14 y depende de la capa SQL, porque reutiliza su parser y su planificador. Si el proyecto se detiene en la Fase 10, no se pierde nada: las funciones en C# siguen siendo el 90% del valor.

### AOT contra JIT, y los plugins en caliente

Hay una premisa que conviene desarmar: **ser compatible con AOT no cuesta rendimiento**. Lo que costaría es obligar a AOT, y eso no hace falta.

Ser AOT-compatible es una propiedad de la librería; publicar con AOT es una decisión de quien la consume. Es el mismo paquete NuGet: una aplicación lo publica con `PublishAot=true` y otra con JIT normal, sin recompilar nada del motor. Y las disciplinas que exige la compatibilidad —cero reflexión, serializadores por generador de código, nada de `Expression.Compile`— son exactamente las que hacen que el motor sea rápido también bajo JIT. No hay tensión entre las dos cosas.

Lo que sí es un trade-off real:

| | Native AOT | JIT con TieredPGO |
| --- | --- | --- |
| Bucles calientes en régimen estacionario | Ligeramente por detrás | Ligeramente por delante: tiene perfil dinámico y devirtualiza mejor |
| Arranque en frío | Milisegundos | Decenas o cientos de milisegundos |
| Memoria y tamaño del binario | Mucho menor | Mayor |
| Compilar planes de consulta a IL en runtime | Imposible | Posible, y vale 2× a 5× en escaneos con expresiones |

Para una base embebida que abre y cierra con la aplicación de escritorio, el arranque en frío es lo que el usuario siente. Para un proceso de servidor que corre días, gana el JIT. Por eso no se elige: se soportan los dos.

### Por qué los plugins en caliente no son la respuesta

Un plugin cargado por `AssemblyLoadContext` da flexibilidad, no velocidad. Cruzar el borde del plugin es despacho por interfaz y menos inlining; un contexto colectible bloquea optimizaciones adicionales. Y un plugin es un `.dll` al lado de la aplicación, no algo dentro del `.tkdb`: no resuelve el problema que sí resuelve el bytecode guardado en el archivo. A cambio, renunciar a AOT para todos los consumidores por una funcionalidad que pocos van a usar es un mal canje.

La salida es tener tres niveles en vez de elegir uno:

| Nivel | Qué es | Dónde corre |
| --- | --- | --- |
| Generador de código | Funciones C# compiladas junto al host | AOT y JIT |
| Bytecode PL/Tk | Rutinas guardadas dentro del archivo | AOT y JIT |
| Plugins por `AssemblyLoadContext` | Ensamblados cargados en caliente | Sólo JIT, paquete aparte, desactivado por defecto |

El mismo patrón aplica al ejecutor: `IExpressionCompiler` con dos implementaciones —una interpretada y vectorizada que siempre está, y una que emite IL— y el motor elige en tiempo de ejecución con `RuntimeFeature.IsDynamicCodeCompiled`. Quien corre bajo JIT se lleva la compilación de planes gratis; quien corre bajo AOT se lleva el intérprete vectorizado, que es de todas formas rápido.

### Dónde está realmente el rendimiento

En orden de impacto, lo que decide si este motor es rápido: el patrón de E/S y el acierto de la caché de páginas, la disciplina de asignaciones, el layout del nodo B+Tree y su fan-out, el camino de lectura sin bloqueo, y SIMD en las distancias vectoriales. La estrategia de compilación está muy abajo en esa lista —unidades porcentuales— y no merece sacrificar el resto por ella.

## Reglas duras de rendimiento y asincronía

Estas van en el `CLAUDE.md` del repo. Son las que un agente rompe sin darse cuenta y las que deciden si el motor es rápido o es otro ORM lento.

### Asignaciones

- Cero asignaciones en el camino de lectura caliente. `ArrayPool<byte>.Shared` para buffers temporales, con `try`/`finally` para devolverlos siempre.
- Las páginas en caché son `Memory<byte>` sobre buffers alquilados, nunca `byte[]` nuevos por lectura.
- Comparaciones de claves sobre `ReadOnlySpan<byte>`, con `SequenceCompareTo`. Nada de `string` ni de `Encoding.GetString` dentro del motor.
- `stackalloc` para claves pequeñas, con un umbral fijo y fallback al pool. Nunca `stackalloc` dentro de un bucle.
- Structs `readonly ref struct` para los cursores, para que el compilador impida capturarlos.

### Asincronía

- `ValueTask` en todo lo que acierta en caché la mayoría de las veces; `Task` sólo en la superficie pública donde se espere más de una vez.
- `ConfigureAwait(false)` en toda la librería. Sin excepciones.
- Nunca `lock` ni `Monitor` dentro de un método async. Serialización con `SemaphoreSlim`.
- Nunca `.Result`, `.Wait()` ni `GetAwaiter().GetResult()`. Ni en pruebas.
- E/S con `RandomAccess.ReadAsync` / `WriteAsync` sobre `SafeFileHandle`, no con `FileStream`, para evitar el estado interno y el buffer de `FileStream`.
- Un fast path síncrono cuando la página ya está en caché: devolver una `ValueTask` ya completada sin tocar el scheduler.

### Compilación

- `<InvariantGlobalization>true</InvariantGlobalization>`, `<TieredPGO>true</TieredPGO>`, `<ServerGarbageCollection>` configurable.
- Compatible con `PublishAot`. Cualquier API que rompa AOT se rechaza en revisión.
- `<Features>runtime-async=on</Features>` sólo **después** de tener benchmarks estables, para medir si ayuda. Es preview en .NET 11.
- `AllowUnsafeBlocks` habilitado, pero `unsafe` se justifica caso por caso en comentario.

### Lo que no se hace

No se optimiza nada sin un benchmark que lo demuestre. Si Claude Code propone una optimización, debe acompañarla del número antes y después, medido con BenchmarkDotNet. Una optimización sin medición se revierte.

## Fases de entrega

Estrictamente secuenciales. Cada fase termina con su criterio de salida cumplido y verde en CI antes de empezar la siguiente. Hay que darle las fases a Claude Code **de una en una**, no el plan completo de golpe: un agente al que se le dan doce fases empieza a andamiar las doce y no termina ninguna.

| # | Fase | Criterio de salida |
| --- | --- | --- |
| 0 | Esqueleto de la solución, proyectos, CI, BenchmarkDotNet, analizadores | `dotnet build` y `dotnet publish -p:PublishAot=true` pasan en Linux, Windows y macOS |
| 0.5 | Spike de validación: pager, B+Tree y lectura puntual en versión mínima, medidos contra SQLite | Números publicados en el repo. Dentro de 2× de SQLite en las tres cargas, se sigue; un orden de magnitud abajo, se replantea |
| 1 | Capa de E/S y pager sin cifrado: leer, escribir, caché de páginas, free-list | Escribir y leer un millón de páginas; cero asignaciones en el camino caliente según el diagnóstico de memoria |
| 1.5 | Modo en memoria: segunda implementación de la capa de dispositivo | Las mismas pruebas del pager corren contra archivo y contra memoria, sin cambiar una línea del motor |
| 2 | Cifrado por página y derivación de clave | Vectores de prueba del RFC 9106 pasan; prueba de unicidad de nonce pasa; abrir con contraseña incorrecta falla limpio |
| 3 | B+Tree copy-on-write: insert, lookup, delete, split, merge | Pruebas de propiedades contra un `SortedDictionary` con un millón de operaciones aleatorias |
| 4 | MVCC, transacciones y commit atómico | Prueba de crash: matar el proceso en cada punto del commit y verificar que siempre abre en un estado consistente |
| 5 | Catálogo, filas tipadas y el generador de código fuente | Round-trip de todos los tipos primitivos y anulables; una fila escrita con la versión 1 se lee con el lector de la versión 2; AOT sigue funcionando |
| 5.5 | Compresión Zstandard por página, activable por archivo | Ratio y latencia medidos contra sin comprimir; un archivo con y otro sin compresión abren con el mismo código |
| 6 | Índices secundarios genéricos, codificación ordenada de claves e índices sobre expresiones | Orden de las claves codificadas coincide con el orden de los valores, para todos los tipos; un índice sobre expresión y uno parcial se mantienen al insertar, y el planificador sólo usa el parcial cuando puede probar que el predicado lo implica |
| 6.5 | Restricciones declarativas: CHECK, UNIQUE, FOREIGN KEY | Una violación aborta la transacción nombrando la restricción y la fila; el planificador usa UNIQUE para acotar el resultado a una fila |
| 6.7 | Change feed post-commit sobre las versiones del MVCC | Un consumidor reanuda desde un id de transacción sin perder cambios; la latencia de escritura no se mueve con el feed activo |
| 6.8 | Carga masiva: construcción del B+Tree de abajo hacia arriba | Importar diez millones de filas ordenadas es un orden de magnitud más rápido que insertarlas una por una |
| 7 | Índice temporal con zone maps y split 100/0 | Un rango sobre diez millones de filas por debajo del umbral fijado en el benchmark |
| 8 | Índice UUID v7 | Inserción secuencial no degrada; ocupación de hoja por encima del 90% |
| 9 | HNSW en `Tekium.Embedded.Vectors` | Recall@10 por encima de 0.95 contra fuerza bruta en un conjunto de un millón de vectores |
| 10 | Herramientas: CLI de inspección, reparación, rekey y compactación | Un archivo corrupto a mano se diagnostica sin abrir la base |
| 10.2 | Backup en caliente desde un snapshot fijado | Copia consistente con el escritor activo; la latencia de escritura no se mueve durante el respaldo |
| 10.4 | VerifyAsync y contadores de diagnóstico por Meter | Un archivo con una página alterada a mano se detecta y se nombra; los contadores apagados cuestan cero |
| 10.6 | Viaje en el tiempo: abrir la base en una transacción pasada | AsOfAsync devuelve el estado de una transacción anterior; el horizonte configurado libera las raíces que lo exceden |
| 11 | Capa LINQ | Traducción de los predicados soportados; los no soportados fallan en compilación o lanzan claro, nunca degradan a evaluación en cliente en silencio |
| 12 | Capa SQL y proveedor ADO.NET | Dapper corre contra el proveedor sin adaptador; una vista anidada en otra se resuelve y un ciclo se rechaza al crearla; EXPLAIN imprime el plan elegido con cardinalidad estimada contra real |
| 13 | Funciones y procedimientos en C# por generador de código | Un UDF escalar y uno vectorizado corren bajo PublishAot, sin reflexión ni Expression.Compile |
| 14 | PL/Tk: parser, compilador a bytecode y máquina virtual | Un procedimiento con bucle y SQL embebido corre; el límite de instrucciones corta un bucle infinito |
| 15 | Adaptadores de ecosistema: EF Core, IDistributedCache, Data Protection, HealthChecks | Cada adaptador en su propio paquete; el núcleo no gana ni una dependencia nueva |

Las fases 0 a 4 son el riesgo real del proyecto. Si esas cuatro quedan sólidas, el resto es trabajo conocido.

## Pruebas y benchmarks

Un motor de base de datos se prueba distinto a una aplicación. Las pruebas de ejemplo no sirven: lo que encuentra los bugs reales son las pruebas de propiedades y las de crash.

### Pruebas de propiedades

Con FsCheck o CsCheck, contra un modelo de referencia en memoria —un `SortedDictionary`— sobre secuencias aleatorias de operaciones. Si el motor y el modelo divergen, el framework reduce la secuencia al caso mínimo. Es la herramienta que encuentra los bugs de split y merge del B+Tree.

### Pruebas de crash

Un proceso hijo que escribe y se mata a sí mismo en un punto elegido del commit. El padre reabre el archivo y verifica que el estado sea o el anterior o el posterior, nunca algo intermedio. Hay que cubrir cada punto entre los pasos 3 y 5 del commit.

Aparte, un wrapper de E/S que simula fallos: escrituras parciales, disco lleno, bits volteados. El motor debe fallar con un error claro, nunca con datos corruptos silenciosos.

### Pruebas de concurrencia

Un escritor y N lectores en paralelo durante varios minutos, verificando que ningún lector ve nunca un estado parcial y que la free-list no libera páginas retenidas. Correr bajo el analizador de deadlocks.

### Benchmarks

Todos con BenchmarkDotNet, con `MemoryDiagnoser` y `ThreadingDiagnoser` activos, y con un punto de comparación real: **SQLite y LiteDB con la misma carga**. Sin ese punto de comparación un número no dice nada.

| Benchmark | Qué mide |
| --- | --- |
| Insert secuencial por UUID v7 | Throughput y ocupación de hoja |
| Insert aleatorio por GUID v4 | El caso malo, para saber cuánto cuesta |
| Lectura puntual por clave primaria | Latencia p50 y p99 |
| Rango de fechas sobre diez millones de filas | Efecto de los zone maps |
| Búsqueda vectorial top-10 | Recall y latencia a distintos `efSearch` |
| Commit con N páginas sucias | Costo del copy-on-write |
| Cifrado activado contra desactivado | Overhead real de AES-GCM |
| Tiempo de apertura en frío | Lo que ve el usuario al arrancar la app |

Los resultados se versionan en el repo. Una regresión por encima del 5% rompe la build.

## Riesgos y decisiones abiertas

### Riesgos reales

| Riesgo | Mitigación |
| --- | --- |
| El B+Tree copy-on-write con split y merge correctos es lo más difícil del proyecto | Pruebas de propiedades desde el primer commit de la Fase 3, no al final |
| Reuso de nonce rompería el cifrado por completo y en silencio | Prueba dedicada en la Fase 2; la construcción del nonce no se toca después sin revisión |
| Amplificación de espacio del copy-on-write: el archivo puede crecer varias veces el tamaño de los datos | Free-list agresiva, `CompactAsync`, y medirlo en benchmark desde la Fase 4 |
| Un lector no liberado retiene páginas para siempre | `IAsyncDisposable` obligatorio + un diagnóstico que avise de snapshots vivos más de N segundos |
| Alcance: el proyecto puede crecer sin fin | Las fases 11 y 12 son opcionales; el producto es entregable al terminar la 10 |
| Argon2id en C# puro mal implementado es peor que PBKDF2 | Validar contra los vectores del RFC 9106 antes de usarlo; si no pasa, usar Konscious |

### Decisiones ya cerradas

- **Nombre y extensión.** El producto es **TekiumDB** y el archivo lleva extensión `.tkdb`.
- **Licencia y repositorio.** MIT, pero el repositorio arranca privado y se abre cuando el motor esté estable. Decisión con consecuencia práctica: cada dependencia que entre debe ser compatible con MIT, y hay que poner `LICENSE` y las cabeceras desde el primer commit para no tener que auditar el historial después.
- **Compresión de páginas.** Sí, Zstandard, aprovechando `ZstandardStream` que ya viene en .NET 11. Se comprime **antes** de cifrar, nunca después: el texto cifrado es indistinguible de ruido y no comprime nada.
- **Esquema evolutivo.** Sí. Cada fila lleva una versión de esquema de un byte y el catálogo guarda el historial de versiones de cada tipo. Agregar una columna anulable no reescribe nada: las filas viejas se leen con su versión y los campos faltantes salen como el valor por defecto.
- **Replicación o sincronización.** Fuera de alcance. No se construye nada para eso ahora, aunque el historial de snapshots del MVCC quede como base natural si algún día se retoma.

### Lo que no se va a hacer

Ni triggers, ni vistas materializadas, ni soporte multi-proceso, ni compatibilidad de formato con VistaDB. Si aparece en una propuesta, se rechaza.
