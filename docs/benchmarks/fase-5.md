# Fase 5 — catálogo, filas tipadas y generador de código

**Criterio de salida cumplido en macOS y en Linux.** Ida y vuelta de todos los tipos primitivos y anulables; una fila escrita con la versión 1 se lee con el lector de la versión 2; Native AOT sigue funcionando. Windows no se ha verificado.

Medido el 2026-11-16. Una sola máquina (macOS, arm64); Linux en un contenedor sobre la misma máquina.

## Criterio de salida

| Criterio | Prueba | Resultado |
| --- | --- | --- |
| Ida y vuelta de todos los tipos primitivos y anulables | `RoundTripTests`: filas al azar (CsCheck) con las 21 clases de columna, cada una en su forma no anulable y anulable, escritas y leídas a través de la base con el serializador generado; más los extremos de cada tipo | Pasa, sobre archivo y sobre memoria |
| Una fila de la versión 1 se lee con el lector de la versión 2 | `SchemaEvolutionTests`: la versión 2 renombra una propiedad, quita una columna, agrega anulables y no anulables y cambia el orden | Pasa; leer con el tipo nuevo no escribe ni un byte |
| AOT sigue funcionando | `TekiumDB.AotSmoke`: base cifrada, escrita con un tipo, reabierta y leída con su versión siguiente, publicada con Native AOT | Pasa en macOS y en Linux |

La igualdad de .NET no mira todo lo que tiene que volver, así que las pruebas lo comprueban aparte: la clase (`Kind`) de un `DateTime`, el desplazamiento de un `DateTimeOffset`, el signo del cero, la escala de un `decimal` (`0.10` no vuelve como `0.1`) y la diferencia entre un `byte[]` nulo y uno vacío.

### Las pruebas detectan el código roto

| Rotura | Pruebas que fallan |
| --- | --- |
| No saltar las columnas que el tipo ya no tiene | Las de evolución de esquema y la del formato de fila |
| Aceptar una columna con nulos guardados en una propiedad no anulable | La de cambios incompatibles |
| No guardar la clase del `DateTime` | Las de ida y vuelta |
| Recordar una versión de esquema de una transacción que hizo rollback | Ninguna, al principio. La prueba se reforzó y ahora falla con esa rotura |

## Qué cuesta la API tipada

Leer una fila por su clave y construir el objeto, sobre 100 000 filas de seis columnas (clave UUID v7, dos textos, un entero, un `double` y una fecha) con la caché caliente. SQLite hace lo mismo con un comando preparado.

| | Tiempo por fila | Memoria por fila |
| --- | ---: | ---: |
| SQLite (`Microsoft.Data.Sqlite`) | 2 016 ns | 670 B |
| TekiumDB (`Table<T>.GetAsync`) | 336 ns | 326 B |

Seis veces más rápido. Los 326 bytes son la fila que se devuelve: el objeto y sus dos cadenas. La lectura cruda del árbol, sin catálogo ni serializador, medía unos 390 ns sobre un millón de filas en las fases anteriores; son tablas de tamaño distinto y no se comparan directamente, pero el catálogo y el serializador no se notan al lado de bajar por el árbol.

No se midió la escritura a través de la API tipada.

Se reproduce con `dotnet run -c Release --project tests/TekiumDB.Benchmarks -- --filter '*TypedTableBenchmarks*'`.

## Cómo quedó hecho

- **Catálogo.** Es el árbol al que apunta el slot de raíz. Guarda dónde están las filas de cada tabla y las columnas de cada versión de su esquema. Cada tabla tiene su propio B+Tree. Como el catálogo es un árbol más, copy-on-write lo versiona con todo lo demás: un lector ve las tablas y los esquemas de su propio commit, y crear una tabla o cambiar su esquema es parte de la transacción que lo hace.
- **Fila.** Un byte con la versión del esquema, un bit por columna que dice si es nula, y los valores que no lo son. Los tipos de tamaño fijo van en little-endian; texto y bytes llevan prefijo de longitud.
- **Evolución.** Las columnas se emparejan por nombre. Una columna nueva se lee con su valor por defecto en las filas viejas; una quitada se salta. Lo que no tiene puente se rechaza nombrando la tabla y la columna: cambiar el tipo de una columna, cambiar la clave, o declarar no anulable una columna que tiene nulos guardados.
- **Generador.** Por cada clase `[TkTable]` escribe la lista de columnas, la escritura, la lectura (para cualquier versión guardada) y la codificación de la clave. Nueve diagnósticos (`TK0001` a `TK0009`) explican lo que no puede ser una tabla.

## Qué no cubre

- **Filas grandes.** Resuelto después de cerrar la fase: ver [páginas de overflow](overflow.md). Ni las filas ni los esquemas tienen ya el límite de un cuarto de página.
- **Claves.** Sólo `Guid`, `int`, `long`, `uint`, `ulong` y `string`, de una columna. La codificación ordenada de todos los tipos es de la Fase 6. La cadena vacía no puede ser clave.
- **Consultas.** Sólo por clave y recorrido completo. Los rangos y los índices secundarios son de la Fase 6 en adelante.
- **Borrar una tabla, o las versiones de esquema que ya no usa ninguna fila.** No hay operación para eso.
- **255 versiones de esquema por tabla.** Es el byte de versión. Alternar dos formas de un tipo sobre la misma tabla gasta una versión cada vez.
- **Windows.** Nada de esto se ha corrido en Windows.
