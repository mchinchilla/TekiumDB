# Fase 15 — adaptadores de ecosistema: caché distribuida, Data Protection y health checks

**Criterio de salida cumplido en macOS y en Linux, para tres de los cuatro adaptadores del plan.** Cada uno está en su propio paquete y el núcleo no ganó ninguna dependencia. El proveedor de EF Core no se hizo: por decisión tuya del 2026-11-17 pasa a ser una fase propia (la 16). Windows no se ha verificado.

Hecho el 2026-11-17. Esta fase no tiene criterio de rendimiento y no se midieron tiempos.

## Qué hay

| Paquete | Qué trae | Dependencia que agrega |
| --- | --- | --- |
| `TekiumDB.Caching` | `TkDistributedCache` (un `IDistributedCache`) y `AddTekiumDistributedCache` | Ninguna: las abstracciones de caché e inyección de dependencias ya vienen con el SDK de .NET 11 |
| `TekiumDB.DataProtection` | `TkXmlRepository` (un `IXmlRepository`) y `PersistKeysToTekiumDb` | `Microsoft.AspNetCore.DataProtection` |
| `TekiumDB.HealthChecks` | `TkHealthCheck` (un `IHealthCheck`) y `AddTekiumDb` | `Microsoft.Extensions.Diagnostics.HealthChecks` |

Los tres usan sólo la API pública del núcleo: ninguno ve sus internos.

El núcleo ganó dos propiedades públicas, sin dependencias: `TekiumDb.IsOpen` y `TekiumDb.AcceptsWrites` (falso después de un commit cuyo resultado quedó en duda). Las necesita el health check para responder sin hacer E/S.

### Caché distribuida

Las entradas viven en una tabla propia (`tekium_cache`), con un índice sobre el momento de vencer.

- **Vencimiento absoluto y deslizante**, con las mismas reglas que las demás implementaciones: el deslizante nunca pasa del absoluto, y una entrada vencida no se devuelve aunque siga en el archivo.
- **Sólo los métodos asíncronos.** `Get`, `Set`, `Refresh` y `Remove` lanzan `NotSupportedException`: TekiumDB no hace E/S síncrona, y bloquear un hilo esperando una tarea está prohibido en todo el repositorio. Los métodos de extensión (`GetStringAsync`, `SetStringAsync`) funcionan.
- **Leer una entrada deslizante la renueva, y renovar es escribir.** Para que cada lectura no sea un commit, la renovación se salta mientras no haya pasado una décima de la ventana (`SlidingRenewalThreshold`, configurable; con 0 se renueva siempre). El precio: una entrada vive entre el 90 % y el 100 % de su ventana después de su última lectura.
- **Sin hilo de limpieza.** Lo vencido sale del archivo dentro de un `SetAsync`, cuando ha pasado `ExpiredItemsDeletionInterval` (media hora) desde la última vez, o a pedido con `RemoveExpiredAsync`.

### Claves de Data Protection

`IXmlRepository` sólo tiene dos métodos, y los dos son síncronos. La regla del proyecto es no hacer E/S síncrona ni esperar una tarea bloqueando. Te pregunté cómo resolverlo y elegiste **memoria más escritura diferida**:

- Las claves se leen una vez, con `await TkXmlRepository.CreateAsync(db)`, y `GetAllElements` las sirve desde memoria.
- `StoreElement` deja la clave en memoria y lanza su escritura al archivo, encadenada a las anteriores para que lleguen en orden; vuelve sin esperar.
- `FlushAsync` espera a que todo lo guardado esté en el archivo, y `DisposeAsync` también.
- Si una escritura falla, no es en silencio: `FlushAsync` lanza el error, el siguiente `StoreElement` se niega, y las claves que estaban en cola detrás de la fallida no se escriben.

**Lo que esa decisión cuesta:** hay una ventana, normalmente de milisegundos, entre que Data Protection recibe una clave nueva y que la clave está en el archivo. Si el proceso muere ahí, la clave se pierde y lo que se haya protegido con ella en ese rato no se puede leer. Data Protection crea claves al arrancar por primera vez y al rotarlas (cada 90 días por defecto), así que la ventana se abre pocas veces.

Las claves se guardan como Data Protection las entrega. Quedan cifradas en reposo si la base tiene contraseña, o si se le dice a Data Protection que las cifre (`ProtectKeysWith…`).

### Health check

- **Rápido (por defecto):** la base está abierta y acepta escrituras. No hace E/S: se puede correr cada pocos segundos.
- **Con `Verify = true`:** además lee del archivo y verifica cada página en uso (`VerifyAsync`). Daño es "unhealthy"; algo que merece mirarse pero no es daño, "degraded". Su costo crece con el tamaño del archivo.
- Informa en sus datos la última transacción, si la base está cifrada y, al verificar, cuántos errores y advertencias hubo y los primeros hallazgos.
- Respeta el estado de fallo con que se registró.

## Lo que no se hizo: EF Core

El plan pone el proveedor de EF Core en esta fase. No está, y no es un adaptador pequeño como los otros tres:

- EF Core genera SQL que la capa de la Fase 12 no tiene: `LEFT JOIN` para `Include`, subconsultas para `Any` y `Contains`, `ALTER TABLE` para las migraciones.
- Usa también las rutas síncronas de ADO.NET, que este proveedor rechaza.

Quedó anotado en el plan como Fase 16, con el criterio de salida por fijar al empezarla. Hasta entonces el README dice que no hay proveedor de EF Core.

## Pruebas

