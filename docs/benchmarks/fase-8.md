# Fase 8 — índice UUID v7

**Criterio de salida cumplido en parte.** Con UUID v7 que llegan hasta unas decenas por milisegundo, la inserción no degrada y las hojas quedan entre el 96 y el 99 %. Con cientos por milisegundo creados en un bucle cerrado, quedan al 68 %, igual que con claves al azar: no se cumple, y el árbol solo no puede cumplirlo. Pruebas verificadas en macOS y Linux; Windows no se ha verificado.

Medido el 2026-11-16. Una sola máquina (Apple M2 Max, macOS 27.0, arm64), una corrida; Linux en un contenedor sobre la misma máquina, sólo pruebas.

## Lo que se encontró

El plan da por hecho que un UUID v7 "ya viene ordenado" y que basta el split 100/0. Es cierto de un milisegundo al siguiente, pero no dentro de un milisegundo: lo que sigue a la marca de tiempo es azar, y `Guid.CreateVersion7()` no garantiza orden entre dos UUID del mismo milisegundo. Las pruebas y los benchmarks anteriores usaban un UUID por milisegundo, el caso ideal.

Con el código de la Fase 7, bastan cuatro UUID por milisegundo para que las hojas queden a la mitad: la clave nueva casi nunca es la mayor de la hoja, el split 100/0 no se aplica, la hoja se parte en dos mitades y la mitad izquierda no vuelve a recibir nada.

## Mediciones

Un millón de filas de 100 bytes, commits de 10 000, sobre archivo, páginas de 8 KiB (unas 66 filas por hoja). SQLite con las mismas claves, tabla `WITHOUT ROWID`, WAL y `synchronous=FULL`.

| Claves | Ocupación antes | Ocupación ahora | Archivo antes | Archivo ahora | TekiumDB | SQLite | Archivo SQLite |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| v7, uno por milisegundo | 98,8 % | 98,8 % | 118,9 MiB | 118,9 MiB | 1 554 ms | 2 117 ms | 134,9 MiB |
| v7, 4 por milisegundo | 56,5 % | 97,8 % | 207,9 MiB | 120,0 MiB | 1 477 ms | 2 129 ms | 134,9 MiB |
| v7, 16 por milisegundo | 52,2 % | 95,9 % | 225,0 MiB | 122,5 MiB | 1 555 ms | 2 128 ms | 134,9 MiB |
| v7, 64 por milisegundo | 53,6 % | 95,8 % | 219,0 MiB | 122,6 MiB | 1 594 ms | 2 115 ms | 134,8 MiB |
| v7, 1 000 por milisegundo | 68,0 % | 68,9 % | 173,0 MiB | 170,8 MiB | 1 625 ms | 2 216 ms | 133,7 MiB |
| v7, `Guid.CreateVersion7()` al insertar | 68,2 % | 68,3 % | 172,7 MiB | 172,4 MiB | 1 639 ms | 2 235 ms | 133,5 MiB |
| v4, al azar | 68,7 % | 68,7 % | 295,7 MiB | 295,7 MiB | 10 339 ms | 11 863 ms | 133,9 MiB |

- **El tiempo no cambió.** Antes del cambio las mismas siete filas dieron entre 1 551 y 1 605 ms (v7) y 10 223 ms (v4); la diferencia con la tabla está dentro de lo que varía de una corrida a otra (una segunda corrida del código nuevo dio entre 1 449 y 1 676 ms).
- **"Al insertar"** es el bucle más cerrado posible: cada clave se crea justo antes de insertarla, y salen cientos por milisegundo.
- **El v4 cuesta seis veces más tiempo** y deja el archivo en 296 MiB: cada commit reescribe hojas repartidas por todo el árbol, y las páginas viejas quedan libres dentro del archivo. Es el caso malo que el plan pedía medir.
- **SQLite ocupa lo mismo con cualquier orden** porque reparte entre hojas vecinas antes de partir. Aquí no se hace.

