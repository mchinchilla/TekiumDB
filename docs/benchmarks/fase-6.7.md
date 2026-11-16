# Fase 6.7 — change feed post-commit

**Criterio de salida cumplido en macOS; pruebas verificadas también en Linux.** Un consumidor reanuda desde un id de transacción sin perder cambios, y la latencia de escritura no se mueve con el feed activo cuando los commits son durables. Windows no se ha verificado.

**Dos salvedades medidas:** conservar commits cuesta espacio en proporción a cuántos se conservan, y en memoria (sin fsync) encarece cada commit unos 7 µs y el consumidor no alcanza a un escritor que escribe sin parar.

Medido el 2026-11-16. Una sola máquina (Apple M2 Max, macOS 27.0, arm64), una corrida por benchmark; Linux en un contenedor sobre la misma máquina, sólo pruebas.

## Criterio de salida

| Criterio | Prueba | Resultado |
| --- | --- | --- |
| Un consumidor reanuda desde un id de transacción sin perder cambios | `ChangeFeedTests`: ocho sesiones, cada una abre la base, hace entre 1 y 30 commits al azar (algunos deshechos), y un consumidor retoma desde el id donde quedó en la sesión anterior. Aplicar lo que recibe reconstruye la tabla exacta; ninguna transacción llega dos veces | Pasa, sobre archivo y sobre memoria |
| La latencia de escritura no se mueve con el feed activo | `ChangeFeedBenchmarks`, abajo | Con commits durables no se mueve. En memoria, +14 % |

## Cómo quedó hecho

El plan dice que el feed sale de las versiones que el MVCC ya guarda. Así está hecho: no hay un registro de cambios aparte, y el escritor no escribe los cambios en ningún lado.

- **Los cambios se calculan comparando dos estados.** Lo que hizo un commit es la diferencia entre el árbol de la tabla antes y después. Copy-on-write deja casi todo el árbol intacto entre un commit y el siguiente, y una página compartida dice lo mismo en los dos estados: la comparación se la salta sin leerla. Cuatro cambios en una tabla de 60 000 filas leen menos de 60 páginas.
- **Cada commit anota dónde estaba el anterior.** En su propio catálogo, con el id de esa transacción. Esa historia viaja con el commit, así que es atómica con él y sobrevive al cierre.
- **Los estados conservados no pierden sus páginas.** El pager no reutiliza las páginas de ningún estado desde el más viejo que la historia nombra. Ese piso se repone al abrir la base, antes de la primera escritura.
- **El consumidor lee después y aparte.** Toma un commit, fija el estado anterior a él para que el escritor no se lleve sus páginas, calcula los cambios en memoria, suelta, y los entrega. Después espera una señal que el commit dispara sin ejecutar nada del consumidor.
- **Horizonte.** `ChangeRetention` es el número de commits que se conservan. El consumidor que queda más atrás recibe `TkChangeFeedException`: no se le saltan cambios en silencio.
- **Sin huecos.** Si una sesión abre la base sin conservar commits y escribe, la historia anterior se borra. Si empieza a conservarse cuando ya había datos, o se recorta, queda marcada como incompleta, y pedir "desde el principio" falla en vez de entregar una parte.

Lo que informa es el efecto neto de cada transacción sobre cada fila: una fila insertada y borrada en la misma transacción no aparece, y dos actualizaciones son una.

### Las pruebas detectan el código roto

Diecisiete roturas hechas a mano. Trece se detectaron a la primera. Tres no, y se agregaron pruebas. Una no cambia el comportamiento.

| Rotura | Detectada |
| --- | --- |
| El escritor ignora el piso de retención | Sí |
| No reponer el piso al abrir | No al principio. Se agregó reabrir y escribir mucho antes de leer la historia |
| El escritor ignora los estados fijados | No al principio. Se agregó la prueba del pager con el piso avanzando |
| Permitir fijar un estado que ya está bajo el piso | No al principio. La misma prueba |
| No recortar nunca la historia | Sí |
| No borrar la historia abandonada | Sí |
| No marcar incompleta la historia que empieza tarde | Sí |
| No marcar incompleta la historia recortada | Sí |
| No despertar a los consumidores | Sí |
| Anotar una transacción que no cambió nada | Sí |
| Anotar la raíz nueva en vez de la anterior | Sí |
| No mover el piso al recortar | Sí |
| Saltarse subárboles distintos | Sí |
| No saltarse nunca los iguales | Sí |
| Informar como cambio un valor grande reescrito igual | No al principio. Se agregó el caso |
| No dar por perdida una posición anterior a la historia | Sí |
| Bajar primero sólo por un lado cuando los niveles coinciden | No: da el mismo resultado en un paso más |