| Qué | Prueba |
| --- | --- |
| Caché: lo que entra sale igual (también vacío y de 200 KB), reemplazar, quitar, y tras cerrar y reabrir | `CacheTests` |
| Caché: vencimiento absoluto, relativo y deslizante con un reloj que la prueba mueve; el instante exacto de vencer; deslizante topado por el absoluto; `RefreshAsync` | `CacheTests` |
| Caché: cuándo una lectura escribe y cuándo no (contando transacciones) | `CacheTests` |
| Caché: lo vencido sale del archivo solo, pasado el intervalo, o a mano; con intervalo cero, nunca solo | `CacheTests` |
| Caché: 1 500 operaciones al azar contra un modelo en memoria | `CacheTests` |
| Caché: los métodos síncronos lanzan; el registro en los servicios y sus opciones | `CacheTests` |
| Claves: en memoria al instante, copias al entregar y al guardar, reemplazo por nombre, y en el archivo tras reabrir | `DataProtectionTests` |
| Claves: guardar no espera al archivo (con el escritor de la base ocupado), `FlushAsync` y `DisposeAsync` sí; el orden | `DataProtectionTests` |
| Claves: un fallo de disco al escribir se informa, detiene las siguientes y no deja el archivo a medias | `DataProtectionTests` |
| Claves: Data Protection de verdad protege un texto, se cierra la base (cifrada), se reabre y lo desprotege; con otra base no puede | `DataProtectionTests` |
| Health check: sano sin leer el archivo; sano verificando; dañado sólo lo ve el que verifica; advertencia es "degraded"; base que dejó de aceptar escrituras; base cerrada; estado de fallo registrado; registro en los health checks | `HealthCheckTests` |
| Native AOT | El smoke: la caché, el health check verificando y el almacén de claves |

Sobre archivo y sobre memoria.

### Las pruebas detectan el código roto

Cuarenta y seis roturas hechas a mano, una por una. Treinta y nueve se detectaron a la primera; para cuatro agregué pruebas; dos hubo que repetirlas porque la primera forma no compilaba; y una no compila de ninguna forma.

| Rotura | Detectada |
| --- | --- |
| Caché: devolver una entrada vencida, o en el instante de vencer; ignorar el deslizante al poner; no topar la renovación con el absoluto; que leer o refrescar no renueven; aceptar un absoluto pasado; no sumar el relativo a la hora | Sí |
| Caché: no topar el deslizante con el absoluto al poner | No al principio. Se agregó una entrada con ventana más larga que su vida |
| Caché: ignorar el umbral de renovación | Sí (repetida: la primera forma no compilaba) |
| Caché: renovar una entrada que ya está en su tope absoluto | No al principio: no cambia lo que se lee, sólo escribe de más. Se agregó contar las transacciones |
| Caché: no sacar nunca lo vencido; sacarlo en cada escritura; sacar lo que no venció; con intervalo cero, sacar siempre; no confirmar al quitar o al sacar a mano | Sí |
| Caché: que sacar a mano no reinicie el intervalo | No al principio. Se agregó el caso |
| Caché: un método síncrono que responde | Sí (repetida) |
| Caché: aceptar un umbral fuera de rango o un intervalo negativo; ignorar las opciones dadas al registrar | Sí |
| Claves: no cargar las del archivo; entregar o guardar el mismo objeto en vez de una copia; que las sin nombre compartan entrada; aceptar una clave después de un fallo; no escribir nunca; `FlushAsync` o `DisposeAsync` que no esperan; `DisposeAsync` que lanza; no entregar el repositorio a Data Protection | Sí |
| Claves: escrituras que no esperan a la anterior | No al principio. Se agregó el caso de dos en cola cuando la primera falla |
| Claves: que guardar espere al archivo bloqueando el hilo | No compila: `.Wait()` es un símbolo prohibido por el build. Es la regla que esta fase tenía que respetar |
| Health check: dar por sana una base que no acepta escrituras o que está cerrada; no verificar nunca, o verificar siempre; daño como "degraded"; advertencias como sano; ignorar el estado de fallo registrado, el tope de hallazgos, las opciones o las etiquetas | Sí |
| Núcleo: `AcceptsWrites` e `IsOpen` siempre verdaderos | Sí |

## Límites conocidos

- **La ventana de las claves de Data Protection**, descrita arriba. Quien no la acepte puede llamar a `FlushAsync` después de arrancar la aplicación, que es cuando se crea la primera clave.
- **`TkXmlRepository` se crea con `await` antes de armar los servicios**: no hay una forma de registrarlo que lea la base dentro del contenedor, porque eso sería E/S síncrona.
- **Cada `SetAsync` de la caché es un commit durable**, con su `fsync`. Es una caché que sobrevive al reinicio, no una caché rápida en memoria: para lo segundo va `IMemoryCache` delante.
- **Una lectura de una entrada deslizante puede ser una escritura**, y entonces espera su turno detrás del escritor de la base.
- **La clave de una entrada de caché no puede ser arbitrariamente larga**: es la clave primaria de una tabla.
- **El health check rápido no detecta daño en el archivo.** Sólo el que verifica.
- **Sin sesiones ni caché de salida propias**: usan `IDistributedCache`, pero el middleware de sesión de ASP.NET Core no se probó contra este adaptador.
- **No se midió nada.**

## Verificación

| Dónde | Qué | Resultado |
| --- | --- | --- |
| macOS (arm64) | `dotnet test -c Release` | 871 pasan, 3 explícitas omitidas |
| macOS (arm64) | Smoke de Native AOT, con los tres adaptadores | Pasa |
| Linux (arm64, contenedor) | `scripts/test-linux.sh`: build, las mismas pruebas y el smoke de AOT | 871 pasan, 3 omitidas; smoke pasa |
| Paquetes del núcleo | `dotnet list package` antes y después | Los mismos: ninguno nuevo |
| Windows | — | Sin verificar |