## Qué se cambió

Dos reglas en el borde derecho del árbol, las dos necesarias (cada una sola deja el caso de 16 por milisegundo en 59 % y 67 %):

1. **La última hoja se parte donde cae la clave.** Si la clave nueva cae en la mitad alta de la última hoja, lo que hay antes se queda (casi lleno) y la clave y lo que sigue pasan a la hoja nueva. El split 100/0 es el caso en que la clave cae al final.
2. **La penúltima hoja le pasa entradas a la última.** Una clave rezagada que cae en la penúltima hoja, ya llena, no la parte: sus entradas más altas (las menos posibles) pasan al principio de la última hoja y la clave que las separa en el nodo padre se actualiza. Si no caben, o el padre no tiene sitio para la clave nueva, se parte como siempre.

Las dos valen para cualquier clave que crezca con desorden local, no sólo UUID: el árbol sigue sin saber de tipos. Y valen para las tablas y para los índices.

## Por qué no se cumple con cientos por milisegundo

Cuando las claves de un milisegundo ocupan varias hojas, dentro de ese tramo la inserción es al azar, y las hojas de inserción al azar quedan al 69 %. Probé extender la segunda regla a cualquier hoja con sitio en su vecina derecha: 71,4 %, sin efecto útil, y lo deshice. Llegar al 90 % ahí pide otra cosa:

- **Claves que crezcan también dentro del milisegundo** (un generador con contador, como permite el RFC 9562). El plan dice que se use `Guid.CreateVersion7()` y no se generen UUID a mano, así que no lo hice: es decisión tuya.
- **Carga masiva** (`BulkInsertAsync`), que ordena las claves y deja las hojas llenas vengan como vengan. Ya existe.
- **Repartir entre hojas vecinas en todo el árbol**, como SQLite. Es un cambio grande al camino de escritura y sube la ocupación del caso al azar a alrededor del 80 %, no al 90.

El límite entre los dos regímenes depende del tamaño de la fila: es más o menos una hoja de filas por milisegundo. Con filas de 100 bytes son unas 64; con filas de 1 KiB serían unas ocho.

## Lo demás que pedía el plan

| El plan | Resultado |
| --- | --- |
| Guardar el UUID en orden de red | Ya estaba (Fase 5): `TkKey`, `TkKeyWriter` y el formato de fila usan `bigEndian: true`, las sobrecargas sin él están prohibidas en el build, y `TkKeyTests` prueba que el orden de las claves es el de creación |
| Split 100/0 en la hoja de más a la derecha | Ya estaba (Fase 3). Ahora es un caso de la regla 1 |
| Fijar en caché la raíz y el camino derecho | No se hizo: no aporta. Con una caché de sólo 16 páginas, 10 000 commits de inserciones en orden no leyeron del dispositivo ni una vez; ese camino se queda en la caché solo porque cada commit lo reescribe |
| "Todo lo creado entre X e Y" sin índice de fecha | Nuevo: `CreatedBetween`, `CreatedSince` y `CreatedBefore` sobre cualquier columna `Guid`. Sobre la clave es un rango de la clave (`KeyRange`) |

```csharp
var lastHour = Order.Fields.Id.CreatedBetween(now.AddHours(-1), now);
await foreach (var order in orders.QueryAsync(lastHour)) { ... }
```

Los momentos se cortan al milisegundo; `CreatedBetween` incluye los dos extremos y `CreatedBefore` excluye el milisegundo del momento dado. No comprueban que la columna guarde UUID v7: con otra clase de UUID comparan los mismos bytes.

## Pruebas