## Lo que cuesta

Una tabla de 20 000 filas; cada commit actualiza 20 filas repartidas por la tabla. `ChangeRetention = 100` cuando el feed está encendido.

### Latencia de escritura

| | Feed apagado | Encendido | Encendido y con un consumidor |
| --- | ---: | ---: | ---: |
| 200 commits durables en archivo | 1 896 ms | 1 913 ms | 1 848 ms |
| 2 000 commits en memoria | 104,6 ms | 119,0 ms | 120,4 ms |

- **En archivo no se mueve.** Cada commit cuesta unos 9,5 ms, casi todo fsync, y las tres medidas difieren menos que su desviación (entre 30 y 75 ms).
- **En memoria, conservar commits cuesta un 14 %:** unos 7 µs más por commit, sobre 52 µs. Es anotar la historia en el catálogo y dejar de reutilizar páginas.
- **El consumidor no le cuesta nada al escritor** en ninguno de los dos casos.

### El consumidor

| Carga | Cambios que vio | Veces que quedó cortado |
| --- | ---: | ---: |
| 200 commits durables en archivo | 4 000 de 4 000 | 0 |
| 2 000 commits en memoria, sin pausa | 19 700 de 40 000 | 10 |

Con commits durables el consumidor va al paso del escritor. Contra un escritor en memoria que no para, no: procesa más o menos la mitad de lo que el escritor produce, queda cien commits atrás y se le corta. La fila "con un consumidor" de la tabla de latencia en memoria mide, por tanto, a un consumidor que fue cortado diez veces.

### Espacio

El archivo después de cargar 20 000 filas y hacer 2 000 commits de 20 actualizaciones:

| `ChangeRetention` | Tamaño del archivo |
| ---: | ---: |
| 0 (apagado) | 4 MiB |
| 10 | 8 MiB |
| 100 | 32 MiB |
| 1 000 | 256 MiB |

El espacio crece con el número de commits conservados: cada uno retiene las páginas que el siguiente reescribió (aquí, unas 20 hojas y sus caminos). El archivo crece duplicándose, así que estos tamaños son el siguiente escalón por encima de lo que se usa, no la cifra exacta. Pasado el horizonte las páginas vuelven a usarse: con 8 commits conservados el archivo no crece entre el commit 60 y el 300 (`ChangeFeedTests`).

## Límites conocidos

- **Los cambios de un commit se leen enteros a memoria** antes de entregar el primero. Un commit que cambia un millón de filas son un millón de objetos.
- **El consumidor es más lento que un escritor sin fsync.** No se optimizó.
- **Efecto neto, no cada paso.** Quien necesite cada escritura intermedia de una transacción no la tiene.
- **El horizonte es un número de commits**, no tiempo ni espacio. Mil commits grandes retienen mucho más que mil chicos.
- **No hay diagnóstico de consumidores atrasados ni de snapshots viejos.** El plan lo pide junto con el de las transacciones de lectura; los dos quedan para la Fase 10.4.
- **Un feed por tabla.** `WatchAsync<T>` informa una tabla; quien observa varias abre uno por cada una, y cada uno compara los mismos commits.
- **La posición debe guardarla el consumidor.** La base no recuerda hasta dónde leyó cada quien.
- **Las tablas que cambian de índices o restricciones no generan cambios**: sólo las filas.

## Verificación

| Dónde | Qué | Resultado |
| --- | --- | --- |
| macOS (arm64) | `dotnet test -c Release` | 516 pasan, 3 explícitas omitidas |
| macOS (arm64) | Smoke de Native AOT, leyendo el feed desde el principio en una base reabierta | Pasa |
| Linux (arm64, contenedor) | `scripts/test-linux.sh`: build, las mismas pruebas y el smoke de AOT | 516 pasan, 3 omitidas; smoke pasa |
| Windows | — | Sin verificar |

Los benchmarks sólo se corrieron en macOS.
