# Fase 7 — índice temporal con zone maps y split 100/0

**Criterio de salida cumplido en macOS; pruebas verificadas también en Linux.** Un rango de fechas sobre diez millones de filas queda por debajo del umbral. El plan dejaba el umbral por fijar en el benchmark; lo fijé antes de medir y está abajo. Windows no se ha verificado.

Medido el 2026-11-16. Una sola máquina (Apple M2 Max, macOS 27.0, arm64), una corrida; Linux en un contenedor sobre la misma máquina, sólo pruebas.

## El umbral

> Sobre diez millones de filas, un rango de fechas por zone map es más rápido que SQLite con un índice sobre la fecha, en rangos de una hora, un día y un mes, construyendo la fila completa, con el código ya compilado.

Lo fijé yo; si prefieres otro (un tiempo absoluto, o contra otra referencia), las cifras de abajo alcanzan para evaluarlo.

## Lo que se midió

Diez millones de muestras, una por segundo (115 días), con clave creciente tipo UUID v7, unos 70 bytes por fila, páginas de 8 KiB. Tres formas de la misma tabla: con zone map sobre la fecha (ventanas de una hora), con índice secundario sobre la fecha, y SQLite con un índice sobre la fecha. Cada rango empieza a mitad de la tabla. "Primera" es la primera ejecución tras abrir la base; "caliente" es la mediana de la segunda mitad de las ejecuciones (200, 40 u 8 según el rango).

| Rango | Filas | Zone map | Índice secundario | SQLite con índice | Recorrer la tabla |
| --- | ---: | ---: | ---: | ---: | ---: |
| Una hora, caliente | 3 601 | 0,75 ms | 1,21 ms | 2,91 ms | 868 ms |
| Un día, caliente | 86 401 | 10,7 ms | 29,1 ms | 69,4 ms | 873 ms |
| Treinta días, caliente | 2 592 001 | 310 ms | 884 ms | 2 102 ms | — |
| Una hora, primera | 3 601 | 18,5 ms | 5,8 ms | 6,9 ms | 1 571 ms |
| Un día, primera | 86 401 | 16,0 ms | 37,9 ms | 71,5 ms | 1 583 ms |
| Treinta días, primera | 2 592 001 | 489 ms | 1 235 ms | 2 135 ms | — |

- **Contra SQLite con índice (el umbral):** 3,9 veces más rápido en una hora, 6,5 en un día y 6,8 en treinta días. Se cumple.
- **Contra el índice secundario propio:** 1,6, 2,7 y 2,9 veces más rápido. El índice va a la tabla una vez por fila encontrada; el zone map lee el tramo de corrido.
- **Contra recorrer la tabla:** tres órdenes de magnitud en el rango de una hora.

**Una salvedad sobre la primera ejecución.** En el rango de una hora, la primera ejecución del zone map tarda 18,5 ms, más que SQLite (6,9 ms). Es la primera consulta del proceso: paga compilar el código, que SQLite, al ser nativo, no paga. En una primera corrida de esta misma medición con sólo cinco repeticiones, el zone map daba 4,4 ms "caliente" contra 3,5 de SQLite, por la misma razón; con más repeticiones baja a 0,75 ms. El umbral habla del código ya compilado.

### Lo que cuesta al escribir

Un millón de filas insertadas una por una en una transacción, mejor de cinco:

| | Tiempo | Contra nada |
| --- | ---: | ---: |
| Sin nada sobre la fecha | 442 ms | 1,00× |
| Con zone map | 456 ms | 1,03× |
| Con índice secundario | 742 ms | 1,68× |

La primera versión del zone map costaba 792 ms (1,77×, más que el índice): leía la fila anterior y escribía en el árbol del mapa en cada inserción. Ahora lo que la transacción aprende de cada ventana se acumula en memoria y se escribe una vez al confirmar. En esta medición el archivo mide lo mismo con zone map que sin nada (128 MiB): el mapa son 278 ventanas para un millón de filas, y 2 778 para diez millones.

## Cómo quedó hecho

De lo que el plan pide para el índice temporal, dos cosas ya existían:

- **La codificación.** Desde la Fase 6 un `DateTimeOffset` se indexa por su instante UTC, en bytes que ordenan como la fecha; el desplazamiento no entra en la clave.
- **El split 100/0.** Desde la Fase 3 el árbol reconoce la inserción al final y parte la hoja dejándola llena. Vale para cualquier árbol, también el de un índice sobre fechas: una prueba nueva lo comprueba (20 000 inserciones en orden de fecha dejan las hojas del índice a más del 90 %).

