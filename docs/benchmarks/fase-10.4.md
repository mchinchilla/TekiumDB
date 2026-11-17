# Fase 10.4 — `VerifyAsync` y contadores de diagnóstico por `Meter`

**Criterio de salida cumplido en macOS; pruebas verificadas también en Linux.** Una página alterada a mano bajo una base abierta se detecta y se nombra, para cada página en uso del archivo. Los contadores apagados no cuestan nada medible: la lectura puntual tarda lo mismo con ellos en el código que sin ellos. Windows no se ha verificado.

Medido el 2026-11-16. Una sola máquina (Apple M2 Max, macOS 27.0, arm64); Linux en un contenedor sobre la misma máquina, sólo pruebas.

## `VerifyAsync`

```csharp
var verification = await db.VerifyAsync();
if (!verification.IsHealthy)
    foreach (var finding in verification.Findings)
        log.Error(finding.Message);      // "Page 57 of index orders.Customer cannot be read: …"
```

Comprueba la base tal como la dejó el último commit, con la base abierta y en uso:

- **Cada página en uso, leída del archivo:** su suma de verificación, o su etiqueta de autenticación si la base está cifrada, y que sea la página que debe ser.
- **Cada árbol:** claves en orden y dentro del rango que da el padre, hojas a la misma profundidad, ninguna hoja vacía que debió unirse a una vecina, ninguna página alcanzada dos veces ni fuera del archivo, valores grandes con todas sus páginas.
- **La lista de libres:** que no ofrezca una página en uso y que no falte ninguna.

Es el inspector de la Fase 10 (`tkdb check`) sobre una base abierta: el mismo recorrido, la misma lista de hallazgos, y además la profundidad de cada árbol.

Tres cosas que lo distinguen de simplemente leer la base:

- **Lee el disco, no la caché.** Una página dañada en el disco se encuentra aunque la aplicación tenga una copia buena en memoria y siga leyéndola sin notar nada. La prueba del criterio lo comprueba así.
- **No desaloja la caché,** por lo mismo.
- **No bloquea al escritor.** Fija un snapshot, como cualquier lector; mientras corre, las páginas de ese estado no se reutilizan.

## Contadores

```csharp
var db = await TekiumDb.OpenAsync(new DbOptions { Path = "app.tkdb", Diagnostics = true });
// Un Meter llamado "Tekium.Embedded" (TekiumDb.MeterName), con la etiqueta tekium.db = la ruta del archivo.
```

| Instrumento | Tipo | Qué dice |
| --- | --- | --- |
| `tekium.cache.hits` | Contador | Páginas encontradas en la caché |
| `tekium.cache.misses` | Contador | Páginas leídas del archivo porque no estaban |
| `tekium.commit.pages` | Histograma | Páginas escritas por cada commit |
| `tekium.commit.duration` | Histograma (ms) | Lo que tardó cada commit, desde su primera escritura hasta ser durable |
| `tekium.snapshots.live` | Medidor | Lectores con un estado de la base retenido |
| `tekium.snapshots.oldest_age` | Medidor (s) | Hace cuánto se confirmó el estado más viejo que algún lector retiene: lo que impide reutilizar espacio |
| `tekium.tree.depth` | Medidor, con etiqueta `tekium.tree` | Niveles de cada tabla e índice escritos desde que se abrió la base |

Se leen con cualquier cosa que escuche un `Meter`: OpenTelemetry, `dotnet-counters`, un `MeterListener`. `DbOptions.DiagnosticsName` cambia la etiqueta `tekium.db` para distinguir varias bases.

### Apagados, no cuestan

Sin `Diagnostics`, no se crea ningún `Meter` y el motor no cuenta nada: en cada punto donde contaría hay una comprobación de un campo nulo y nada más. Ni siquiera toma la hora al confirmar.

Lectura puntual por clave sobre un millón de filas, caché caliente (BenchmarkDotNet, media ± error):

| Código | Corridas |
| --- | --- |
| Antes de esta fase (commit `4953140`) | 420,0 ± 6,5 ns; 434,6 ± 5,2 ns; 438,5 ± 3,2 ns |
| Con los contadores, apagados | 426,0 ± 4,8 ns; 430,9 ± 5,1 ns; 436,5 ± 4,8 ns |
| Con los contadores, encendidos | 430,5 ± 4,6 ns |

Las tres filas se solapan: de una corrida a otra varía más (420 a 439 ns con el mismo código) que entre una fila y otra. No hay costo que esta medición alcance a ver, ni apagados ni encendidos. Encendidos cuestan un incremento atómico por página leída; con muchos hilos leyendo a la vez ese contador compartido puede notarse, y no lo medí.

## Lo que no es como dice el plan

