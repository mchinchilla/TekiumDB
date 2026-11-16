# Fase 6 — índices secundarios, codificación ordenada de claves, índices sobre expresiones y parciales

**Criterio de salida cumplido en macOS y en Linux.** El orden de las claves codificadas coincide con el de los valores para todos los tipos; un índice sobre expresión y uno parcial se mantienen al insertar; y el planificador sólo usa el parcial cuando puede probar que el predicado de la consulta lo implica. Windows no se ha verificado.

Medido el 2026-11-16. Una sola máquina (Apple M2 Max, macOS 27.0, arm64), una corrida por benchmark; Linux en un contenedor sobre la misma máquina, sólo pruebas.

## Criterio de salida

| Criterio | Prueba | Resultado |
| --- | --- | --- |
| El orden de las claves codificadas coincide con el de los valores, para todos los tipos | `TkKeyWriterTests`: pares al azar (CsCheck) de cada una de las 21 clases de columna, comparando el orden de los bytes con el de `CompareTo`; más los valores anulables, varias columnas seguidas contra el orden de la tupla, y las claves cortadas | Pasa |
| Un índice sobre expresión y uno parcial se mantienen al insertar | `IndexTests`: 1 800 inserciones, actualizaciones y borrados al azar en doce transacciones (una de cada cuatro sin confirmar) sobre una tabla con cinco índices; tras cada una, 23 consultas se comparan con filtrar un modelo en memoria y se cuentan las entradas de cada índice | Pasa, sobre archivo y sobre memoria |
| El planificador sólo usa el parcial cuando puede probar que el predicado lo implica | `IndexTests`: cuatro consultas que lo implican (tal cual, en otro orden, con más condiciones, con un rango contenido) lo usan; siete que no (falta una mitad, rango más ancho, otro valor) no. `KeyRangeTests` prueba la pregunta de fondo contra un modelo clave por clave | Pasa |

### Las pruebas detectan el código roto

Trece roturas hechas a mano, una por una. Diez se detectaron a la primera; tres no, y las pruebas se reforzaron hasta que sí.

| Rotura | Detectada |
| --- | --- |
| `decimal`: no quitar los ceros finales (1 y 1,00 con claves distintas) | Sí |
| Texto: no escapar el cero interior | No al principio: el generador de textos casi nunca producía ceros. Ahora los produce y hay una prueba específica |
| `double`: no igualar −0 con 0 | Sí |
| Usar el índice parcial sin probar la implicación | Sí |
| Probar la implicación al revés | Sí |
| Ignorar que una cota es exclusiva al probar la implicación | No al principio. Se agregó `KeyRangeTests` |
| No quitar la entrada vieja al actualizar | Sí |
| No tocar los índices al borrar | Sí |
| No liberar el árbol de un índice que ya no se declara | Sí (páginas perdidas) |
| Leer un índice cuya definición guardada es otra | No al principio, dentro de una transacción de escritura. Se agregó el caso |
| Cortar la cota superior inclusiva del rango | Sí |
| Mantener la cota inferior exclusiva | Sí |
| No marcar la transacción cuando una operación falla a medias | Sí |

## Cómo quedó hecho

**Codificación.** `TkKeyWriter` escribe cada valor de modo que comparar bytes sea comparar valores, y que ningún valor codificado sea prefijo de otro: por eso varias columnas seguidas ordenan como la tupla y el árbol sigue sin saber de tipos.

| Tipo | Bytes |
| --- | --- |
| Enteros | Big-endian, con el bit de signo invertido en los que tienen signo |
| `float`, `double` | Los bits IEEE con el signo invertido (los negativos, todos invertidos). NaN va primero y −0 es 0, como en `CompareTo` |
| `decimal` | Por valor, no por representación: signo y exponente en un byte, dígitos de a pares, marca de fin. 18 bytes como máximo |
| `DateTimeOffset` | El instante UTC; el desplazamiento no entra en la clave, así que el mismo momento en dos zonas es una sola clave |
| `DateTime` | Los ticks, sin la clase (`Kind`) |
| `Guid` | Orden de red: un UUID v7 ordena por su fecha |
| Texto, `byte[]` | Los bytes (UTF-8 para el texto), con el cero escapado como `00 FF` y `00 00` al final |
| Anulables | Un byte delante: 0 sin valor (ordena primero), 1 con valor |

El texto ordena por punto de código Unicode, sin cultura. No es el orden de `string.CompareOrdinal` para caracteres fuera del plano básico (que en UTF-16 ordenan antes que U+E000–U+FFFF).

**Índices.** Cada índice es un B+Tree más en el mismo archivo. Su entrada es `valores indexados ‖ clave primaria` y el valor guarda dónde empieza la clave. Se declaran con `[TkIndex]` y el generador emite lo necesario; el motor los mantiene en la misma transacción que la fila.

- **Sobre expresión:** `[TkIndex]` en una propiedad calculada. El catálogo guarda el texto de la expresión; si cambia, el índice se reconstruye.
- **Parcial:** `Where` nombra una propiedad `bool`. El generador lee su cuerpo y lo convierte en condiciones (`columna` comparada con constante, unidas por `&&`); lo que no puede convertir es un error de compilación (`TK0012`), no un índice que nunca se usa.
- **Cambios:** la definición de cada índice va al catálogo. Un índice cuya definición guardada no es idéntica a la declarada no se lee nunca; la siguiente escritura con ese tipo lo reconstruye, o lo elimina si ya no se declara. No hay migraciones.