Lo nuevo son los **zone maps**, y no están hechos como el plan los describe. El plan dice que cada hoja guarda su fecha mínima y máxima en el encabezado. Eso obliga a leer cada hoja para decidir si se salta, y en un árbol copy-on-write sin enlaces entre hojas llevarlo a los nodos de arriba significa cambiar el formato de los nodos internos. Lo hice al revés, con el mismo efecto:

- Un zone map es un árbol chico con **una entrada por ventana de tiempo** (una hora por defecto): la clave más baja y la más alta de las filas cuya fecha cae en esa ventana.
- Una consulta por rango de fechas busca las ventanas que toca, une los tramos de claves y lee esos tramos de la tabla de corrido, descartando las filas de los bordes que no cumplen.
- Si las filas se escriben en orden de fecha bajo claves que crecen, cada ventana es un tramo corto y contiguo. Si las claves no tienen que ver con la fecha, las ventanas abarcan toda la tabla: sigue siendo correcto, pero no ahorra nada.
- Las ventanas sólo se ensanchan. Borrar filas, o cambiarles la fecha, no las achica: la consulta puede leer de más, nunca de menos.

Se declara con `[TkZoneMap]` sobre una columna `DateTimeOffset`, `DateTime` o `DateOnly`. Comparte ciclo de vida con los índices: lo construye la primera escritura con el tipo que lo declara, la carga masiva lo mantiene, y lo elimina quien escribe con un tipo que no lo declara.

**Planificador.** Si la columna tiene zone map e índice, el zone map se usa cuando el rango abarca más de una ventana o no tiene una de sus cotas, y el índice para una fecha exacta o un rango dentro de una ventana. Una clave exacta o un rango de claves siguen yendo primero.

### Las pruebas detectan el código roto

Diecisiete roturas hechas a mano. Quince se detectaron a la primera, para una se agregó una prueba, y una no cambia el comportamiento.

| Rotura | Detectada |
| --- | --- |
| Las escrituras no anotan su ventana | Sí |
| Las ventanas no se escriben al confirmar | Sí |
| Una consulta dentro de la transacción no ve lo anotado | Sí |
| Una ventana en memoria no se ensancha hacia abajo | No al principio. Se agregó la prueba de claves en cualquier orden |
| Una ventana guardada no se ensancha hacia abajo | Sí |
| Una ventana guardada no se ensancha hacia arriba | Sí |
| La última ventana del rango queda fuera | Sí |
| La primera ventana se busca una más tarde | Sí |
| Tramos que se solapan se leen dos veces | Sí |
| La clave más alta de un tramo queda fuera | Sí |
| No se construye sobre las filas que ya había | Sí |
| La carga masiva no lo mantiene | Sí |
| Se usa un zone map que todavía no existe | Sí |
| Lo anotado por un tipo no se escribe antes de que otro cambie los índices | Sí |
| Los rangos cortos van al zone map | Sí |
| Los rangos anchos van al índice | Sí |
| Una fecha sin valor tiene ventana | No: la descarta igual la comprobación de longitud |

La primera pasada de estas roturas no valió: una prueba que yo acababa de agregar estaba mal escrita y fallaba sola, así que todas las roturas aparecían como detectadas. La corregí y repetí las diecisiete.

## Límites conocidos

- **Depende de que la clave crezca con la fecha.** Con claves al azar no ahorra nada, y no avisa.
- **Las ventanas no se achican.** Una tabla de la que se borra mucho, o cuyas filas cambian de fecha, va perdiendo precisión. No hay todavía una forma de reconstruir el zone map sin quitarlo y volver a declararlo.
- **El tamaño de la ventana es fijo** (`BucketMinutes`). Ventanas más chicas leen menos filas de más y guardan más entradas.
- **Sólo fechas**: `DateTimeOffset`, `DateTime` y `DateOnly`. La misma idea serviría para cualquier columna que crezca con la clave.
- **El planificador elige entre zone map e índice por el ancho del rango en ventanas**, no por cuántas filas hay.
- **Los benchmarks no se corrieron con la caché del sistema operativo vacía.** "Primera" es con la caché del motor vacía, pero el archivo estaba en la del sistema.

## Verificación

| Dónde | Qué | Resultado |
| --- | --- | --- |
| macOS (arm64) | `dotnet test -c Release` | 589 pasan, 3 explícitas omitidas |
| macOS (arm64) | Smoke de Native AOT, con zone map e índice sobre la misma fecha | Pasa |
| Linux (arm64, contenedor) | `scripts/test-linux.sh`: build, las mismas pruebas y el smoke de AOT | 589 pasan, 3 omitidas; smoke pasa |
| Windows | — | Sin verificar |

Las mediciones sólo se hicieron en macOS.