| Qué | Prueba |
| --- | --- |
| Ocupación por encima del 90 % con 1, 4 y 16 UUID v7 por milisegundo; contenido y estructura correctos | `BTreeUuidTests` |
| Con 2 000 por milisegundo, no peor que al azar (60 %) | `BTreeUuidTests` |
| El camino nuevo con claves y valores de cualquier tamaño, borrados, reaperturas y rollbacks | `BTreePropertyTests` (un millón de operaciones contra el modelo) y `BTreeUuidTests` (claves casi en orden de 4 a 250 bytes) |
| Rangos por fecha de creación sobre la clave, sobre un índice y sobre una columna anulable; extremos; fechas que un UUID v7 no puede llevar | `UuidTimeTests`, sobre archivo y memoria |

### Las pruebas detectan el código roto

Veintidós roturas hechas a mano, una por una. Dieciséis se detectaron a la primera; para dos agregué una prueba; una hubo que repetirla porque la primera forma de romperla no compilaba; y dos no cambian ningún resultado.

| Rotura | Detectada |
| --- | --- |
| No partir la última hoja donde cae la clave | Sí (ocupación) |
| No pasar nunca entradas a la última hoja | Sí (ocupación) |
| Pasar entradas aunque la clave ya exista | Sí |
| No actualizar la clave del padre | Sí |
| Dejar siempre la clave nueva en la hoja llena | Sí |
| Mover también la entrada anterior a la clave | Sí |
| No comprobar que la última hoja tenga sitio | Sí |
| No comprobar que el padre tenga sitio para la clave nueva | No al principio: hacían falta claves de largos muy distintos. Se agregó la prueba de claves casi en orden de 4 a 250 bytes |
| Perder la marca de overflow al mover una entrada, o la de la clave nueva | Sí |
| No enlazar en el padre la copia de la última hoja | Sí |
| No quitar de la hoja las entradas movidas | Sí |
| Pasar entradas a cualquier vecina derecha, no sólo a la última hoja (dos formas) | No, y no es un error: es otra política con el mismo contenido. Medida, deja el caso denso en 71 % |
| `CreatedBetween` sin el milisegundo final | Sí |
| No recortar las fechas anteriores a 1970 | Sí |
| Tomar los bytes equivocados de la marca de tiempo | Sí |
| `CreatedSince` sin cota | Sí |
| `CreatedBefore` incluyendo su milisegundo | Sí |
| Marca de "tiene valor" equivocada en columnas anulables | Sí (la primera forma de romperlo no compilaba; se repitió con otra) |
| Incluir las filas sin valor en "antes de" | Sí |
| Cota superior inclusiva | No al principio: sólo se nota con un UUID que sea su marca de tiempo y ceros. Se agregó la prueba |

## Límites conocidos

- **Cientos de UUID v7 por milisegundo:** ocupación del 68 %, como al azar. Ver arriba.
- **Sólo el borde derecho.** Las dos reglas no tocan el resto del árbol: la inserción al azar queda como estaba.
- **Varios procesos o máquinas generando UUID v7** con relojes desfasados más de lo que cubre una hoja escriben fuera del borde derecho y se comportan como inserción al azar.
- **Las condiciones de fecha de creación no validan la versión del UUID.**
- **Sin la abstracción `IIndex`.** Las tres "familias" del plan resultaron ser el mismo árbol con claves codificadas en orden (más el zone map); no hay una segunda implementación que la pida todavía. Corresponde con el índice vectorial, en la Fase 9.

## Verificación

| Dónde | Qué | Resultado |
| --- | --- | --- |
| macOS (arm64) | `dotnet test -c Release` | 604 pasan, 3 explícitas omitidas |
| macOS (arm64) | Smoke de Native AOT, con un rango por fecha de creación sobre la clave | Pasa |
| Linux (arm64, contenedor) | `scripts/test-linux.sh`: build, las mismas pruebas y el smoke de AOT | 604 pasan, 3 omitidas; smoke pasa |
| Windows | — | Sin verificar |

Las mediciones sólo se hicieron en macOS. La tabla de mediciones es una corrida del código nuevo; la columna "antes" es una corrida del commit anterior (`b3679f2`) con el mismo programa (`-- uuid 1000000`).