- **"Ocupación de nodos":** el árbol no garantiza una ocupación mínima (el split 100/0 y los borrados dejan hojas con lo que tengan), así que no hay un porcentaje que comprobar. Lo que sí es invariante, y se comprueba, es que no quede una hoja vacía con hermanas.
- **Profundidad del árbol por índice:** se informa de los árboles que se escribieron desde que se abrió la base, y se actualiza cuando un commit los cambia. Un árbol que sólo se lee no aparece en el medidor; su profundidad sí está en `VerifyAsync`. Los árboles de los índices vectoriales no aparecen en el medidor.
- **`EXPLAIN`:** el plan lo deja para la capa SQL (Fase 12). `Table.ExplainAsync` ya existe desde la Fase 6.

## Pruebas

| Qué | Prueba |
| --- | --- |
| Criterio: una página alterada a mano se detecta y se nombra | `VerifyTests`: con la base abierta y todo en la caché, se altera un byte de cada página en uso, una por una; la verificación nombra la página y a qué pertenece, una página libre no es un hallazgo, y la aplicación sigue leyendo sin notar nada |
| Cifrada | Una página cifrada alterada falla su autenticación |
| Lo que la suma no ve | La lista de libres ofreciendo una página en uso; una hoja vacía con hermanas |
| Un solo commit | Con el estado ya fijado, se borra casi todo y se reescribe el resto seis veces: lo verificado es el estado anterior, sin hallazgos |
| Con el escritor activo | Verificaciones una tras otra mientras otra tarea confirma sin pausa: nunca un hallazgo; y la caché queda como estaba |
| Contadores | `DiagnosticsTests`, escuchando como un exportador: fallos al leer en frío y aciertos al releer; una medida de páginas y duración por commit, ninguna por un commit vacío; lectores vivos (varios sobre el mismo estado) y edad del más viejo; profundidad por árbol; sin la opción no hay `Meter`; cada base bajo su nombre; al cerrar la base su `Meter` desaparece |
| Índices vectoriales | La verificación sigue sus cuatro árboles (`Tekium.Embedded.Vectors.Tests`) |

### Las pruebas detectan el código roto

Veintiuna roturas hechas a mano. Dieciséis se detectaron a la primera; para tres agregué o afiné pruebas; una hubo que repetirla porque no compilaba; y una no se puede probar.

| Rotura | Detectada |
| --- | --- |
| Verificar leyendo por la caché; saltarse la lista de libres; informar otra transacción; aceptar una hoja vacía; no informar la profundidad | Sí |
| Verificar sin retener el snapshot | No al principio: con el escritor concurrente era cuestión de suerte. Se agregó la prueba con el escritor actuando justo después de fijar el estado |
| No contar aciertos; no contar fallos; contar los fallos como aciertos | Sí |
| No informar el commit; informar cero páginas; no publicar los estados al confirmar | Sí |
| Contar estados sin lectores | Sí |
| Tomar la edad del estado más nuevo; contar un lector por estado | No al principio. La prueba ahora usa dos lectores sobre el mismo estado y deja pasar tiempo entre un estado y el siguiente |
| No registrar la profundidad de tablas o de índices | Sí |
| No cerrar el `Meter`; no etiquetar la base | Sí |
| Crear el `Meter` sin la opción | Sí (repetida de otra forma) |
| Tomar la hora al confirmar con los contadores apagados | **No se puede probar:** no cambia nada salvo el costo. Queda a la vista en el código |

## Límites conocidos

- **`VerifyAsync` no revisa el historial retenido del change feed,** igual que el inspector: sólo el estado actual.
- **No repara.** Para eso está `tkdb repair`, con la base cerrada.
- **Retiene espacio mientras corre,** como un backup.
- **Contadores por base, no por tabla,** salvo la profundidad. No hay tiempos por consulta.
- **La profundidad sólo de lo escrito** en esta sesión, y sin índices vectoriales.
- **El costo con los contadores encendidos bajo muchos hilos** no se midió.
- **No hay diagnóstico de un consumidor del change feed atrasado,** que había quedado pendiente de la Fase 6.7 para esta: los contadores de snapshots dicen si algo retiene espacio, no quién.

## Verificación

| Dónde | Qué | Resultado |
| --- | --- | --- |
| macOS (arm64) | `dotnet test -c Release` | 745 pasan, 3 explícitas omitidas |
| macOS (arm64) | Smoke de Native AOT: verificar una base cifrada abierta y leer un contador con un `MeterListener` | Pasa |
| Linux (arm64, contenedor) | `scripts/test-linux.sh`: build, las mismas pruebas y el smoke de AOT | 745 pasan, 3 omitidas; smoke pasa |
| Windows | — | Sin verificar |

Las mediciones sólo se hicieron en macOS.
