# Fase 6.5 — restricciones declarativas: CHECK, UNIQUE y FOREIGN KEY

**Criterio de salida cumplido en macOS y en Linux.** Una violación aborta la transacción nombrando la restricción y la fila, y el planificador usa UNIQUE para acotar el resultado a una fila. Windows no se ha verificado.

Medido el 2026-11-16. Una sola máquina (Apple M2 Max, macOS 27.0, arm64), una corrida por benchmark; Linux en un contenedor sobre la misma máquina, sólo pruebas.

## Criterio de salida

| Criterio | Prueba | Resultado |
| --- | --- | --- |
| Una violación aborta la transacción nombrando la restricción y la fila | `ConstraintTests`: cada clase de restricción, violada al insertar, al actualizar y al borrar. La excepción lleva el nombre de la restricción, la tabla y la clave de la fila; después de ella `CommitAsync` se niega y la base queda como estaba | Pasa, sobre archivo y sobre memoria |
| El planificador usa UNIQUE para acotar el resultado a una fila | `ConstraintTests`: con todas las columnas de un índice único fijadas a un valor el plan es `UniqueLookup` y la lectura se detiene en la primera entrada. No lo es si falta una columna, si se busca "sin valor" (esas filas están exentas), ni en un único parcial cuya condición la consulta no implica | Pasa |

Además, 1 500 escrituras al azar sobre dos tablas con siete restricciones, cada una en su transacción, se comparan una por una con un modelo en memoria que aplica las mismas reglas: la misma operación debe aceptarse o rechazarse, y rechazarse por la misma restricción.

### Las pruebas detectan el código roto

Diecisiete roturas hechas a mano. Quince se detectaron a la primera; para las otras dos se agregaron pruebas.

| Rotura | Detectada |
| --- | --- |
| No evaluar el CHECK | Sí |
| Lanzar la violación sin abortar la transacción | Sí |
| Tratar las filas sin valor como repetidas en un índice único | Sí |
| Que una fila choque consigo misma al actualizarse | Sí |
| No comprobar UNIQUE al escribir | Sí |
| No comprobar UNIQUE al construir el índice sobre filas existentes | Sí |
| No buscar la fila referida | Sí |
| No permitir que una fila se refiera a sí misma | Sí |
| Borrar sin preguntar quién se refiere a la fila | Sí |
| No escapar una clave de texto al buscarla en el índice de quien se refiere | Sí |
| Que una fila que se refiere a sí misma impida su propio borrado | Sí |
| No validar las filas existentes al añadir una restricción | Sí |
| No anotar en la tabla referida quién se refiere a ella | Sí |
| Tratar como única la búsqueda de filas sin valor | Sí |
| Tratar como única una búsqueda que no fija todas las columnas | Sí |
| Dar la misma definición a un índice único y a uno que no lo es | No al principio. Se agregó la prueba del índice que pasa a ser único |
| No quitar del catálogo las restricciones que dejan de declararse | No al principio. Se agregó quitarlas y volver a declararlas |

## Cómo quedó hecho

- **CHECK.** `[TkCheck]` sobre una propiedad `bool`. El generador emite la llamada a la propiedad; el catálogo guarda su nombre y el texto de la expresión.
- **UNIQUE.** `Unique = true` en un `[TkIndex]`. Es una propiedad del índice, y entra en su definición: un índice no único del mismo nombre no se toma por único. Antes de escribir se mira si el índice ya tiene una entrada con los mismos valores para otra fila. Las filas sin valor en alguna columna del índice quedan exentas, como en SQL.
- **FOREIGN KEY.** `[TkForeignKey(typeof(Otra))]` sobre una columna del tipo de la clave de la otra tabla. Al escribir se busca la fila referida. Al borrar, la tabla referida consulta el índice de cada tabla que se refiere a ella; por eso cada clave foránea trae un índice que empieza por su columna (o aprovecha uno que ya exista). Sólo hay rechazo: no hay borrado ni actualización en cascada.
- **Orden y momento.** Cada operación comprueba CHECK, después FOREIGN KEY, después UNIQUE, antes de tocar ningún árbol. No hay comprobación diferida al commit.
- **Añadir y quitar.** Las restricciones siguen al tipo que escribe, como los índices. La primera escritura con un tipo que declara una restricción nueva la comprueba contra todas las filas de la tabla. Quien escribe con un tipo que no las declara las quita.

## Lo que cuestan

10 000 facturas en un commit, sobre una base con 1 000 clientes. Con restricciones: un CHECK, un UNIQUE sobre el número y una FOREIGN KEY al cliente, que entre las dos últimas mantienen dos índices. SQLite con las mismas restricciones y un índice sobre el cliente, en modo WAL con `synchronous=FULL` y `foreign_keys=ON`.

| | Tiempo | Memoria |
| --- | ---: | ---: |
| TekiumDB sin restricciones ni índices | 15,2 ms | 2,6 MB |
| TekiumDB con CHECK, UNIQUE y FOREIGN KEY | 28,1 ms | 13,7 MB |
| SQLite con las mismas | 32,7 ms | 10,4 MB |

Las tres restricciones casi duplican el tiempo de inserción (+85 %). Buena parte es mantener sus dos índices: en la Fase 6, tres índices sin restricciones costaban +52 %. La memoria se multiplica por cinco. No medí de dónde viene; lo que cambia por fila es un recorrido del índice único y una búsqueda en la tabla referida.

**Las tablas sin restricciones no pagan.** 2 000 transacciones de una fila sobre una tabla sin índices ni restricciones, en memoria: 27,3 ms en el commit anterior y 28,3 ms ahora, con una desviación de unos 4 ms en ambas. La diferencia está dentro del ruido.

## Límites conocidos

- **Una violación aborta la transacción entera**, como pide el plan. No se puede atrapar la excepción y seguir con la misma transacción. La clave primaria repetida (`TkDuplicateKeyException`) sigue sin abortar, como antes.
- **Sin comprobación diferida.** Dos filas que se refieren una a otra se insertan con la referencia vacía y se completan después.
- **Sin cascadas.** Borrar una fila referida se rechaza; no hay `ON DELETE CASCADE` ni `SET NULL`.
- **La clave foránea apunta siempre a la clave primaria** de la otra tabla, de una sola columna.
- **El CHECK es código de la aplicación**, no una expresión guardada que el motor evalúe: el catálogo guarda su texto para poder inspeccionarlo y para detectar que cambió, pero quien lo evalúa es la propiedad compilada. Si cambia algo que la propiedad llama, pero no su texto, las filas existentes no se vuelven a comprobar.
- **Añadir una restricción recorre la tabla** dentro de la primera transacción que escribe con el tipo nuevo.
- **Dos versiones de la aplicación alternándose** quitan y vuelven a validar las restricciones en cada cambio, igual que pasa con los índices. Mientras una versión que no las declara escribe, nada las hace cumplir.
- **El planificador sólo usa UNIQUE para detener la búsqueda.** No hay todavía otras decisiones que dependan de saber que el resultado es una fila (eso llega con los joins de la capa SQL).

## Verificación

| Dónde | Qué | Resultado |
| --- | --- | --- |
| macOS (arm64) | `dotnet test -c Release` | 493 pasan, 3 explícitas omitidas |
| macOS (arm64) | Smoke de Native AOT, con una restricción de cada clase violada y nombrada | Pasa |
| Linux (arm64, contenedor) | `scripts/test-linux.sh`: build, las mismas pruebas y el smoke de AOT | 493 pasan, 3 omitidas; smoke pasa |
| Windows | — | Sin verificar |

Los benchmarks sólo se corrieron en macOS.