**Planificador.** Por reglas, sin estadísticas: clave exacta; después el índice con más campos iniciales fijados a un valor; después un rango de la clave; después un índice con rango en su primer campo o uno parcial; y si nada sirve, la tabla. Un índice parcial sólo es candidato si cada condición suya cubre el rango que la consulta impone al mismo campo. Como la codificación conserva el orden, esa prueba es una comparación de rangos de bytes, igual para todos los tipos. Elija lo que elija, cada fila que sale se vuelve a comprobar contra el filtro completo.

## Lo que cuesta y lo que ahorra

100 000 facturas de 1 000 clientes (clave UUID v7, cliente, fecha, total `decimal`, pagada, un texto corto), páginas de 8 KiB, caché caliente. Tres índices: por cliente, por fecha y uno parcial por total sobre las impagas (una de cada diez). SQLite con las mismas filas y los mismos tres índices, en modo WAL con `synchronous=FULL`.

### Escribir

10 000 filas en un commit, sobre una base nueva.

| | Tiempo | Memoria |
| --- | ---: | ---: |
| TekiumDB sin índices | 15,1 ms | 2,7 MB |
| TekiumDB con tres índices | 22,9 ms | 4,7 MB |
| SQLite con tres índices | 31,6 ms | 9,4 MB |

Tres índices encarecen la inserción un 52 %.

La escritura en tablas sin índices no cambió: el mismo benchmark sobre el commit anterior da 14,98 ms y 2,59 MB. Leer una fila por su clave tampoco: 342 ns y 326 B (antes 338 ns).

### Consultar

Cada consulta construye el objeto de cada fila que devuelve.

| Consulta | Filas | Recorriendo la tabla | Por el índice | SQLite |
| --- | ---: | ---: | ---: | ---: |
| Las facturas de un cliente | ~100 | 10 389 µs, 20 MB | 69 µs, 23 KB | 124 µs, 19 KB |
| Las facturas de un día | ~270 | — | 149 µs, 58 KB | 309 µs, 50 KB |
| Impagas de más de 9 900 (índice parcial) | ~100 | — | 78 µs, 22 KB | 128 µs, 18 KB |

Por el índice la consulta es unas 150 veces más rápida que recorrer la tabla, y entre 1,6 y 2,1 veces más rápida que SQLite. La desviación de estas medidas es alta (entre 12 y 20 µs sobre 70–150 µs), así que la ventaja sobre SQLite es de ese orden, no ese número exacto.

Recorrer la tabla cuesta 20 MB porque hoy cada fila se decodifica a objeto antes de comprobar el filtro. Comprobarlo sobre los bytes de la fila lo evitaría; no se hizo.

## Límites conocidos

- **Sin columnas incluidas (`INCLUDE`).** El índice lleva sólo los valores indexados y la clave; toda consulta va después a la tabla por cada fila.
- **Sin `OR`, sin "distinto de", sin prefijos de texto.** Las condiciones son comparaciones y rangos unidos por `And`.
- **Sin índices únicos.** Llegan con las restricciones, en la Fase 6.5.
- **Planificador sin estadísticas.** Entre dos índices igual de buenos según las reglas gana el declarado primero. Un índice parcial implicado por la consulta se prefiere a recorrer la tabla aunque la consulta no acote ninguno de sus campos; si el índice contiene casi toda la tabla, eso es más lento que recorrerla.
- **Tamaño de la entrada.** Los valores indexados más la clave no pueden pasar de 1/16 de página (512 bytes con páginas de 8 KiB). La fila que lo excede se rechaza antes de tocar nada.
- **Construcción en la transacción.** Un índice nuevo se construye fila por fila dentro de la primera transacción que escribe con el tipo nuevo, con todas sus páginas en memoria hasta el commit. La carga masiva es la Fase 6.8.
- **Dos versiones de la aplicación alternándose.** Quien escribe con un tipo que no declara un índice lo elimina (no podría mantenerlo). Si otra versión lo declara, lo reconstruye en su siguiente escritura.
- **El cambio de una expresión se detecta por su texto.** Si cambia algo que la expresión llama, pero no su texto, el índice queda desactualizado sin aviso. La expresión debe depender sólo de las columnas de su fila.
- **La API de consulta no es la del ejemplo del plan.** El plan muestra `docs.RangeAsync(x => x.CreatedAt, from, to)`. Leer esa lambda exige árboles de expresión, que son reflexión en tiempo de ejecución, y el propio plan la prohíbe en el núcleo. Se usa `Order.Fields.CreatedAt.Between(from, to)`, con campos que emite el generador.
- **Sin la abstracción `IIndex`.** El plan la nombra para tres familias de índices; con una sola no se introdujo. Corresponde cuando llegue la segunda, en la Fase 7.

## Verificación

| Dónde | Qué | Resultado |
| --- | --- | --- |
| macOS (arm64) | `dotnet test -c Release` | 463 pasan, 3 explícitas omitidas |
| macOS (arm64) | Smoke de Native AOT, con una tabla indexada (columna, expresión y parcial) | Pasa |
| Linux (arm64, contenedor) | `scripts/test-linux.sh`: build, las mismas pruebas y el smoke de AOT | 463 pasan, 3 omitidas; smoke pasa |
| Windows | — | Sin verificar |

Los benchmarks sólo se corrieron en macOS.

Además de las pruebas del criterio: un corte de energía simulado antes de cada operación de siete commits sobre una tabla indexada, tras el cual la tabla y sus índices coinciden y no se pierde ninguna página; un fallo de lectura inyectado en cada punto de una escritura, tras el cual la transacción sólo puede deshacerse; y la reconstrucción y eliminación de índices contando páginas.
